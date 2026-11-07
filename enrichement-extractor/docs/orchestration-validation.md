# Unified orchestration and validation

Both commands now delegate task execution to `immersive.orchestration.run_job`. The executor discovers books, enriches and validates each one, performs explicitly requested CSV updates, prepares shared study data, exports selected formats, validates those outputs and optionally packages them. It calls installed package modules directly; it does not load repository scripts through `runpy`.

## Command presets

| Command | Default task |
| --- | --- |
| `python -m immersive.pipeline` | Reader data, HTML, PDFs, cards, verification and ZIP |
| `python -m immersive.cli build` | Reader data, HTML, PDFs and verification |
| `python -m immersive.cli enrich` or pipeline `--reader-only` | Validated reader/lookup/quality JSON; inputs unchanged |
| `python -m immersive.cli decks` or pipeline `--decks-only` | Validate enrichment, then explicitly update source CSV sort keys; no output documents |

Both commands support `--book <folder>` or `--books <parent>`, `--resources <directory>`, `--output <directory>`, and `--update-decks` during a study build. The shorter CLI also retains `--kanjidic`, `--kanjivg` and `--font` overrides. Paths supplied through the commands are resolved from the caller's working directory. The portable launcher selects the extracted distribution's directory first.

The pipeline command has independent `--skip-html`, `--skip-pdf`, `--skip-cards` and `--skip-package` switches. For example:

```powershell
# Cards and reader JSON, with validation; no document resources needed:
.venv\Scripts\python.exe -m immersive.pipeline --book 'books/my-book' --skip-html --skip-pdf --skip-package

# HTML and study JSON, without loading print libraries:
.venv\Scripts\python.exe -m immersive.pipeline --book 'books/my-book' --skip-pdf --skip-cards --skip-package
```

For `immersive.cli build`, use `--skip-html` or `--skip-pdf` to disable documents, and `--cards` / `--package` to opt into those exports. PDFs can be exported without HTML. Reader JSON is always written for non-deck tasks.

`--skip-verify` explicitly skips reader semantic/schema validation and output verification. Input parsing, configuration validation and malformed-CSV checks still run.

## Python API

```python
from pathlib import Path
from immersive.orchestration import BuildOptions, run_job

result = run_job(
    books=Path("books"),
    book_folder=Path("books/my-book"),
    output=Path("reader-output"),
    resources=Path("data"),
    options=BuildOptions(),  # reader-only, validation enabled
)
for book in result.books:
    print(book.book_id, book.data_directory, book.diagnostics)
```

`BuildOptions` is a frozen dataclass. Its `mode` is `reader`, `study` or `decks`; `html`, `pdf`, `cards` and `package` independently select supplementary outputs in study mode. `validate=True` is the default. `update_decks=True` is allowed in study/deck tasks and rejected in reader mode. `previews=False` suppresses PDF contact-sheet generation during verification.

`run_job` returns a `JobResult` with books, archive, publication manifest/generation, status, errors and warnings. Each `BookResult` contains book ID/title, output directories, diagnostics, deck-update reports and counts. It is quiet by default. An optional `on_event` callback receives coarse synchronous stage events. Cancellation and concurrent-job coordination are described in [reliable publication](reliable-publication.md). Returned directories are immutable snapshot paths.

The lower-level `enrich_book` API also validates its returned data by default. `validate=False` is available for trusted callers managing validation themselves; the shared executor uses it internally and runs validation once to collect diagnostics.

All selected books are enriched and validated before explicitly requested CSV updates. Output is staged and published through an atomic manifest pointer. Document failures retain previous study output while publishing independently completed reader data; CSV input changes are outside this output transaction. See [publication guarantees and recovery](reliable-publication.md).

## Validation layers

- `immersive.validation`: schema, unique IDs, references, reading-order ownership, paragraph/sentence/token/occurrence spans, exact UTF-16 coordinate conversion, source paragraph ranges, ruby spans, selected examples, meaning provenance and quality counts. It loads no HTML/PDF renderer.
- `immersive.verification`: persisted reader/quality/lookup consistency, study-set membership, first-appearance HTML cards, local links/anchors and example furigana. HTML checks are selected independently of PDF checks.
- `immersive.pdf_validation`: expected PDF filenames, covers/licenses, blank reverse pages, layout manifests, card/practice bounds and spacing, chapter vocabulary membership, rotated charts, page/text bounds, white backgrounds and embedded fonts. This module is imported only when PDF validation is requested.

