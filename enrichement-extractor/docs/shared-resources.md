# Shared resources and worker ownership

The EPUB library, reader and extractor can use the same resource files and the same `ResourceStore` within a process. Resource ownership is independent of EPUB parsing, publication and rendering. Import `ResourceBundle` and `ResourceStore` from `immersive.resources`; no PDF dependency is imported by these APIs.

## Ownership

| Object | Owner and lifetime | Shared contents |
| --- | --- | --- |
| `ResourceBundle` | Application configuration; frozen, absolute paths | Dictionary, grammar, font, chart and stroke-resource locations |
| `ResourceStore` | Application/service, or one process in a worker pool | One native Sudachi dictionary; immutable parsed KANJIDIC2, Kanji alive and grammar data; lazily parsed KanjiVG paths and original SVG bytes |
| `WorkerContext` | One thread in one process; close at the end of its work | Dedicated Sudachi A/B/C tokenizers and a bounded 8,192-item ruby cache |
| Enrichment/study results | Calling feature or job | Mutable JSON-compatible book data; it cannot mutate the store's dictionary snapshots |

The store creates tokenizers under a lock; subsequent tokenization uses each worker's buffers. Enrichment, furigana and study preparation use the same supplied worker. Saved example/sentence ruby is reused by later exports instead of being regenerated with a different default dictionary.

Do not pass a worker between threads or processes. It checks its creating thread/process and rejects misuse. Construct each worker inside its owning thread. A store can be shared across threads; each process must create its own store and may point at the same resource directory. Python objects/caches are not shared across processes. The frozen bundle describes paths and can be passed as configuration instead.

## Application API

```python
from pathlib import Path
from immersive.resources import ResourceBundle, ResourceStore
from immersive.enrichment import enrich_book
from immersive.orchestration import BuildOptions, run_job

bundle = ResourceBundle.from_directory(Path("shared-resources").resolve())

# Own this store at application/service scope, not once per book.
with ResourceStore(bundle) as resources:
    # Create another worker in the reader/library background thread as needed.
    with resources.worker() as worker:
        book = enrich_book(Path("books/my-book"), worker=worker)
        # Use this same worker for another book or reader annotation operation.
        from immersive.furigana import furigana
        ruby = furigana("海を読む。", worker=worker)

        result = run_job(
            Path("books"), Path("output"), resources,
            book_folder=Path("books/my-book"), worker=worker,
            options=BuildOptions(),  # reader only
        )
```

`enrich_book(folder, resources)` and `run_job(..., resources)` also accept a store without an explicit worker: they borrow the store and create/close their own worker for that call. Passing a bundle creates a temporary store for the call. Passing a directory retains the legacy directory adapter. Existing dictionary/font/archive overrides work with directory arguments; put overrides in the bundle when supplying an explicit bundle/store/worker. Conflicting ownership and overrides raise `ValueError`.

Close all worker contexts before closing their store. Closing an active store raises an error rather than invalidating an in-use dictionary. Context managers close owned workers even on cancellation or failure. Closing the store releases its caches and native dictionary reference; callers retaining native token results can extend the native data's lifetime.

Standalone `furigana()` calls without a worker retain a thread-local compatibility worker; there is no global shared tokenizer. Call `immersive.tokenization.close_default_worker()` to release it on that thread. Applications should supply explicit workers for predictable lifetime and dictionary selection.

## Explicit resource selection

`ResourceBundle.from_directory(root)` resolves these conventional paths once:

```text
shared-resources/
  kanjidic2.xml.gz
  kanji-alive/ka_data.csv
  kanji-alive/source.json
  kanjivg/source.zip
  kanjivg/source.json          optional provenance
  fonts/NotoSansJP-Regular.ttf
  charts/hiragana-chart-by-tofugu.jpg
  charts/katakana-chart-by-tofugu.jpg
  yomitan/*.zip               optional; filename priority order
```

The default grammar rules are a packaged resource, not a working-directory lookup. The default compiled Sudachi system dictionary comes from the installed `sudachidict_core` package. Specify a shared compiled system dictionary and compiled user dictionaries to use the same morphology resources across components:

```python
bundle = ResourceBundle.from_directory(
    Path("D:/Japanese resources"),
    sudachi_dictionary=Path("D:/Japanese resources/sudachi/system.dic"),
    sudachi_user_dictionaries=[Path("D:/Japanese resources/sudachi/names.dic")],
    grammar=Path("D:/Japanese resources/grammar.json"),
    yomitan_paths=[Path("D:/Japanese resources/dictionaries/terms.zip")],
    charts=[],  # explicitly omit charts
)
```

