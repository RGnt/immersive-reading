"""Shared task execution for both commands and Python callers."""
from dataclasses import dataclass, field
from pathlib import Path
from xml.etree.ElementTree import ParseError
from zipfile import BadZipFile

from .enrichment import enrich_book, write_reader_data, EnrichmentReport
from .validation import load_json, validate_reader
from .jobs import CancelledError, check_cancelled
from .publication import Publication, PublicationError, PublicationConflict, canonical_root, file_info, read_manifest, snapshot_root

INPUT_ERRORS = (ValueError, OSError, BadZipFile, ParseError)


@dataclass(frozen=True)
class BuildOptions:
    mode: str = "reader"
    html: bool = False
    pdf: bool = False
    cards: bool = False
    package: bool = False
    validate: bool = True
    update_decks: bool = False
    previews: bool = True

    def check(self):
        if self.mode not in {"reader", "study", "decks"}:
            raise ValueError(f"Unknown pipeline mode: {self.mode}")
        if self.mode != "study" and any((self.html, self.pdf, self.cards, self.package)):
            raise ValueError("Document, card and package exports require study mode")
        if self.mode == "reader" and self.update_decks:
            raise ValueError("reader-only cannot be combined with deck updates")


@dataclass
class BookResult:
    book_id: str
    title: str
    data_directory: Path | None
    html_directory: Path | None = None
    pdf_directory: Path | None = None
    diagnostics: list = field(default_factory=list)
    deck_updates: list = field(default_factory=list)
    counts: dict = field(default_factory=dict)
    reader_generation: str | None = None
    study_directory: Path | None = None
    study_generation: str | None = None
    export_status: str = "not_requested"
    export_error: str | None = None
    cache_status: str = "disabled"
    enrichment_seconds: dict = field(default_factory=dict)


@dataclass
class JobResult:
    books: list[BookResult]
    archive: Path | None = None
    manifest: Path | None = None
    generation: str | None = None
    status: str = "complete"
    errors: list = field(default_factory=list)
    warnings: list = field(default_factory=list)


class ExportFailure(ValueError):
    def __init__(self, result):
        self.result = result
        details = "; ".join(e["stage"]+": "+e["message"] for e in result.errors)
        super().__init__(f"Reader snapshots published; supplementary exports failed: {details}")


def _source_stamps(folder, book, cancel):
    stamps = {}
    names = {p.name for pattern in ("*.epub", "*.csv") for p in folder.glob(pattern)}
    if names != {source["file"] for source in book["source_files"]}:
        raise PublicationConflict(f"Input file set changed during enrichment: {folder}")
    for source in book["source_files"]:
        path = folder / source["file"]
        info = file_info(path, cancel)
        if info["sha256"] != source["sha256"]:
            raise PublicationConflict(f"Input changed during enrichment: {path}")
        stamps[path] = info
    config = folder / "study.config.json"
    if (load_json(config) if config.exists() else {}) != book.get("build_inputs", {}).get("book_config", {}):
        raise PublicationConflict(f"Configuration changed during enrichment: {config}")
    stamps[config] = file_info(config, cancel) if config.exists() else None
    from .provenance import LEDGER
    ledger = folder / LEDGER
    stamps[ledger] = file_info(ledger, cancel) if ledger.exists() else None
    return stamps


def run_job(books, output, resources, *, book_folder=None, options=None, font=None,
            kanjidic_path=None, kanjivg_archive=None, on_event=None, cancel=None, yomitan_paths=None, worker=None, cache=None):
    """Stage work privately, then publish one atomic immutable manifest.

    Document failures publish independently completed reader snapshots and
    retain the last study snapshot. Cancellation discards uncommitted output.
    Explicit source-CSV updates are a separate, non-rollback input operation.
    """
    options = options or BuildOptions()
    options.check()
    check_cancelled(cancel)
    from .resources import resource_worker
    with resource_worker(resources, worker=worker, cache=cache, font=font, kanjidic_path=kanjidic_path,
                         kanjivg_archive=kanjivg_archive, yomitan_paths=yomitan_paths) as active:
        return _run_job(books, output, active, book_folder=book_folder, options=options, on_event=on_event, cancel=cancel, cache=cache)


