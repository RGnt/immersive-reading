# Shared study data and rendering

Study preparation is independent of HTML and PDF rendering. It turns enrichment data into first-appearance vocabulary, kanji and grammar cards, learning sets and source-grounded kanji context.

```python
from pathlib import Path
from immersive.enrichment import enrich_book
from immersive.study_data import prepare_study_data
from immersive.card_export import export_book
from immersive.render_outputs import render_book

book = enrich_book(Path("books/my-book"), resources=Path("data"))
study = prepare_study_data(book)

# A JSON-compatible book/chapter/card object, without writing files:
card_book = export_book(book, study=study)

# HTML and study JSON, using the same prepared data:
render_book(book, Path("study-output"), Path("study-output/pdf"),
            skip_pdf=True, study=study)
```

To include PDFs, use `skip_pdf=False` and supply a Japanese TrueType `font_path`. Stroke-order data must already have been added to the book if wanted; `kanjivg_archive` lets the exporter copy its original SVG assets. PDF generation uses the existing layouts.

## Module boundaries

| Module | Responsibility |
| --- | --- |
| `immersive/enrichment.py` | Read-only book enrichment and explicit reader JSON persistence |
| `immersive/study_data.py` | Plain card records, shared indexes, spelling groups, first-appearance ordering and learning sets |
| `immersive/context.py` | Source-grounded chapter word/readings/glosses for kanji cards |
| `immersive/card_export.py` | Book/chapter/card JSON representation and library export |
| `immersive/render.py` | HTML markup, escaping, ruby and highlighted examples |
| `immersive/pdf_elements.py` | PDF font registration and ruby/stroke/practice flowables |
| `immersive/kanji_pdf.py`, `master_pdf.py` | Supplementary PDF layouts |
| `immersive/render_outputs.py` | Export prepared data into the existing HTML/JSON/PDF directory structure |

Data preparation, context preparation and card export import no renderer. HTML-only export does not import ReportLab, fontTools or PyMuPDF. Print dependencies remain included in the full installation; optional dependency groups are separate packaging work.

## Prepared model

`prepare_study_data(book)` creates one `StudyIndexes` instance, prepares each chapter's cards once, assigns vocabulary set/study numbers, and prepares kanji context from the same base cards.

`StudyData` contains:

- `book_id`: the owning book ID.
- `indexes`: shared sentence, vocabulary, kanji, spelling-group and ordered-grammar indexes.
- `vocabulary`, `kanji`, `grammar`: chapter-ID-to-card-list mappings.
- `learning_sets`: ordered sets of up to 20 vocabulary cards, sharing the numbered vocabulary records.
- `context`: chapter-ID-to-contextual-kanji-card lists.

Cards contain plain `title`, `reading`, `meaning`, `details`, `plain_example`, structured `ruby`, source/provenance fields and optional `example_highlight`. Highlight spans are half-open Unicode-code-point offsets relative to `plain_example`; they are display data, not EPUB DOM locators. The model contains no generated HTML `example` field.

Use `prepare_study_data(book, include_context=False)` when only generic cards are needed. `export_book` does this automatically if no prepared model is supplied. Document export requires the default context preparation.

Consumers must treat both the enrichment result and prepared model as read-only. The model references source records to avoid copying the entire book. Rebuild it after changing enrichment inputs; a matching book ID alone does not detect changed CSV/configuration contents.

`study_reader_data(book, study)` creates an export view with `learning_sets` and context quality notes. Rendering serializes that view rather than adding fields to the caller's enrichment dictionary. Existing study-output JSON still includes those supplementary fields. The view also shares source records and is intended for reading/serialization.

The full pipeline prepares once after adding stroke data, passes the model to document rendering, then reuses it for card export. Existing books outside the current build are loaded and prepared once each by the library card exporter.

The [shared task executor](orchestration-validation.md) now drives both command presets, independently selects formats and validates the enrichment/document stages. The module boundaries above remain unchanged.

## Compatibility and verification

Historical imports from `immersive.render` still work through adapters. Its old card factories add HTML `example` strings for compatibility; new data consumers should import from `immersive.study_data`. Historical PDF names resolve lazily to `pdf_elements`. Internal PDF callers use `pdf_elements` directly.

`scripts/export_flashcards.py` remains a compatibility command backed by `immersive.card_export`. The public card JSON schema and contents are unchanged. `kanji-context.json` now contains plain cards with `example_highlight`, rather than a generated HTML `example` string; use `plain_example` and `ruby` for display, or `render_example(card)` for HTML.

Verification for this refactor:

- All 50 tests passed, including fresh-process tests blocking render imports during data preparation and print-library imports during HTML export.
- Across both previously generated books, 7,709 card/context records preserved their existing semantic fields, and all 92 HTML pages matched the pre-refactor bytes.
- The 6,326-card JSON export matched the pre-refactor bytes, including readings, furigana and set assignments.
- A temporary seven-PDF fixture passed generation, cover/blank-page/content checks and visual inspection of kanji, vocabulary and grammar pages.
- Preparation and export left the supplied enrichment object unchanged.

These comparisons use the same input reader files on both sides; they test this separation rather than revisiting earlier alignment changes. Source mapping and lookup APIs are handled separately. The existing book outputs and portable ZIP have not been rebuilt for this refactor.

See [reliable output publication](reliable-publication.md) for immutable reader/study snapshots, compatibility paths, partial exports, cancellation and recovery. The existing distributed portable ZIP must be rebuilt to include these source changes.

[Explicit ambiguity metadata](ambiguity-handling.md) preserves source definition blocks, reading ties and review status. Matched study cards exclude rejected homograph readings.
