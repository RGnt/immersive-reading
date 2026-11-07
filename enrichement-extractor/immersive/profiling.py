"""Offline, read-only-input benchmarks with optional temporary export artifacts."""
import argparse
import cProfile
import ctypes
import importlib.metadata
import json
from pathlib import Path
import platform
import pstats
import sys
import tempfile
from time import perf_counter
import tracemalloc

from .cache import ContentCache
from .enrichment import enrich_book, EnrichmentReport
from .resources import ResourceStore, add_resource_arguments, bundle_from_arguments


def process_memory():
    if sys.platform == "win32":
        from ctypes import wintypes
        size = ctypes.c_size_t
        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD),
                        ("peak", size), ("current", size), ("pool_peak", size), ("pool", size),
                        ("nonpaged_peak", size), ("nonpaged", size), ("pagefile", size), ("pagefile_peak", size)]
        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        kernel, psapi = ctypes.WinDLL("kernel32"), ctypes.WinDLL("psapi")
        kernel.GetCurrentProcess.restype = wintypes.HANDLE
        psapi.GetProcessMemoryInfo.argtypes = (wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD)
        if psapi.GetProcessMemoryInfo(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
            return {"process_peak_rss_bytes": counters.peak, "process_rss_bytes": counters.current}
    else:
        import resource
        peak = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
        return {"process_peak_rss_bytes": peak if sys.platform == "darwin" else peak*1024}
    return {}


class Measurements:
    def __init__(self):
        self.stages = []

    def call(self, name, operation, *args, **kwargs):
        if tracemalloc.is_tracing():
            tracemalloc.reset_peak()
        start = perf_counter()
        try:
            return operation(*args, **kwargs)
        finally:
            record = {"stage": name, "seconds": perf_counter()-start, **process_memory()}
            if tracemalloc.is_tracing():
                record["python_current_bytes"], record["python_peak_bytes"] = tracemalloc.get_traced_memory()
            self.stages.append(record)


def benchmark(folder, bundle, *, cache=None, runs=2, html=False, pdf=False, cards=False):
    """No input edits or publication; temporary study artifacts are discarded."""
    measurements, results = Measurements(), []
    with tempfile.TemporaryDirectory(prefix="immersive-profile-") as temporary:
        for number in range(runs):
            before = len(measurements.stages)
            with ResourceStore(bundle, cache=cache if cache is not False else None) as store, store.worker() as worker:
                report = EnrichmentReport()
                book = measurements.call("enrichment", enrich_book, folder, store, worker=worker, cache=cache, report=report)
                root = Path(temporary) / str(number)
                if html or pdf or cards:
                    if html or pdf:
                        from .kanjivg import add_stroke_order
                        measurements.call("stroke_order", add_stroke_order, book, bundle.kanjivg, store=store)
                        book["build_inputs"]["print_resources"] = store.print_manifest(pdf=pdf)
                    from .study_data import prepare_study_data
                    study = measurements.call("study_preparation", prepare_study_data, book, include_context=html or pdf, worker=worker)
                    if html or pdf:
                        from .render_outputs import render_book
                        if html:
                            measurements.call("html", render_book, book, root, root / "pdf", skip_pdf=True,
                                              kanjivg_archive=bundle.kanjivg, study=study, resources=bundle, resource_store=store)
                        if pdf:
                            measurements.call("pdf", render_book, book, root, root / "pdf", skip_pdf=False, include_html=False,
                                              kanjivg_archive=bundle.kanjivg, study=study, resources=bundle, resource_store=store)
                        from .verification import verify
                        measurements.call("export_verification", verify, root, html=html, pdf=pdf, previews=False, write_report=False)
                    if cards:
                        from .card_export import export_book
                        measurements.call("cards", lambda: json.dumps(export_book(book, study=study), ensure_ascii=False))
                results.append({"run": number+1, "book_id": book["id"], "chapters": len(book["chapters"]),
                                "characters": sum(len(c["text"]) for c in book["chapters"]),
                                "vocabulary": len(book["vocabulary"]), "cache_status": report.cache_status,
                                "matched": book["quality"]["vocabulary_matched"], "ambiguities": len(book.get("ambiguities", [])),
                                "enrichment_seconds": report.seconds, "stages": measurements.stages[before:]})
                if html or pdf or cards:
                    del study
                del book
    return {"environment": {"python": sys.version, "platform": platform.platform(),
                             "packages": {name: importlib.metadata.version(name) for name in ("sudachipy", "sudachidict_core", "jsonschema")}},
            "runs": results, "cache": cache.stats() if cache else {},
            "memory_note": "RSS peak is the process lifetime high-water mark, not a per-stage allocation. Python tracing excludes native allocations.",
            "lookup_note": "Reader source mapping and lookup APIs are maintained separately; tap latency is outside this benchmark."}


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--book", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--runs", type=int, default=2)
    parser.add_argument("--html", action="store_true")
    parser.add_argument("--pdf", action="store_true")
    parser.add_argument("--cards", action="store_true")
    parser.add_argument("--cprofile", type=Path)
    parser.add_argument("--trace-python-memory", action="store_true")
    add_resource_arguments(parser)
    args = parser.parse_args(argv)
    if args.runs < 1:
        parser.error("--runs must be positive")
    with tempfile.TemporaryDirectory(prefix="immersive-profile-cache-") as cache_directory:
        cache = False if args.no_cache else ContentCache(args.cache_dir or cache_directory)
        profiler = cProfile.Profile() if args.cprofile else None
        if args.trace_python_memory:
            tracemalloc.start()
        try:
            if profiler:
                profiler.enable()
            report = benchmark(args.book.resolve(), bundle_from_arguments(args), cache=cache, runs=args.runs,
                               html=args.html, pdf=args.pdf, cards=args.cards)
        finally:
            if profiler:
                profiler.disable()
            if args.trace_python_memory:
                tracemalloc.stop()
        if profiler:
            args.cprofile.parent.mkdir(parents=True, exist_ok=True)
            profiler.dump_stats(str(args.cprofile))
            stats = pstats.Stats(profiler)
            report["top_functions"] = sorted([{"function": f"{Path(k[0]).name}:{k[1]}:{k[2]}", "calls": v[1],
                                                "self_seconds": v[2], "cumulative_seconds": v[3]}
                                               for k, v in stats.stats.items()], key=lambda r: r["cumulative_seconds"], reverse=True)[:40]
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(args.report)


if __name__ == "__main__":
    main()