The constructor also accepts independent `kanjidic`, `kanji_alive`, `grammar`, `sudachi_dictionary`, `sudachi_user_dictionaries`, `kanjivg`, `font`, `charts` and `yomitan` fields. Thus a library need not duplicate dictionaries next to its executable. Paths are resolved at construction and stay stable after a working-directory change. `ResourceBundle()` by itself supports morphology helpers; reader enrichment additionally requires KANJIDIC2 and Kanji alive paths.

Only the legacy directory adapter keeps the documented `--kanjidic` convention of finding Kanji alive beside that override. `kanji_alive=` overrides this explicitly. There is no project-parent or operating-system font search. Default optional charts come from the chosen root's `charts/`; explicit chart lists may be empty. The supplied development charts have moved there.

Yomitan ZIPs are selected in the bundle and remain shared disk resources. Import retains only book-relevant candidates and processes banks one at a time. Full parsed Yomitan dictionaries are not retained indefinitely in the shared store; persistent indexing and measured cache optimization remain future work. Enriched reader data already contains candidates for later lookup.

## Commands and capability checks

Both commands accept the same resource flags:

- `--resources <directory>`
- `--kanjidic <file>`, `--kanji-alive <directory>`, `--grammar-rules <file>`
- `--sudachi-dictionary <compiled file>` and repeated `--sudachi-user-dictionary <compiled file>`
- repeated `--yomitan <ZIP>`
- `--kanjivg <ZIP>`, `--font <file>`
- `--charts <directory>` or `--no-charts`

```powershell
.venv\Scripts\python.exe -m immersive.cli enrich --book 'books/my-book' --resources 'D:/Japanese resources'

# Check HTML/card resources without loading print libraries or requiring fonts:
.venv\Scripts\python.exe -m immersive.pipeline --doctor --skip-pdf

# Check reader resources only:
.venv\Scripts\python.exe -m immersive.pipeline --doctor --reader-only
```

`doctor.check(resources=store, capabilities={"reader", "html"})` supports the same shared store. Reader/cards checks require morphology and reader dictionaries/rules. HTML additionally requires KanjiVG for the existing study-export path. PDF checks add font/rendering dependencies and selected charts. Doctor checks actual dictionary/rule loading, not just file existence.

ReportLab has a process-global font registry. Pipeline PDF rendering uses a serialized `pdf_session`; PDF verification takes the same lock so concurrent jobs cannot overwrite each other's `Japanese` font while drawing. Reader enrichment remains independent and can run concurrently. Callers using the low-level PDF writers directly must wrap registration and drawing in `pdf_session(font_path)` too.

## Resource snapshots and provenance

Stores pin SHA-256 fingerprints and reject observed resource replacement with `ResourceChangedError`. They do not silently mix newly loaded files with old dictionary objects. Update resources by creating a new bundle/store, route new work there, and close the old store after its workers finish. Keep resource files stable while workers use them; this API does not implement a resource downloader or file-update manager.

`build_inputs.resources` records reader-resource filenames, hashes and sizes, including compiled morphology dictionaries. Existing dictionary/rule provenance fields remain. Study data adds `build_inputs.print_resources`, recording the font, charts, KanjiVG and optional archive metadata. Publication rehashes the resources used by that job before committing; it checks reader resources before explicit CSV updates as well. A changed resource rejects publication and preserves the previous active generation. Files previously used by other capabilities are not required for an unrelated reader-only job.

PDF verification uses the saved chart inventory (legacy output uses its layout metadata), so verification does not depend on fonts/charts installed on the verification machine. Resource snapshots are not full enrichment caches: semantic cache keys, persistent caches, profiling and release retention remain separate tasks.

The tokenizer ownership design follows the upstream [Sudachi tokenizer implementation](https://github.com/WorksApplications/sudachi.rs/blob/develop/python/src/tokenizer.rs) and [shared dictionary implementation](https://github.com/WorksApplications/sudachi.rs/blob/develop/python/src/dictionary.rs). Runtime operation is fully offline.

## Verification

All 108 tests passed. Tests exercise parallel workers over one dictionary, immutable shared data, isolated book mutations, worker/process ownership, lifecycle errors, changed resources, working-directory changes, shared KanjiVG parsing/assets, saved ruby reuse and HTML/cards with print imports blocked. A full authored-book build from an unrelated working directory produced and verified seven PDFs (37 pages), HTML, cards and a ZIP. Cover, kanji, vocabulary and rotated-chart pages were visually inspected. Existing book outputs and the distributed portable ZIP were not rebuilt.

See [profiling and caching](profiling-caching.md) for measured results, invalidation and bypass controls, and [ambiguity handling](ambiguity-handling.md) for dictionary candidates and reviewed glosses.