`validate_reader(book)` returns nonfatal diagnostic records and raises `ValidationError` for invalid structure. Diagnostics cover unmatched vocabulary, unresolved furigana, missing dictionary records and empty analysis tokens. Sudachi can emit empty punctuation tokens; their zero-width spans are permitted only with an empty surface. Nonempty token and occurrence spans must still be valid.

Incomplete reading coverage does not block reader output. Strict supplementary HTML verification requires furigana on displayed example kanji. Numeric/Latin pronunciation candidates and dictionary-sense uncertainty retain their existing evidence/status fields.

Production checks use explicit exceptions and remain enabled under `python -O`. `ValidationError` is a `ValueError` with `path` and `message` attributes. Invalid configuration and malformed CSVs name the input location. JSON errors include filename, line and column. Commands return a zero status on success and print actionable errors on failure; library callers receive exceptions and structured results.

Configuration keys/types are checked against `immersive/config.schema.json` before EPUB processing. Unknown keys are rejected. Required deck columns, duplicate headers and malformed row widths are checked before alignment. Additional deck columns are retained.

## Verify existing output

```powershell
# Validate reader bundles without print libraries or report writes:
.venv\Scripts\python.exe -m immersive.verification --output output --reader-only --no-report

# Validate HTML and reader data, selecting a book:
.venv\Scripts\python.exe -m immersive.verification --output output --book-id book-0123456789abcdef --skip-pdf

# Full study verification, without generating contact sheets:
.venv\Scripts\python.exe -m immersive.verification --output output --no-previews
```

`--book-id` may be repeated. Build-time verification automatically selects the books processed in that invocation, so an unrelated reader-only book does not need PDFs. Build-time reports belong to study snapshots. Standalone verification writes `reports/verification-latest.json` and optional previews under `reports/validation-runs/`. Reader-only builds publish the three core JSON files plus publication metadata, returning diagnostics without supplementary reports.

`python -m immersive.packaging --output output` calls the installed packaging module. Compatibility scripts for verification, card export and packaging remain available for direct checkout execution. Their small path bootstrap is only for the legacy script entry point; the executor never depends on it.

`python -m immersive.pipeline --doctor --reader-only --resources data` checks reader dependencies/resources without fonts, KanjiVG or print libraries. The default doctor remains the full-study check.

## Verification record and remaining work

The earlier orchestration pass had 66 passing tests; publication coverage and current verification are recorded in [reliable publication](reliable-publication.md). The suite covers both command presets, independent export selections, source-file immutability, validation before CSV modification, invalid options/configuration/decks, malformed references/spans/companions, unresolved readings, empty learning sets, HTML links and optimization-safe checks. Both existing book bundles also passed read-only data validation.

A temporary full-study run produced and verified seven PDFs (37 pages), HTML, card JSON and a ZIP. Contact sheets were visually checked; optimized verification accepted the valid output and rejected corrupted layout metadata. Previews now render through a fresh PDF context after resource inspection, avoiding blank-chart artifacts seen during verification.

A wheel was built and installed into a separate environment. From another working directory, its API performed an offline cards-only build, verification and packaging with print imports blocked. Both installed console entry points and reader doctor were also exercised. The environment reused existing offline dependency packages; the test did not download the runtime dependency set again.

This completes the orchestration/validation portion of plan step 5. Separate original-vs-rewritten CSV provenance expansion, cache/performance work, optional dependency groups and portable release rebuilding remain separate tasks. Publication and manifest-owned packaging are now implemented; see [the guide](reliable-publication.md). Source mapping and lookup are handled separately. Existing book exports and the distributed portable ZIP have not been rebuilt.

[Shared resources and worker ownership](shared-resources.md) describes reusable resource stores, explicit paths, compiled morphology dictionaries and worker-scoped tokenizers.

See [profiling and caching](profiling-caching.md) for measured results, invalidation and bypass controls, and [ambiguity handling](ambiguity-handling.md) for dictionary candidates and reviewed glosses.
