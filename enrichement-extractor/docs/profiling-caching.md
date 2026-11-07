# Profiling and content caching

The profiling tool measures enrichment separately from study preparation, HTML, PDFs, cards and export verification. Persistent caching reuses validated reader data and parsed reader dictionaries; it never caches native tokenizer objects or publishes study files itself.

## Measurements

Measured on 2026-11-01 with the supplied books, without changing their inputs or published exports:

| Book | Included text | Bypass cache | Cache miss, including write | Validated cache hit |
| --- | --- | --- | --- | --- |
| こぐまのクーク (12 sections) | 18,439 characters | 3.75 s | 4.44 s | 0.32 s |
| くま クマ 熊 ベアー (28 sections) | 92,699 characters | 18.59 s | 20.77 s | 1.09 s |

These warm hits were about 12x and 17x faster than bypassing the persistent cache. They are single observations on one workstation, with potentially warm OS file caches; they are not performance guarantees. Cache misses pay serialization/compression costs. Process lifetime peak working sets during the two measurement series were about 328 MiB and 566 MiB, including native allocations and earlier runs. These are not isolated per-stage memory requirements.

Initial cProfile runs identified schema validation as the dominant cost (roughly 63–72% of instrumented wall time). cProfile overhead is substantial, so those instrumented timings are not compared directly with the table. The cache preserves prior successful validation under identical semantic input, code, schema and dependency fingerprints instead of repeating it on each hit. Fresh analysis and previously unvalidated cache entries are validated normally.

A tiny authored export fixture measured approximately 0.82 s for enrichment/cache creation, 0.04 s for stroke data, 0.001 s for study preparation, 0.01 s for HTML, 2.68 s for seven PDFs, and 0.19 s for export verification. Peak process working set reached about 479 MiB during PDF work. Card JSON serialization was below 1 ms for this tiny fixture. This fixture establishes format/stage coverage, not large-book export throughput. See [machine-readable results](performance-results.json) for exact observations and environment.

## Run profiles

```powershell
# Fresh temporary cache by default: first run miss, second run hit.
.venv\Scripts\python.exe -m immersive.profiling --book 'books/my-book' --resources data --report tmp/profile.json --runs 2

# Bypass persistent caching and collect function-level CPU information:
.venv\Scripts\python.exe -m immersive.profiling --book 'books/my-book' --report tmp/no-cache.json --runs 1 --no-cache --cprofile tmp/no-cache.pstats

# Also benchmark temporary study exports and Python allocation peaks:
.venv\Scripts\python.exe -m immersive.profiling --book 'books/my-book' --report tmp/exports.json --html --pdf --cards --runs 1 --trace-python-memory
```

The tool never updates source CSVs or active publications. Temporary export files are discarded. Supply `--cache-dir` to benchmark/reuse a persistent directory; reports record actual hit/miss status rather than assuming its contents. Reports include stage wall times, process memory and optional Python allocation peaks. Windows reports process working set, POSIX reports peak RSS. Peaks are process-lifetime high-water marks; tracemalloc excludes native allocations and adds overhead. For independent cold/warm memory comparisons, use separate profiling processes. Reader tap-lookup latency is outside this tool because its API is maintained separately.

## Normal commands

Both commands enable persistent caching by default, under `.cache/immersive` beside the selected output directory. For example, the default `output/` uses the project's `.cache/immersive/`.

```powershell
.venv\Scripts\python.exe -m immersive.cli enrich --book 'books/my-book' --cache-dir 'D:/Shared reading cache'
.venv\Scripts\python.exe -m immersive.pipeline --book 'books/my-book' --no-cache
```

`--no-cache` bypasses persistent reads and writes in both tiers; a supplied resource store can still reuse its immutable in-memory dictionaries. Doctor checks actual resources and do not populate this cache. The shorter CLI reports each book's `cache_status`; the pipeline prints it. `BookResult.enrichment_seconds` exposes analysis/validation/cache timings when performed.

## Application API