def _run_job(books, output, worker, *, book_folder, options, on_event, cancel, cache):
    books, output = (Path(p).resolve() for p in (books, output))
    store, bundle = worker.store, worker.store.bundle
    store.reader_manifest(cancel=cancel)
    output = canonical_root(output)
    base = read_manifest(output) if options.mode != "decks" else None
    selected_dictionaries = bundle.yomitan
    archive = bundle.kanjivg
    folders = [Path(book_folder).resolve()] if book_folder is not None else sorted(
        p for p in books.iterdir() if p.is_dir() and list(p.glob("*.epub")))
    if not folders:
        raise ValueError(f"No EPUB book folders found in {books}. Put an EPUB and its CSV deck in books/<book name>/.")
    def emit(stage, **details):
        check_cancelled(cancel)
        if on_event is not None:
            on_event(dict(stage=stage, **details))
    loaded, ids = [], set()
    for folder in folders:
        emit("enrichment", folder=str(folder))
        enrichment_report = EnrichmentReport()
        book = enrich_book(folder, store, validate=options.validate, cancel=cancel, worker=worker, cache=cache, report=enrichment_report)
        emit("cache", folder=str(folder), status=enrichment_report.cache_status, seconds=enrichment_report.seconds)
        if (options.mode == "decks" or options.update_decks) and not list(folder.glob("*.csv")):
            raise ValueError(f"{folder}: deck updates require a source CSV; Yomitan ZIPs are read-only")
        diagnostics = (enrichment_report.diagnostics if enrichment_report.validated else validate_reader(book)) if options.validate else []
        if book["id"] in ids:
            raise PublicationError(f"Duplicate EPUB/book identity in the selected input folders: {book['id']}")
        ids.add(book["id"])
        result = BookResult(book["id"], book["title"], None, diagnostics=diagnostics,
                            cache_status=enrichment_report.cache_status, enrichment_seconds=enrichment_report.seconds,
                            counts={"chapters": len(book["chapters"]), "vocabulary": len(book["vocabulary"]),
                                    "matched": book["quality"]["vocabulary_matched"],
                                    "unmatched": len(book["quality"]["unmatched_vocabulary_ids"]), "kanji": len(book["kanji"]),
                                    "grammar_candidates": len(book["grammar"])})
        loaded.append((folder, book, result))
    # Validate all selected inputs before any explicitly requested CSV update.
    if options.mode == "decks" or options.update_decks:
        store.verify_current(paths=store.reader_files(), cancel=cancel)
    for folder, book, result in loaded:
        check_cancelled(cancel)
        if options.mode == "decks" or options.update_decks:
            from .deck_enrichment import enrich_decks
            result.deck_updates = enrich_decks(book, folder)
            emit("deck_update", book_id=book["id"], reports=result.deck_updates)
    if options.mode == "decks":
        return JobResult([r for _, _, r in loaded])
    resource_files = set(store.reader_files())
    stamps = {}
    for folder, book, _ in loaded:
        stamps.update(_source_stamps(folder, book, cancel))
        for path, expected in zip(selected_dictionaries, book.get("build_inputs", {}).get("yomitan", [])):
            actual = file_info(path, cancel)
            if actual["sha256"] != expected["sha256"]:
                raise PublicationConflict(f"Dictionary changed during enrichment: {path}")
            stamps[path] = actual
    def unchanged_inputs():
        for path, expected in stamps.items():
            check_cancelled(cancel)
            actual = file_info(path, cancel) if path.exists() else None
            if actual != expected:
                raise PublicationConflict(f"Input changed while the build was staged: {path}")
        for folder, book, _ in loaded:
            names = {p.name for pattern in ("*.epub", "*.csv") for p in folder.glob(pattern)}
            if names != {source["file"] for source in book["source_files"]}:
                raise PublicationConflict(f"Input file set changed while the build was staged: {folder}")
        store.verify_current(paths=resource_files, cancel=cancel)
    errors, prepared_by_book = [], {}
    with Publication(output, base=base, cancel=cancel) as publication:
        for folder, book, result in loaded:
            bid = book["id"]
            emit("staging", book_id=bid)
            reader = publication.book_root(bid, "reader")
            write_reader_data(book, reader)
            publication.add(bid, book["title"], "reader", validated=options.validate)
            emit("reader_complete", book_id=bid)
            documents = options.mode == "study" and (options.html or options.pdf)
            if options.mode == "study" and any((options.html, options.pdf, options.cards)):
                try:
                    from .study_data import prepare_study_data
                    if documents:
                        from .kanjivg import add_stroke_order
                        add_stroke_order(book, archive, cancel=cancel, store=store)
                    study = prepare_study_data(book, include_context=documents, cancel=cancel, worker=worker)
                    prepared_by_book[bid] = study
                    if documents:
                        from .render_outputs import render_book
                        book["build_inputs"]["print_resources"] = store.print_manifest(pdf=options.pdf, cancel=cancel)
                        stage = publication.book_root(bid, "study")
                        emit("documents", book_id=bid)
                        render_book(book, stage, stage / "pdf", str(bundle.font) if bundle.font else None,
                                    not options.pdf, archive, study=study, include_html=options.html, cancel=cancel,
                                    resources=bundle, resource_store=store)
                        if options.validate:
                            from .verification import verify
                            emit("verification", book_id=bid)
                            verify(stage, book_ids=[bid], html=options.html, pdf=options.pdf, previews=options.previews, cancel=cancel)
                        publication.add(bid, book["title"], "study", validated=options.validate,
                                        capabilities={"html": options.html, "pdf": options.pdf})
                        resource_files.update(store.print_files(pdf=options.pdf))
                        publication.updates[bid]["export_error"] = None
                        result.export_status = "complete"
                except CancelledError:
                    raise
                except Exception as error:
                    check_cancelled(cancel)
                    result.export_status, result.export_error = "failed", str(error)
                    failure = {"book_id": bid, "stage": "documents" if documents else "card_preparation",
                               "type": type(error).__name__, "message": str(error)}
                    errors.append(failure)
                    publication.updates[bid]["export_error"] = failure
                    # A partial document tree is never listed in the manifest.
                    publication.discard_study(bid)
        emit("publication")
        manifest, warnings = publication.publish(cards=options.cards, package=options.package,
                                                 prepared_by_book=prepared_by_book, precommit=unchanged_inputs,
                                                 status="partial" if errors else "complete")
        errors.extend(manifest["export_errors"])
    for _, book, result in loaded:
        published = manifest["books"][book["id"]]
        result.data_directory = snapshot_root(output, published["reader"]) / "data" / book["id"]
        result.reader_generation = published["reader"]["generation"]
        if published.get("study"):
            study = published["study"]
            result.study_directory = snapshot_root(output, study)
            result.study_generation = study["generation"]
            if result.export_status == "complete":
                result.html_directory = result.study_directory / "html" / book["id"] if options.html else None
                result.pdf_directory = result.study_directory / "pdf" / book["id"] if options.pdf else None
        # Publication is committed: cancellation/callback errors cannot roll it back.
        if on_event is not None:
            try:
                on_event({"stage": "book_complete", "book_id": book["id"], "title": book["title"],
                          "path": str(result.html_directory or result.data_directory), "export_status": result.export_status})
            except Exception as error:
                warnings.append({"code": "progress_callback_failed_after_commit", "message": str(error)})
    packaged = output / manifest["shared"]["archive"]["path"] if options.package and manifest["shared"].get("archive", {}).get("path", "").startswith(f".publication/releases/{manifest['generation']}/") else None
    final = JobResult([r for _, _, r in loaded], packaged, output / f".publication/releases/{manifest['generation']}/manifest.json",
                      manifest["generation"], "partial" if errors else "complete", errors, warnings)
    if on_event is not None:
        for warning in tuple(warnings):
            try:
                on_event({"stage": "publication_warning", **warning})
            except Exception as error:
                warnings.append({"code": "progress_callback_failed_after_commit", "message": str(error)})
        try:
            on_event({"stage": "complete", "archive": str(packaged) if packaged else None, "status": final.status})
        except Exception as error:
            warnings.append({"code": "progress_callback_failed_after_commit", "message": str(error)})
    return final
