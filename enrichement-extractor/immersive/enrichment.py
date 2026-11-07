"""Reader enrichment and JSON persistence, independent of study exporters.

Importing or calling this module does not load PDF libraries or modify inputs.
"""
import json
from dataclasses import dataclass, field
from pathlib import Path
from time import perf_counter

from .build import build_data
from .validation import validate_reader


@dataclass
class EnrichmentReport:
    cache_status: str = "disabled"
    validated: bool = False
    diagnostics: list = field(default_factory=list)
    seconds: dict = field(default_factory=dict)


def enrich_book(folder, resources=None, *, kanjidic_path=None, validate=True, cancel=None, yomitan_paths=None, worker=None, cache=None, report=None):
    """Return reader data for one EPUB folder without writing any files.

    ``resources`` contains kanjidic2.xml.gz and kanji-alive/. Fonts and KanjiVG
    are not required. Optional study.config.json is read by the core builder.
    All occurrences, original source hashes and dictionary candidates survive.
    Local Yomitan ZIPs supplement CSVs or supply vocabulary without a CSV.
    ``yomitan_paths=None`` discovers resources/yomitan/*.zip; [] disables it.
    """
    from .resources import resource_worker
    from .jobs import check_cancelled
    report = report if report is not None else EnrichmentReport()
    report.cache_status, report.validated = "disabled", False
    report.diagnostics, report.seconds = [], {}
    with resource_worker(resources, worker=worker, cache=cache, kanjidic_path=kanjidic_path, yomitan_paths=yomitan_paths) as active:
        bundle = active.store.bundle
        from .cache import CACHE_ENABLED
        cache = (cache if cache is not None else active.store.cache) if CACHE_ENABLED.get() and cache is not False else None
        saved, key = None, None
        if cache is not None:
            from .cache import reader_inputs, hydrate_reader
            start = perf_counter()
            key, files, records = reader_inputs(folder, active.store, cancel=cancel)
            saved = cache.get("reader", key, cancel=cancel)
            report.seconds["cache_lookup"] = perf_counter()-start
            report.cache_status = "hit" if saved is not None else "miss"
        if saved is not None:
            book = hydrate_reader(saved["data"], files, records)
            report.validated = saved["validated"] is True
            report.diagnostics = saved["diagnostics"]
        else:
            start = perf_counter()
            book = build_data(Path(folder), bundle.kanjidic, cancel=cancel, yomitan_paths=bundle.yomitan, worker=active)
            report.seconds["analysis"] = perf_counter()-start
        if validate:
            start = perf_counter()
            if not report.validated:
                report.diagnostics = validate_reader(book)
                report.validated = True
            report.seconds["validation"] = perf_counter()-start
        if cache is not None and (saved is None or (validate and not saved["validated"])):
            current_key, current_files, _ = reader_inputs(folder, active.store, cancel=cancel)
            if current_key != key or current_files != files or book["source_files"] != files:
                raise ValueError("Inputs changed during cached enrichment; retry with stable sources")
            start = perf_counter()
            cache.put("reader", key, book, validated=report.validated, diagnostics=report.diagnostics, cancel=cancel)
            report.seconds["cache_write"] = perf_counter()-start
        check_cancelled(cancel)
        return book


def save_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def write_reader_data(book, output):
    """Write reader/quality/lookup JSON under output/data/<book-id>.

    This explicit output operation does not mutate ``book`` or source files.
    Study-only fields and stroke assets are optional, supplied by exporters.
    """
    root = Path(output) / "data" / book["id"]
    lookup = {}
    for entry in book["vocabulary"]:
        for form in sorted({entry["word"], entry["reading"]} - {""}):
            lookup.setdefault(form, []).append(entry["id"])
    save_json(root / "reader.json", book)
    save_json(root / "quality.json", book["quality"])
    save_json(root / "lookup.json", {"schema_version": "1.0.0", "book_id": book["id"], "forms": lookup})
    return root