```python
from pathlib import Path
from immersive.cache import ContentCache
from immersive.resources import ResourceBundle, ResourceStore
from immersive.enrichment import enrich_book, EnrichmentReport

cache = ContentCache(Path("application-cache"))
with ResourceStore(ResourceBundle.from_directory(Path("shared-resources")), cache=cache) as resources:
    with resources.worker() as worker:
        report = EnrichmentReport()
        book = enrich_book(Path("books/my-book"), worker=worker, report=report)
        print(report.cache_status, report.seconds)
        # Force analysis while keeping the same worker/resource ownership:
        book = enrich_book(Path("books/my-book"), worker=worker, cache=False)
print(cache.stats())
```

The library and reader can share the store/cache instance across their workers. Separate processes can share the cache directory. Standalone `enrich_book` and `run_job` calls without a configured cache retain their original no-cache behavior; passing `cache=ContentCache(...)` opts in. `None` inherits the store's cache; `False` bypasses it. Cache counters are aggregate instance statistics across calls, not per-job counters. Native tokenizers remain worker-owned.

## Keys, provenance and invalidation

Reader keys include EPUB bytes/filename, semantic CSV contents and column/row order, book configuration, dictionary/rule bytes, native system/user dictionaries, ordered Yomitan archives, pipeline/schema versions, Python/Unicode versions, relevant dependency versions and reader-code/schema fingerprints. Renderers, font files and charts are excluded from reader keys. Changing a PDF layout or print-resource selection therefore reuses reader analysis while rebuilding requested exports.

Only the pipeline-derived `first_appearance` CSV column is excluded from the semantic key. A cache hit refreshes current full source-byte hashes and original deck-row fields from disk. Rewriting that column does not loop through re-analysis, and cached/uncached results still agree on current provenance. Other column/header/row changes invalidate analysis. This retains current source hashes; the separate original-versus-rewritten CSV provenance expansion remains future work.

Parsed KANJIDIC2, Kanji alive and grammar caches use resource fingerprints, loader code and a format version. Store-local immutable snapshots remain pinned to their original files. Native objects are not serialized. Yomitan banks remain filtered per-book rather than retaining every dictionary entry in a persistent global index. Shared KanjiVG parsing already reuses glyphs within a store. UTF-16 offsets are now precomputed per chapter, and sentence containment uses a binary-search index.

## Integrity and validation

Entries are compressed JSON, with checksums covering data, key, format and validation metadata. Writes use unique temporary files and atomic replacement. Concurrent identical-key writers publish complete entries; no shared mutable cache index is needed. Truncated/corrupt/unreadable entries are misses. Cache write errors do not invalidate a successful enrichment. Cancellation never publishes an incomplete entry.

A completed reader entry records whether validation succeeded and its diagnostics. An unvalidated entry is validated and upgraded when a validated result is requested. Validated entries reuse that decision only under the same input/code/schema/dependency key. These are trusted application cache files with corruption checks, not signed artifacts for import from an untrusted party. Use `--no-cache` for a fresh diagnostic run. Source parsing/config checks and publication's input/resource checks still run; supplementary HTML/PDF verification is not skipped by a reader-cache hit.

Cache directories may contain book-derived text. Keep them with application data and apply the same access/backup policy as reader bundles. Automatic cache eviction and immutable-release garbage collection are not implemented; retention should be added deliberately rather than deleting files currently in use.

## Verification

All 126 tests passed. The authored study pack produced and verified seven PDFs (37 pages), HTML, card JSON and a ZIP; candidate labels and layout were visually checked.

Tests cover validated reuse, uncached equivalence including serialized order, unvalidated upgrades, derived sort-key provenance, EPUB/dictionary/config/code/dependency invalidation, print-resource independence, resource reuse across stores, explicit bypass, reused report objects, corrupted entries, concurrent writers, interrupted replacement, cancellation and Unicode/sentence boundaries. The complete suite and authored PDF/HTML/cards/ZIP smoke also cover the subsequent ambiguity changes. Published books and the distributed portable ZIP were not rebuilt.
