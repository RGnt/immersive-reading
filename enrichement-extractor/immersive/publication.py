"""Immutable per-book snapshots, atomic selection and manifest-owned views."""
import copy
import hashlib
import json
import os
import re
import shutil
import tempfile
import time
import uuid
from contextlib import AbstractContextManager
from html import escape
from pathlib import Path, PurePosixPath

from .jobs import CancelledError, check_cancelled
from .validation import load_json

BOOK_ID = re.compile(r"book-[a-f0-9]{16}\Z")
GENERATION = re.compile(r"[a-f0-9]{32}\Z")
CORE_FILES = ("reader.json", "quality.json", "lookup.json")
_AUTOMATIC_BASE = object()


class PublicationError(ValueError):
    pass


class PublicationConflict(PublicationError):
    pass


def _normal_namespace(path):
    """Windows realpath can retain its extended prefix during mkdir races."""
    text = str(path)
    if os.name == "nt":
        if text.upper().startswith("\\\\?\\UNC\\"):
            text = "\\\\" + text[8:]
        elif text.startswith("\\\\?\\") and len(text) > 6 and text[4].isascii() and text[4].isalpha() and text[5:7] == ":\\":
            text = text[4:]
    return Path(text)


def canonical_root(path):
    return _normal_namespace(Path(path).resolve())


def relative_name(name):
    if not isinstance(name, str) or not name or "\\" in name or any(c in name for c in ':*?"<>|'):
        raise PublicationError(f"Unsafe manifest path: {name!r}")
    path = PurePosixPath(name)
    if path.is_absolute() or any(p in {"", ".", ".."} for p in name.split("/")):
        raise PublicationError(f"Unsafe manifest path: {name!r}")
    return path


def contained(root, name):
    """Reject traversal and links/junctions before any write or cleanup."""
    root = canonical_root(root)
    path = root
    for part in relative_name(name).parts:
        path = path / part
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise PublicationError(f"Linked paths are not publication targets: {path}")
    resolved = canonical_root(path)
    if not resolved.is_relative_to(root):
        raise PublicationError(f"Manifest path escaped its root: {name} ({resolved} outside {root})")
    return path


def file_info(path, cancel=None):
    digest, size = hashlib.sha256(), 0
    with Path(path).open("rb") as stream:
        while chunk := stream.read(1024*1024):
            check_cancelled(cancel)
            digest.update(chunk)
            size += len(chunk)
    return {"sha256": digest.hexdigest(), "size": size}


def inventory(root, cancel=None):
    files, folded = {}, set()
    for path in sorted(Path(root).rglob("*")):
        check_cancelled(cancel)
        name = path.relative_to(root).as_posix()
        checked = contained(root, name)
        if checked.is_file():
            if name.casefold() in folded:
                raise PublicationError(f"Case-colliding output paths: {name}")
            folded.add(name.casefold())
            files[name] = file_info(checked, cancel)
    return files


