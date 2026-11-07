# Reader enrichment, study exports and deck updates

The reader enrichment API reads one book folder containing one EPUB, optional Jiten CSV decks (or supplied Yomitan term dictionaries) and optional `study.config.json`. It returns a JSON-compatible dictionary without modifying input files or producing supplementary documents.

## Python API

```python
from pathlib import Path
from immersive.enrichment import enrich_book, write_reader_data

book = enrich_book(Path("books/my-book"), resources=Path("data"))

# Use book directly in a Python caller, or explicitly persist its JSON:
data_directory = write_reader_data(book, Path("reader-output"))
```

Required resources are `data/kanjidic2.xml.gz` and `data/kanji-alive/` (learner meanings and pinned provenance). Sudachi and its dictionary are local Python dependencies. Fonts, KanjiVG, ReportLab, fontTools and PyMuPDF are not required by this execution path. The full distribution still includes print dependencies; optional installation groups are future work.

To use a dictionary at another location, pass `kanjidic_path=Path(...)`. The `kanji-alive/` directory is resolved beside that dictionary. This matches the existing builder's resource convention.

`enrich_book` performs extraction, tokenization, vocabulary alignment, grammar candidate detection, kanji dictionary enrichment, source-grounded example selection and furigana preparation. It retains all occurrences across chapters, unmatched entries, quality diagnostics, source hashes and attribution. Story sentences are never translated.

Returned data is validated by default. `validate=False` lets a trusted caller perform validation itself. For structured task results, diagnostics and optional exports, use [the shared executor](orchestration-validation.md).

The returned data follows `immersive/reader.schema.json`. Study-only fields such as `learning_sets`, print-context notes and optional stroke-order data are not populated by this reader-only stage. `lookup.json` is currently a spelling/reading-to-ID index; positional lookup and original-DOM source mapping remain planned work. See [the reader contract](reader-data.md) for coordinate-system limitations.

`write_reader_data` creates exactly these files under the chosen output root:

```text
reader-output/data/<book-id>/
  reader.json
  lookup.json
  quality.json
```

It does not mutate the supplied book object. It overwrites those three JSON files if present; unrelated existing artifacts are retained. Use a separate output root when you want a directory containing only reader data. This low-level helper writes directly. Commands and `run_job` wrap it in staged, immutable [publication](reliable-publication.md), with cancellation and recovery. Use their returned snapshot directory for reader integration.

## Reader-only command

From the development project root:

```powershell
.venv\Scripts\python.exe -m immersive.pipeline --reader-only --book 'books/my-book' --output reader-output
```

Omit `--book` to process all folders under `--books` (default `books`). Use `--resources` to choose another resource directory. This mode bypasses study rendering, print verification, flashcard export, packaging and CSV updates.

The shorter CLI also supports reader enrichment with an explicit dictionary path:

```powershell
.venv\Scripts\python.exe -m immersive.cli enrich --book 'books/my-book' --kanjidic data/kanjidic2.xml.gz --output reader-output
```

Both commands use the same `enrich_book` and `write_reader_data` functions. The pipeline command prints progress text; a structured subprocess protocol for the future reader remains planned work. Python callers can use the API immediately.

## Supplementary study exports

```powershell
.venv\Scripts\python.exe -m immersive.pipeline --book 'books/my-book'
```

The default pipeline still creates HTML, PDFs, card JSON, verification reports and the study-pack ZIP. CSV modification is now opt-in. To update sort keys during that study build, add `--update-decks`.

`immersive.cli build` retains its HTML/PDF build path, also with an optional `--update-decks` flag. Its `--skip-pdf` option produces HTML and study JSON without importing print libraries. Use `enrich` for reader-only processing. [Shared study data](study-data.md) can now be prepared once and reused by HTML, PDF and card exporters without modifying the enrichment result.

## Explicit deck-update task

```powershell
.venv\Scripts\python.exe -m immersive.pipeline --decks-only --book 'books/my-book'
```

This reads/enriches the book, then adds or refreshes `first_appearance` in its source CSVs. It creates no output documents. All other CSV values and row order are preserved. See [sort-key semantics](portable-pipeline.md#deck-sort-keys).

The lower-level `immersive.deck_enrichment.enrich_decks(book, folder)` function remains available. It intentionally writes CSVs and updates the supplied book's CSV hashes/records to reflect those writes. Call it only as an explicit modification task, or pass a copy if retaining the original in-memory input snapshot.

`--reader-only` cannot be combined with `--update-decks` or `--decks-only`; conflicting options are rejected before work begins. `immersive.cli enrich --update-decks` is likewise rejected.

## Distribution status

These commands are implemented in the development source. Existing portable ZIPs retain the code bundled when they were built. Rebuild the portable release to distribute these changes; in a rebuilt release, use `Run.cmd` with the same pipeline arguments.

[Yomitan dictionary support](yomitan-dictionaries.md) accepts local format-3 ZIPs as supplements or as vocabulary sources for EPUBs without CSV decks. It preserves dictionary candidates, metadata and source notices.

[Shared resources and worker ownership](shared-resources.md) describes reusable resource stores, explicit paths, compiled morphology dictionaries and worker-scoped tokenizers.

See [profiling and caching](profiling-caching.md) for measured results, invalidation and bypass controls, and [ambiguity handling](ambiguity-handling.md) for dictionary candidates and reviewed glosses.
