# Study Exports & Validation

This page explains how enriched book data is transformed into study cards, kanji context cards, HTML pages, A4 PDFs, flashcards, and portable distributions, and how the three-layer validation system verifies every output.

---

## 1. Study Data Preparation (`immersive/study_data.py`)

[`prepare_study_data(book, include_context=True, cancel=None, worker=None)`](../enrichement-extractor/immersive/study_data.py#L51-L77) transforms an enriched `book` dictionary into a read-only [`StudyData`](../enrichement-extractor/immersive/study_data.py#L35-L48) model without importing HTML or PDF libraries:

1. **Spelling Consolidation (`grouped_vocabulary`)** ([`study_data.py#L136-L162`](../enrichement-extractor/immersive/study_data.py#L136-L162)):
   - Groups `book["vocabulary"]` by `entry["word"]` (exact spelling).
   - **Homograph isolation**: If a spelling has at least one matched entry (`v["occurrences"]`), unmatched homograph entries for that same spelling are excluded from the combined study card so rejected readings/definitions never pollute matched cards.
   - Assigns the group to `first_chapter_id` = the earliest chapter in reading order where any matched member appears.
   - Joins distinct readings with `" / "` and distinct definitions with `" / "`.
2. **First-Appearance Chapter Cards**:
   - [`vocab_cards()`](../enrichement-extractor/immersive/study_data.py#L179-L206): Emits one card per unique spelling **only in its `first_chapter_id`**, ordered by its earliest `(start, end)` occurrence in that chapter.
   - [`kanji_cards()`](../enrichement-extractor/immersive/study_data.py#L209-L242): Emits one card per unique kanji **only in its `first_chapter_id`**, with `on`/`kun` readings, `primary_meanings` (`Kanji alive` / `KANJIDIC2`), up to 4 related chapter vocabulary compounds, and first sentence example.
   - [`grammar_cards()`](../enrichement-extractor/immersive/study_data.py#L245-L255): Emits one card per grammar rule in the chapter of its earliest occurrence.
3. **Learning Sets of 20 (`vocabulary_learning_sets`)** ([`study_data.py#L165-L176`](../enrichement-extractor/immersive/study_data.py#L165-L176)):
   - Flattens all chapter vocabulary cards in reading order, assigns consecutive `study_number` (`1..N`) and `set_number` (`1..ceil(N/20)`), and chunks them into sets of `size=20` (`set-001`, `set-002`, ...). Sets can span across chapter boundaries.

---

## 2. Source-Grounded Kanji Context (`immersive/context.py`)

When `include_context=True`, [`contextual_cards(book, chapter, ...)`](../enrichement-extractor/immersive/context.py#L32-L167) selects a chapter word (`context_word`), reading (`context_reading`), and word gloss (`context_gloss`) for each new kanji card in `chapter`:

1. **Candidate Search**:
   - **Aligned vocabulary**: Finds all vocabulary occurrences in `chapter` whose `surface` contains the kanji and whose EPUB ruby reading matches (or is inflectionally compatible with) the vocabulary reading (`aligned_deck_word`), or where the EPUB has no overlapping ruby (`aligned_deck_word_csv_reading`).
   - **Exact surface + EPUB ruby fallback**: If no aligned occurrence exists (e.g., the kanji appears inside a larger compound that Sudachi tokenized as one unit), searches `chapter.text` for literal occurrences of `entry["word"]` backed by explicit EPUB `<ruby>` (`exact_surface_and_book_ruby`). Incompatible homographs are rejected unless explicitly authorized by `study.config.json` `print_context`.
2. **Ranking & Tie Handling**:
   - Ranks candidates by `(entry["word"] != char, surface != entry["word"], start_offset, len(entry["word"]))` (preferring standalone single-kanji words first, then uninflected forms, then earliest appearance, then shortest compound).
   - If multiple distinct vocabulary entries tie at the same winning span `[start, end)` and no `context_gloss` / `print_context` override resolves them, combines their words/readings/glosses with `" / "` and marks `context_gloss_status = "ambiguous_dictionary_candidates"`.
3. **Character Dictionary Fallback**:
   - If no vocabulary word in the chapter contains the kanji, falls back cleanly to `context_gloss_scope = "character_dictionary"` and `context_gloss_status = "general_character_meaning"` using KANJIDIC2 meanings (or a configured `print_context` supplement with required `source` attribution).

---

## 3. HTML & PDF Rendering

### HTML Companion (`immersive/render_outputs.py` & `render.py`)
Generated under `output/html/<book-id>/`:
- `index.html`: Book overview, alignment summary stats, links to masters, lookups, JSONs, and per-chapter kanji/vocabulary counts.
- `chapters/chapter-XX-kanji.html` & `chapters/chapter-XX-vocabulary.html`: First-appearance chapter study sheets.
- `vocabulary.html`: All 20-word learning sets on one page with expandable Japanese examples (`<details class="vocabulary-example">`).
- `grammar.html`: Grammar candidates grouped by first chapter.
- `dictionary.html` & `kanji.html`: Complete searchable word and kanji lookups across the entire book.
- `review.html`: Unmatched deck entries (`unmatched_review`).
- `dictionary-sources.html`: Rendered when Yomitan dictionaries are used, displaying index revisions, attribution, and full bundled license notices.
- `assets/style.css` & `assets/lookup.js`: Client-side search filter, furigana toggle (`data-toggle="hide-ruby"`), and meaning blur toggle (`data-toggle="hide-meanings"`).

### Printable A4 PDFs (`immersive/kanji_pdf.py`, `master_pdf.py`, `pdf_elements.py`)
Generated under `output/pdf/<book-id>/` when `skip_pdf=False`:
- **File Inventory**: `2 * len(chapters) + 3` PDF files per book:
  - `chapter-01-kanji.pdf` ... `chapter-NN-kanji.pdf`
  - `chapter-01-vocabulary.pdf` ... `chapter-NN-vocabulary.pdf`
  - `kanji-master.pdf`, `vocabulary-master.pdf`, `grammar-master.pdf`
- **Strict Visual & Layout Rules** (enforced by [`pdf_validation.py`](../enrichement-extractor/immersive/pdf_validation.py)):
  - **Page 1 (Cover)**: Book metadata, usage instructions, source filenames, and full copyright/license notices (`COPYRIGHT / LICENSE NOTICES`). Must fit above `18 mm` bottom margin.
  - **Page 2 (Blank Reverse)**: Completely blank, unnumbered page (`canvas.showPage()`) for double-sided printing.
  - **Page 3+ (Study Content)**: Printed page numbers start at `1` on physical page 3 (`canvas.getPageNumber() - 2`). White backgrounds only (no shaded fills with area >= 1000 pt²). No raw EPUB paths (`OEBPS/`, `.xhtml`) in PDF text.
  - **Kanji Cards**: Up to 4 cards per page (typically 3), separated by `7 mm` vertical whitespace (`>= 6.9 mm` verified). Each card has an outer border (`#777777`), left glyph/metadata column (`21 mm`), middle `ON'YOMI`/`KUN'YOMI` column (`69 mm`), right `KANJI MEANINGS` + `CHAPTER WORD / READING` + `WORD GLOSS` column, full-width [`RubyText`](../enrichement-extractor/immersive/pdf_elements.py#L33-L68) example row, full-width cumulative [`StrokeOrder`](../enrichement-extractor/immersive/pdf_elements.py#L91-L129) row (previous strokes `#263b30`, current stroke red `#b44331`, future strokes faint `#e0e6df`), and one `14 mm`-tall [`PracticeGrid`](../enrichement-extractor/immersive/pdf_elements.py#L71-L88) row with dashed crosshairs inside the card border.
  - **Vocabulary Master vs Chapter Vocabulary PDFs**: `vocabulary-master.pdf` places each 20-word set on its own page (`PageBreak()` + `KeepInFrame`). Chapter vocabulary PDFs flow set headings continuously without forced page breaks.
  - **Appended Kana Charts**: If `data/charts/hiragana-chart-by-tofugu.jpg` and `katakana-chart-by-tofugu.jpg` are configured in `ResourceBundle.charts`, they are appended at the end of every PDF rotated 90 degrees.

---

## 4. Three-Layer Validation System

| Layer | Module | Dependencies | What It Checks |
| :--- | :--- | :--- | :--- |
| **Layer 1: Reader & Config Validation** | [`immersive/validation.py`](../enrichement-extractor/immersive/validation.py) | `jsonschema` only (no HTML/PDF libs) | Draft 2020-12 JSON Schema (`config.schema.json`, `reader.schema.json`), resource fingerprint consistency, contiguous paragraph spans, exact reconstruction of `chapter.text` and `sentence.text`, non-overlapping ruby spans, UTF-16 offset equality, `first_chapter_id` reading order, Yomitan reference integrity, and ambiguity/definition candidate provenance. Returns nonfatal `diagnostics` warnings. |
| **Layer 2: Study & HTML Verification** | [`immersive/verification.py`](../enrichement-extractor/immersive/verification.py) | `beautifulsoup4` | Snapshot manifest checksums (`verify_record`), `quality.json` & `lookup.json` agreement with `reader.json`, consecutive 20-word `learning_sets` covering all matched spellings (`validate_study`), KanjiVG SVG asset presence, unique first-appearance HTML cards per chapter, valid local `<a href>` / `<link>` / `<script>` targets and `#fragment` IDs, and `<ruby>` coverage on every kanji inside `.example` elements (`verify_html`). |
| **Layer 3: PDF Layout & Font Verification** | [`immersive/pdf_validation.py`](../enrichement-extractor/immersive/pdf_validation.py) | `pymupdf`, `PIL` | Exact PDF file count (`2 * chapters + 3`), cover license notice on page 1, blank page 2, `.layout.json` / `.vocabulary.json` agreement, 1–4 kanji entries per page, `practice_bbox` inside card `bbox`, `>= 6.9 mm` gap between cards, 1 page per set in `vocabulary-master.pdf`, rotated unclipped chart pages, text span bounding boxes within page margins (`x0 >= 20, y0 >= 15, x1 <= width-18, y1 <= height-12`), no shaded backgrounds, embedded Japanese fonts, and renders `previews/<bid>/<bid>-contact-XX.png` contact sheets. |

---

## 5. Portable Windows x64 Distribution (`scripts/build_portable.py`)

[`scripts/build_portable.py`](../enrichement-extractor/scripts/build_portable.py) and [`immersive/release.py`](../enrichement-extractor/immersive/release.py) build `dist/ImmersivePipeline-windows-x64.zip`:
1. Verifies that all packages in the transitive closure of `uv.lock` (including `extra = "print"`) are installed at their exact pinned versions and that every installed file matches its wheel `RECORD` SHA-256 hash (`verify_installed_file`).
2. Copies the base Python 3.12 runtime (`python.exe`, DLLs, stdlib `Lib/`) into `dist/ImmersivePipeline-windows-x64/python/` and writes `python312._pth` pointing to `../app`.
3. Copies `immersive/`, `scripts/`, `tests/`, `pyproject.toml`, `uv.lock` into `app/`.
4. Copies `data/kanjidic2.xml.gz`, `kanjivg/`, `fonts/` (excluding variable fonts), `kanji-alive/`, and `charts/` into `data/`. Excludes user books, Yomitan ZIPs, `.cache`, and `output/`.
5. Generates `Run.cmd`, `Check.cmd`, `licenses/dependencies.json`, `licenses/THIRD-PARTY-NOTICES.txt`, and `distribution.json` (with SHA-256 fingerprints of every file in the distribution).
6. Runs a live subprocess smoke test (`python.exe -I app/run_pipeline.py --doctor`) before zipping `ImmersivePipeline-windows-x64.zip` and writing `.zip.sha256`.