def atomic_bytes(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(value)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def atomic_json(path, value):
    atomic_bytes(path, (json.dumps(value, ensure_ascii=False, indent=2)+"\n").encode("utf-8"))


def atomic_copy(source, destination, expected):
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        digest, size = hashlib.sha256(), 0
        with Path(source).open("rb") as stream, tempfile.NamedTemporaryFile(dir=destination.parent, suffix=".tmp", delete=False) as target:
            temporary = Path(target.name)
            while chunk := stream.read(1024*1024):
                digest.update(chunk)
                size += len(chunk)
                target.write(chunk)
            target.flush()
            os.fsync(target.fileno())
        if {"sha256": digest.hexdigest(), "size": size} != expected:
            raise PublicationError("Snapshot changed while refreshing its view")
        os.replace(temporary, destination)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def read_manifest(output):
    output = canonical_root(output)
    pointer = contained(output, ".publication/active.json")
    if not pointer.exists():
        return None
    active = load_json(pointer)
    if not isinstance(active, dict):
        raise PublicationError("Invalid active publication pointer")
    generation = active.get("generation", "")
    if not GENERATION.fullmatch(generation) or active.get("schema_version") != 1:
        raise PublicationError("Invalid active publication pointer")
    expected = f".publication/releases/{generation}/manifest.json"
    if active.get("manifest") != expected:
        raise PublicationError("Active publication has an invalid manifest location")
    path = contained(output, expected)
    if file_info(path)["sha256"] != active.get("sha256"):
        raise PublicationError("Publication manifest checksum mismatch")
    manifest = load_json(path)
    if not isinstance(manifest, dict) or manifest.get("generation") != generation or manifest.get("schema_version") != 1:
        raise PublicationError("Publication manifest does not match its pointer")
    if not isinstance(manifest.get("books"), dict):
        raise PublicationError("Invalid published book inventory")
    for bid, book in manifest["books"].items():
        if not BOOK_ID.fullmatch(bid) or not isinstance(book, dict) or "reader" not in book:
            raise PublicationError("Invalid published book identity")
        for kind in ("reader", "study"):
            if kind in book:
                snapshot_root(output, book[kind])
                for name in book[kind]["files"]:
                    relative_name(name)
    for field in ("views", "retired", "history"):
        for name in manifest.get(field, {}):
            _view_name(name)
    return manifest


def snapshot_root(output, record):
    name = record["root"]
    parts = relative_name(name).parts
    if len(parts) < 4 or parts[:2] != (".publication", "releases") or not GENERATION.fullmatch(parts[2]):
        raise PublicationError("Snapshot is outside the immutable release store")
    return contained(output, name)


def reader_paths(output, manifest=None):
    manifest = manifest if manifest is not None else read_manifest(output)
    if manifest is None:
        return sorted((Path(output) / "data").glob("book-*/reader.json"))
    return [snapshot_root(output, book["reader"]) / "data" / bid / "reader.json"
            for bid, book in sorted(manifest["books"].items())]


def verify_record(output, record, *, cancel=None):
    root = snapshot_root(output, record)
    for name, expected in record["files"].items():
        if file_info(contained(root, name), cancel) != expected:
            raise PublicationError(f"Published snapshot checksum mismatch: {name}")


class PublicationLock(AbstractContextManager):
    """OS-owned process lock; a crashed process cannot leave a stale lock."""
    def __init__(self, output, *, cancel=None, timeout=60, lock_name=".publication/publish.lock"):
        self.output, self.cancel, self.timeout = Path(output), cancel, timeout
        self.lock_name = lock_name
        self.stream = None

    def __enter__(self):
        check_cancelled(self.cancel)
        path = contained(self.output, self.lock_name)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.stream = path.open("a+b", buffering=0)
        start = time.monotonic()
        while True:
            try:
                check_cancelled(self.cancel)
                self.stream.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(self.stream.fileno(), msvcrt.LK_NBLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(self.stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                # Initialize only after locking. Two first-time writers must
                # never race to write the lock byte before owning its region.
                self.stream.seek(0, 2)
                if not self.stream.tell():
                    self.stream.write(b"0")
                    self.stream.flush()
                return self
            except OSError:
                if time.monotonic()-start >= self.timeout:
                    self.stream.close()
                    raise PublicationError("Timed out waiting for another publication job")
                time.sleep(.05)
            except BaseException:
                self.stream.close()
                raise

    def __exit__(self, *args):
        if self.stream is not None:
            try:
                self.stream.seek(0)
                if os.name == "nt":
                    import msvcrt
                    msvcrt.locking(self.stream.fileno(), msvcrt.LK_UNLCK, 1)
                else:
                    import fcntl
                    fcntl.flock(self.stream.fileno(), fcntl.LOCK_UN)
            finally:
                self.stream.close()


def _cleanup_abandoned(output):
    """Caller holds the publication lock; active producer leases are skipped."""
    removed = []
    parent = contained(output, ".publication/staging")
    if not parent.exists():
        return removed
    for stage in parent.iterdir():
        if not GENERATION.fullmatch(stage.name):
            continue
        stage = contained(output, f".publication/staging/{stage.name}")
        marker = stage / "job.json"
        if not marker.is_file():
            continue
        try:
            job = load_json(marker)
            if job != {"schema_version": 1, "generation": stage.name}:
                continue
            with PublicationLock(stage, timeout=0, lock_name=".lease.lock"):
                pass
        except (OSError, PublicationError, ValueError):
            continue
        shutil.rmtree(stage)
        removed.append({"code": "abandoned_staging_removed", "generation": stage.name})
    return removed


def _view_name(name):
    parts = relative_name(name).parts
    if parts[0] not in {"data", "html", "pdf", "previews", "reports"} and name != "study-pack.zip":
        raise PublicationError(f"Not an output view: {name}")
    return name


class Publication(AbstractContextManager):
    def __init__(self, output, *, base=_AUTOMATIC_BASE, cancel=None):
        self.output = canonical_root(output)
        self.base = read_manifest(self.output) if base is _AUTOMATIC_BASE else base
        self.cancel = cancel
        self.generation = uuid.uuid4().hex
        self.prefix = f".publication/releases/{self.generation}"
        self.stage_name = f".publication/staging/{self.generation}"
        self.stage = contained(self.output, self.stage_name)
        self.release = contained(self.output, self.prefix)
        self.updates = {}
        self.committed = False
        self.lease = None
        self.recovered = []

    def __enter__(self):
        check_cancelled(self.cancel)
        with PublicationLock(self.output, cancel=self.cancel):
            self.recovered = _cleanup_abandoned(self.output)
            self.stage.mkdir(parents=True, exist_ok=False)
            try:
                atomic_json(self.stage / "job.json", {"schema_version": 1, "generation": self.generation})
                self.lease = PublicationLock(self.stage, cancel=self.cancel, timeout=0, lock_name=".lease.lock")
                self.lease.__enter__()
            except BaseException:
                shutil.rmtree(self.stage)
                raise
        return self

    def __exit__(self, *args):
        if self.lease is not None:
            self.lease.__exit__(*args)
            self.lease = None
        # Never delete a sealed release: an interrupted atomic pointer write
        # may have committed even if its caller observed an exception.
        for name in (self.stage_name,):
            if name:
                path = contained(self.output, name)
                if path.exists():
                    shutil.rmtree(path)

    def book_root(self, bid, kind):
        if not BOOK_ID.fullmatch(bid) or kind not in {"reader", "study"}:
            raise PublicationError("Invalid book snapshot identity")
        path = contained(self.stage, f"books/{bid}/{kind}")
        path.mkdir(parents=True, exist_ok=True)
        return path

    def add(self, bid, title, kind, *, validated, capabilities=None, error=None):
        root = self.book_root(bid, kind)
        files = inventory(root, self.cancel)
        prefixes = (f"data/{bid}/",) if kind == "reader" else tuple(f"{category}/{bid}/" for category in ("data", "html", "pdf", "previews"))
        if any(not name.startswith(prefixes) and not (kind == "study" and name == "reports/verification.json") for name in files):
            raise PublicationError(f"Snapshot contains output belonging to another book: {bid}")
        for filename in CORE_FILES:
            if f"data/{bid}/{filename}" not in files:
                raise PublicationError(f"Incomplete {kind} snapshot for {bid}: missing {filename}")
        record = {"generation": self.generation, "root": f"{self.prefix}/books/{bid}/{kind}",
                  "files": files, "validated": validated, "capabilities": capabilities or {}}
        if kind == "study":
            record["reader_generation"] = self.generation
            if capabilities.get("html") and f"html/{bid}/index.html" not in files:
                raise PublicationError(f"Missing HTML export for {bid}")
            if capabilities.get("pdf") and not any(n.startswith(f"pdf/{bid}/") for n in files):
                raise PublicationError(f"Missing PDF export for {bid}")
        book = self.updates.setdefault(bid, {"title": title})
        book[kind] = record
        if error:
            book["export_error"] = error
        return record

    def discard_study(self, bid):
        if not BOOK_ID.fullmatch(bid):
            raise PublicationError("Invalid book identity")
        path = contained(self.stage, f"books/{bid}/study")
        if path.exists():
            shutil.rmtree(path)
        self.updates[bid].pop("study", None)

    def source(self, record, name):
        root = snapshot_root(self.output, record)
        relative_name(name)
        if root.is_relative_to(self.release):
            root = self.stage / root.relative_to(self.release)
        path = contained(root, name)
        if file_info(path, self.cancel) != record["files"][name]:
            raise PublicationError(f"Published artifact changed: {path}")
        return path

    def _shared(self, relative, value):
        path = contained(self.stage, relative)
        if isinstance(value, bytes):
            atomic_bytes(path, value)
        else:
            atomic_json(path, value)
        return {"path": f"{self.prefix}/{relative}", **file_info(path, self.cancel)}

    def _shared_source(self, record):
        parts = relative_name(record["path"]).parts
        if len(parts) < 4 or parts[:2] != (".publication", "releases") or not GENERATION.fullmatch(parts[2]):
            raise PublicationError("Shared artifact is outside the release store")
        path = contained(self.output, record["path"])
        if path.is_relative_to(self.release):
            path = self.stage / path.relative_to(self.release)
        if file_info(path, self.cancel) != {k: record[k] for k in ("sha256", "size")}:
            raise PublicationError(f"Shared artifact changed: {path}")
        return path

    def _legacy(self):
        """Adopt only recognizable generated files, never sweep arbitrary files."""
        books, views, shared, ignored = {}, {}, {}, []
        report_path = contained(self.output, "reports/verification.json")
        try:
            old_reports = load_json(report_path) if report_path.is_file() else []
        except ValueError:
            old_reports = []
        from .validation import validate_reader
        for path in sorted((self.output / "data").glob("book-*/reader.json")):
            check_cancelled(self.cancel)
            bid = path.parent.name
            if not BOOK_ID.fullmatch(bid):
                continue
            try:
                path = contained(self.output, path.relative_to(self.output).as_posix())
                book = load_json(path)
                validate_reader(book)
                if book["id"] != bid or any(not path.with_name(n).is_file() for n in CORE_FILES):
                    raise PublicationError(f"Incomplete legacy reader bundle: {bid}")
                from .verification import verify
                verify(self.output, book_ids=[bid], html=False, pdf=False, write_report=False, cancel=self.cancel)
            except (ValueError, OSError) as error:
                ignored.append({"code": "legacy_bundle_not_adopted", "book_id": bid, "message": str(error)})
                continue  # Unknown/incomplete legacy output stays unowned.
            roots = {}
            for kind in ("reader", "study"):
                root = contained(self.stage, f"legacy/{bid}/{kind}")
                root.mkdir(parents=True, exist_ok=True)
                for filename in CORE_FILES:
                    name = f"data/{bid}/{filename}"
                    source = contained(self.output, name)
                    target = contained(root, name)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(source, target)
                    views[name] = file_info(source, self.cancel)
                roots[kind] = root
            chapters = {c["id"] for c in book["chapters"]}
            svg_names = {PurePosixPath(k["stroke_order"]["asset"]).name for k in book["kanji"] if k.get("stroke_order")}
            html = (self.output / "html" / bid / "index.html").is_file()
            pdf = (self.output / "pdf" / bid / "kanji-master.pdf").is_file()
            for category in ("html", "pdf", "previews", "data"):
                source_root = self.output / category / bid
                if not source_root.exists():
                    continue
                for source in sorted(source_root.rglob("*")):
                    name = source.relative_to(self.output).as_posix()
                    source = contained(self.output, name)
                    if not source.is_file():
                        continue
                    local = source.relative_to(source_root).as_posix()
                    recognized = ((category == "html" and (local in {"index.html", "dictionary.html", "vocabulary.html", "grammar.html", "kanji.html", "review.html", "assets/style.css", "assets/lookup.js"} or re.fullmatch(r"chapters/chapter-\d+-(kanji|vocabulary)\.html", local)))
                                  or (category == "pdf" and re.fullmatch(r"(chapter-\d+-(kanji|vocabulary)|(kanji|vocabulary|grammar)-master)\.pdf", local))
                                  or (category == "previews" and re.fullmatch(r"book-[a-f0-9]{16}-contact-\d+\.png", local))
                                  or (category == "data" and (local in {*CORE_FILES, "kanji-context.json", "vocabulary-sets.json"} or re.fullmatch(r"assets/kanjivg/([a-f0-9]+\.svg|ATTRIBUTION\.txt)", local) or re.fullmatch(r"print/(chapter-\d+-(kanji|vocabulary)|(kanji|vocabulary|grammar)-master)\.(layout|sources|vocabulary)\.json", local))))
                    if not recognized:
                        continue
                    views[name] = file_info(source, self.cancel)
                    chapter_match = re.search(r"chapter-\d+", local)
                    if chapter_match and chapter_match.group() not in chapters:
                        continue  # Owned obsolete legacy chapter: retire its view.
                    if category == "previews":
                        continue  # Cannot establish which legacy pages a contact sheet depicts.
                    if category == "data" and local.startswith("assets/kanjivg/"):
                        if source.name not in svg_names and source.name != "ATTRIBUTION.txt":
                            continue
                        reader_target = contained(roots["reader"], name)
                        reader_target.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copyfile(source, reader_target)
                    target = contained(roots["study"], name)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(source, target)
            if isinstance(old_reports, list):
                reports = [r for r in old_reports if isinstance(r, dict) and r.get("book_id") == bid]
                if reports:
                    atomic_json(roots["study"] / "reports/verification.json", reports)
            record = lambda kind: {"generation": self.generation, "root": f"{self.prefix}/legacy/{bid}/{kind}",
                                   "files": inventory(roots[kind], self.cancel), "validated": kind == "reader", "legacy": True,
                                   "capabilities": {"html": html, "pdf": pdf} if kind == "study" else {}}
            books[bid] = {"title": book["title"], "reader": record("reader")}
            if html or pdf:
                books[bid]["study"] = dict(record("study"), reader_generation=self.generation)
        for name in ("html/index.html", "data/flashcards/library.json", "study-pack.zip", "reports/verification.json"):
            path = contained(self.output, name)
            if path.is_file():
                if name == "data/flashcards/library.json":
                    try:
                        cards = load_json(path)
                        if not isinstance(cards, dict) or cards.get("schema_version") != "1.0" or not isinstance(cards.get("books"), list) or not all(b.get("id") in books for b in cards["books"]):
                            continue
                    except (ValueError, AttributeError):
                        continue
                views[name] = file_info(path, self.cancel)
                kind = {"data/flashcards/library.json": "cards", "study-pack.zip": "archive"}.get(name)
                if kind:
                    target = contained(self.stage, "legacy/shared/"+name)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(path, target)
                    shared[kind] = {"path": f"{self.prefix}/legacy/shared/{name}", **views[name], "legacy": True}
        return {"books": books, "shared": shared, "views": views, "history": {}, "retired": {}, "migration_warnings": ignored}

    def _check_conflicts(self, current):
        for bid, update in self.updates.items():
            for kind in ("reader", "study"):
                if kind not in update:
                    continue
                before = (self.base or {}).get("books", {}).get(bid, {}).get(kind)
                now = current["books"].get(bid, {}).get(kind)
                if before is None and now and now.get("legacy"):
                    continue
                if before != now:
                    raise PublicationConflict(f"Another job published {kind} data for {bid}; rebuild against the current snapshot")

    def _library(self, books, start):
        from .library import library_page
        rows = []
        for bid, book in sorted(books.items()):
            study = book.get("study")
            if study and study["capabilities"].get("html"):
                destination = snapshot_root(self.output, study) / "html" / bid / "index.html"
                href = os.path.relpath(destination, start).replace("\\", "/")
                rows.append((book["title"], href))
        return library_page(rows).encode("utf-8")

    def _views(self, state):
        views = {}
        for bid, book in state["books"].items():
            for kind in ("reader", "study"):
                record = book.get(kind)
                if not record:
                    continue
                for name, info in record["files"].items():
                    if kind == "study" and (name.startswith("html/") or name.startswith("reports/") or name in {f"data/{bid}/{f}" for f in CORE_FILES}):
                        continue
                    views[_view_name(name)] = {"source": record["root"]+"/"+name, **info}
            study = book.get("study")
            if study and study["capabilities"].get("html"):
                target = snapshot_root(self.output, study) / "html" / bid / "index.html"
                href = escape(os.path.relpath(target, self.output / "html" / bid).replace("\\", "/"))
                redirect = f'<!doctype html><meta charset="utf-8"><meta http-equiv="refresh" content="0;url={href}"><a href="{href}">Open completed study pages</a>'
                shared = self._shared(f"shared/views/html/{bid}/index.html", redirect.encode("utf-8"))
                views[f"html/{bid}/index.html"] = {"source": shared["path"], **{k: shared[k] for k in ("sha256", "size")}}
        if any(b.get("study", {}).get("capabilities", {}).get("html") for b in state["books"].values()):
            shared = self._shared("shared/views/html/index.html", self._library(state["books"], self.output / "html"))
            views["html/index.html"] = {"source": shared["path"], **{k: shared[k] for k in ("sha256", "size")}}
        for kind, name in (("cards", "data/flashcards/library.json"), ("archive", "study-pack.zip"), ("report", "reports/verification.json")):
            if kind in state["shared"]:
                record = state["shared"][kind]
                views[name] = {"source": record["path"], **{k: record[k] for k in ("sha256", "size")}}
        return views

    def publish(self, *, cards=False, package=False, prepared_by_book=None, precommit=None, status="complete"):
        check_cancelled(self.cancel)
        with PublicationLock(self.output, cancel=self.cancel):
            previous = read_manifest(self.output)
            current = previous or self._legacy()
            self._check_conflicts(current)
            state = copy.deepcopy(current)
            for bid, update in self.updates.items():
                state["books"].setdefault(bid, {}).update(update)
            if not state["books"]:
                raise PublicationError("No completed reader bundles to publish or package")
            state.update(schema_version=1, generation=self.generation, status=status, export_errors=[])
            state.pop("views", None)
            if cards:
                from .card_export import export
                target = contained(self.stage, "shared/data/flashcards/library.json")
                paths = [self.source(b["reader"], f"data/{bid}/reader.json") for bid, b in sorted(state["books"].items())]
                try:
                    export(self.output, target, prepared_by_book=prepared_by_book, reader_paths=paths, announce=False, cancel=self.cancel)
                    state["shared"]["cards"] = {"path": f"{self.prefix}/shared/data/flashcards/library.json", **file_info(target, self.cancel),
                                                   "inputs": {bid: b["reader"]["generation"] for bid, b in state["books"].items()}}
                except (CancelledError, PublicationError):
                    raise
                except Exception as error:
                    target.unlink(missing_ok=True)
                    state["export_errors"].append({"stage": "cards", "type": type(error).__name__, "message": str(error)})
            reports = []
            for book in state["books"].values():
                study = book.get("study")
                if study and "reports/verification.json" in study["files"]:
                    reports.extend(load_json(self.source(study, "reports/verification.json")))
            state["shared"].pop("report", None)
            if reports:
                state["shared"]["report"] = self._shared("shared/reports/verification.json", reports)
            if package and status != "partial" and not state["export_errors"]:
                from .packaging import write_archive
                members = {}
                for bid, book in sorted(state["books"].items()):
                    record = book.get("study", book["reader"])
                    for name in record["files"]:
                        if not name.startswith("reports/"):
                            members[name] = (self.source(record, name), record["files"][name])
                    for name in book["reader"]["files"]:
                        archive_name = f"reader-data/{bid}/" + name.removeprefix(f"data/{bid}/")
                        members[archive_name] = (self.source(book["reader"], name), book["reader"]["files"][name])
                for key, name in (("cards", "data/flashcards/library.json"), ("report", "reports/verification.json")):
                    if key in state["shared"]:
                        record = state["shared"][key]
                        members[name] = (self._shared_source(record), {k: record[k] for k in ("sha256", "size")})
                if any(name.startswith("html/") for name in members):
                    from .library import library_page
                    rows = [(b["title"], bid+"/index.html") for bid, b in sorted(state["books"].items()) if b.get("study", {}).get("capabilities", {}).get("html")]
                    index = self._shared("shared/archive-index.html", library_page(rows).encode("utf-8"))
                    members["html/index.html"] = (self._shared_source(index), {k: index[k] for k in ("sha256", "size")})
                target = contained(self.stage, "shared/study-pack.zip")
                archive_manifest = {"schema_version": 1, "generation": self.generation,
                                    "books": {bid: {"reader_generation": b["reader"]["generation"],
                                                    "reader_data": f"reader-data/{bid}/reader.json",
                                                    "study_generation": b.get("study", {}).get("generation"),
                                                    "study_reader_generation": b.get("study", {}).get("reader_generation")}
                                              for bid, b in state["books"].items()},
                                    "card_inputs": state["shared"].get("cards", {}).get("inputs"),
                                    "files": {name: info for name, (_, info) in sorted(members.items())}}
                try:
                    write_archive(target, members, archive_manifest, cancel=self.cancel)
                    state["shared"]["archive"] = {"path": f"{self.prefix}/shared/study-pack.zip", **file_info(target, self.cancel)}
                except (CancelledError, PublicationError):
                    raise
                except Exception as error:
                    target.unlink(missing_ok=True)
                    state["export_errors"].append({"stage": "packaging", "type": type(error).__name__, "message": str(error)})
            if state["export_errors"]:
                state["status"] = "partial"
            views = self._views(state)
            history = state.setdefault("history", {})
            retired = state.setdefault("retired", {})
            for name, info in current.get("views", {}).items():
                hashes = set(history.get(name, [])) | {info["sha256"]}
                history[name] = sorted(hashes)
                if name not in views:
                    retired[name] = sorted(set(retired.get(name, [])) | hashes)
            for name, info in views.items():
                history[name] = sorted(set(history.get(name, [])) | {info["sha256"]})
                retired.pop(name, None)
            state["views"] = views
            state["release_files"] = {
                path.relative_to(self.stage).as_posix(): file_info(path, self.cancel)
                for path in self.stage.rglob("*") if path.is_file() and path.name not in {".lease.lock", "manifest.json"}}
            atomic_json(self.stage / "manifest.json", state)
            atomic_json(self.stage / "seal.json", {"generation": self.generation,
                                                   "manifest_sha256": file_info(self.stage / "manifest.json")["sha256"]})
            check_cancelled(self.cancel)
            if precommit:
                precommit()
            check_cancelled(self.cancel)
            self.release.parent.mkdir(parents=True, exist_ok=True)
            self.lease.__exit__(None, None, None)
            self.lease = None
            os.rename(self.stage, self.release)  # New UUID destination; never replace a nonempty directory.
            pointer = {"schema_version": 1, "generation": self.generation, "manifest": f"{self.prefix}/manifest.json",
                       "sha256": file_info(self.release / "manifest.json")["sha256"]}
            atomic_json(contained(self.output, ".publication/active.json"), pointer)
            self.committed = True  # Cancellation after this point cannot undo a committed snapshot.
            warnings = refresh_views(self.output, state)
            warnings.extend(self.recovered)
            if previous is None:
                warnings.extend(current.get("migration_warnings", []))
            return state, warnings


def refresh_views(output, manifest):
    """Repairable compatibility copies; the active immutable manifest is authoritative.

    Call while holding PublicationLock. Unowned or edited files are preserved.
    """
    output, warnings = canonical_root(output), []
    for name, hashes in manifest.get("retired", {}).items():
        try:
            path = contained(output, _view_name(name))
            if path.is_file():
                if file_info(path)["sha256"] not in hashes:
                    warnings.append({"code": "modified_obsolete_file_preserved", "path": name})
                    continue
                path.unlink()
                parent = path.parent
                while parent != output:
                    try:
                        parent.rmdir()
                    except OSError:
                        break
                    parent = parent.parent
        except (OSError, PublicationError) as error:
            warnings.append({"code": "view_cleanup_pending", "path": name, "message": str(error)})
    for name, record in manifest["views"].items():
        try:
            path = contained(output, _view_name(name))
            if path.exists():
                current = file_info(path)
                if current == {k: record[k] for k in ("sha256", "size")}:
                    continue
                if current["sha256"] not in manifest["history"].get(name, []):
                    warnings.append({"code": "unowned_or_modified_file_preserved", "path": name})
                    continue
            source = contained(output, record["source"])
            if file_info(source) != {k: record[k] for k in ("sha256", "size")}:
                raise PublicationError("Snapshot view checksum mismatch")
            atomic_copy(source, path, {k: record[k] for k in ("sha256", "size")})
        except (OSError, PublicationError) as error:
            warnings.append({"code": "view_refresh_pending", "path": name, "message": str(error)})
    return warnings


def repair_views(output):
    with PublicationLock(output):
        manifest = read_manifest(output)
        if manifest is None:
            raise PublicationError("No publication manifest to repair")
        return refresh_views(output, manifest)


def main(argv=None):
    import argparse
    parser = argparse.ArgumentParser(description="Inspect publication status or repair compatibility views")
    parser.add_argument("command", choices=["status", "repair"])
    parser.add_argument("--output", type=Path, default=Path("output"))
    args = parser.parse_args(argv)
    try:
        if args.command == "repair":
            result = {"warnings": repair_views(args.output)}
        else:
            manifest = read_manifest(args.output)
            if manifest is None:
                raise PublicationError("No publication manifest found")
            result = {"generation": manifest["generation"], "status": manifest["status"], "books": sorted(manifest["books"])}
    except (ValueError, OSError) as error:
        parser.exit(1, f"Publication error: {error}\n")
    print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
