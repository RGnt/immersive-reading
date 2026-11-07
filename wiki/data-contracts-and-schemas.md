# Data Contracts & Schemas

This page documents all JSON data contracts, coordinate systems, and deterministic ID generation rules across [`enrichement-extractor`](../enrichement-extractor).

---

## 1. Coordinate Systems & Offsets (Critical for Reader Integration)

Every span `[start, end)` in the pipeline is **zero-based and end-exclusive**, but you must pay strict attention to which string and unit a span references:

| Location in JSON | Coordinate Unit | Base Text Referenced |
| :--- | :--- | :--- |
| `chapter.paragraphs[].start / end` | **Unicode code points** (Python `str` index) | `chapter.text` (concatenated non-heading paragraphs, whitespace & `<rt>`/`<rp>` removed) |
| `chapter.paragraphs[].ruby[].start / end` | **Unicode code points** | `chapter.text` (chapter-relative, guaranteed inside owning paragraph) |
| `chapter.sentences[].start / end` | **Unicode code points** | `chapter.text` |
| `chapter.sentences[].sources[].start / end` | **Unicode code points** | `paragraph.text` (relative to the referenced paragraph) |
| `chapter.sentences[].display_ruby[].start / end` | **Unicode code points** | `sentence.text` (sentence-relative!) |
| `chapter.tokens[].start / end` | **Unicode code points** | `chapter.text` |
| `chapter.tokens[].start_utf16 / end_utf16` | **UTF-16 code units** (JS `string.slice` index) | `chapter.text` |
| `vocabulary[].occurrences[].start / end` | **Unicode code points** | `chapter.text` |
| `vocabulary[].occurrences[].start_utf16 / end_utf16` | **UTF-16 code units** (JS `string.slice` index) | `chapter.text` |
| `vocabulary[].selected_examples[cid].start / end` | **Unicode code points** (or `null` if unaligned) | `chapters[selected_examples[cid].chapter_id].text` |
| `vocabulary[].selected_examples[cid].ruby[].start / end` | **Unicode code points** | `selected_examples[cid].text` (example-relative!) |
| `StudyData` card `example_highlight.start / end` | **Unicode code points** | `card.plain_example` (example-relative!) |
| Source CSV `first_appearance` column | **1-based Unicode code points** | Concatenation of all `chapter.text` strings in reading order (`""` if unmatched) |

> [!IMPORTANT]
> **Normalized Text vs. Raw EPUB DOM**: `chapter.text` strips whitespace (`[\s\u3000]+`), omits heading blocks (`h1`–`h4`), and removes `<rt>`/`<rp>` ruby readings. Do **not** apply `start`/`end` or `start_utf16`/`end_utf16` directly to raw un-normalized XHTML DOM text nodes. Map via `sentence.sources` (`href`, `element_id`, `paragraph_index`) and normalize text nodes first.

---

## 2. Deterministic ID Generation (`uid`)

Defined in [`immersive/epub.py#L11-L12`](../enrichement-extractor/immersive/epub.py#L11-L12):
```python
def uid(prefix, value):
    return prefix + "-" + hashlib.sha256(value.encode()).hexdigest()[:16]
```

| Entity | Prefix / Format | Hash Input Formula | Where Defined |
| :--- | :--- | :--- | :--- |
| **Book ID** | `book-<16 hex>` | `uid("book", sha256(epub_bytes).hexdigest())` | [`build.py#L96`](../enrichement-extractor/immersive/build.py#L96) |
| **Chapter ID** | `chapter-01`, `chapter-02`, ... | `f"chapter-{i+1:02d}"` (1-based ordinal after TOC filtering) | [`epub.py#L134`](../enrichement-extractor/immersive/epub.py#L134) |
| **Sentence ID** | `s-<16 hex>` | `uid("s", f"{href}#{fragment}:{start}:{sentence_text}")` | [`epub.py#L140`](../enrichement-extractor/immersive/epub.py#L140) |
| **Vocabulary ID (CSV)** | `v-<16 hex>` | `uid("v", f"{JmDictWordId or ''}:{Word}:{ReadingKana}")` | [`build.py#L35`](../enrichement-extractor/immersive/build.py#L35) |
| **Vocabulary ID (Yomitan)** | `v-<16 hex>` | `uid("v", f"yomitan:{expression}:{hira(reading)}")` | [`yomitan.py#L227`](../enrichement-extractor/immersive/yomitan.py#L227) |
| **Kanji ID** | `kanji-<hex codepoint>` | `f"kanji-{ord(c):x}"` (e.g., `kanji-6d77`) | [`build.py#L247`](../enrichement-extractor/immersive/build.py#L247) |
| **Grammar ID** | `<rule.id>` | Defined in [`immersive/grammar.json`](../enrichement-extractor/immersive/grammar.json) | [`build.py#L273`](../enrichement-extractor/immersive/build.py#L273) |
| **Definition Candidate ID** | `definition-<16 hex>` | CSV: `uid("definition", f"{vid}:csv:{file}:{row}:{text}")`<br>Yomitan: `uid("definition", f"{vid}:{dict_id}:{bank}:{row}:{glossary_idx}")` | [`ambiguity.py#L17-L24`](../enrichement-extractor/immersive/ambiguity.py#L17-L24) |
| **Ambiguity Group ID** | `ambiguity-<16 hex>` | `uid("ambiguity", f"{chapter_id}:{start}:{end}")` | [`ambiguity.py#L49`](../enrichement-extractor/immersive/ambiguity.py#L49) |
| **Yomitan Dictionary ID** | `yomitan-<64 hex>` | `"yomitan-" + sha256(zip_bytes).hexdigest()` | [`yomitan.py#L155`](../enrichement-extractor/immersive/yomitan.py#L155) |

---

## 3. Core Reader Bundle (`output/data/<book-id>/`)

Validated by [`immersive/reader.schema.json`](../enrichement-extractor/immersive/reader.schema.json) and [`validate_reader()`](../enrichement-extractor/immersive/validation.py#L82-L202).

### 3.1 `reader.json` Top-Level Structure
- `schema_version`: `"1.0.0"`
- `id`: `"book-<16 hex>"`
- `title`: Book title from OPF `<dc:title>` or EPUB filename stem.
- `language`: `"ja"`, `gloss_language`: `"en"` (or Yomitan target language / `"mul"` / `"und"`).
- `publication`: `{"creator": ..., "publisher": ..., "date": ...}` from OPF metadata.
- `source_files`: Array of source descriptors from [`provenance.source_descriptor()`](../enrichement-extractor/immersive/provenance.py#L28-L41):
  - EPUB: `{"file": "...", "sha256": "...", "input_sha256": "..."}`
  - CSV: adds `"original_sha256"`, `"exported_sha256"` (`null` or hex), `"provenance_status"` (`"observed_input"`, `"pipeline_export"`, or `"external_input"`).
- `build_inputs`:
  - `tool_version`, `packages` (`sudachipy`, `sudachidict_core`), `kanjidic_sha256`, `grammar_rules_sha256`, `book_config`, `resources` (pinned SHA-256 + size of reader resources), `kanji_meanings` (Kanji alive metadata + SHA-256).
  - Optional: `yomitan` (ordered list of `{file, sha256}`), `kanjivg` (when stroke order added), `print_resources` (when rendered in study mode).
- `offset_convention`, `translation_policy`, `furigana_policy`, `ambiguity_policy`, `grammar_coverage`, `attribution`.
- `chapters`: Array of chapter objects:
  - `id`, `number` (1-based), `title`, `href`, `fragment`, `text`
  - `paragraphs`: `[{href, element_id, paragraph_index, text, start, end, heading, ruby: [{surface, reading, start, end}]}]`
  - `sentences`: `[{id, chapter_id, text, start, end, sources: [{href, element_id, paragraph_index, start, end}], display_ruby: [{start, end, surface, reading, source}]}]`
  - `tokens`: Sudachi split-mode A tokens `[{surface, lemma, normalized, reading, pos, start, end, start_utf16, end_utf16, oov, vocabulary_ids}]`
  - `vocabulary_ids`, `kanji_ids`, `new_grammar_ids`
- `vocabulary`: Array of vocabulary entry objects:
  - `id`, `word`, `reading`, `furigana`, `definitions`, `jmdict_id`, `pitch_positions`, `deck_occurrences`, `frequency_rank`
  - `context_gloss` (from `study.config.json`), `aliases` (from `study.config.json`)
  - `sources`, `deck_records`, `deck_examples` (with `verified_locations` and `status`)
  - `occurrences`: `[{chapter_id, start, end, start_utf16, end_utf16, surface, sentence_id, method, reading_match: {source, status, reading?, reason?}, candidate_vocabulary_ids, candidate_status}]`
  - `first_chapter_id`, `status` (`"matched"` or `"unmatched_review"`), `example_sentence_ids`
  - `selected_examples`: `{chapter_id: {text, source, source_file, csv_row?, chapter_id, sentence_id?, start, end, status, ruby}}`
  - `definition_candidates`: `[{id, text, source, status: "source_definition_block"}]`
  - `sense_selection`: `{status: "unresolved"|"unavailable"|"reviewed_override", selected_candidate_ids, reviewed_text, source, note}`
  - Optional Yomitan fields: `vocabulary_source` (`"yomitan"`), `definition_dictionary_id`, `dictionary_entries`, `dictionary_metadata`.
- `kanji`: Array of kanji objects:
  - `id`, `character`, `first_chapter_id`, `occurrences`, `vocabulary_ids`
  - `dictionary`: `{on, kun, meanings, radical, strokes, grade, source, primary_meanings, meaning_source}`
  - Optional: `stroke_order` (`{source, source_file, asset, view_box, strokes: [{number, id, d}], license, author}`), `dictionary_entries`, `dictionary_metadata`.
- `grammar`: Array of grammar objects (`id`, `label`, `meaning`, `note`, `first_chapter_id`, `status: "rule_based_candidate"`, `occurrences`).
- `ambiguities`: Array of span-level tie objects (`{id, chapter_id, start, end, status: "ambiguous_reading"|"multiple_entries", candidate_vocabulary_ids, selected_vocabulary_id: null}`).
- `quality`: Summary counts & diagnostic lists (`vocabulary_total`, `vocabulary_matched`, `unmatched_vocabulary_ids`, `missing_kanji_dictionary_ids`, `empty_chapter_ids`, `unresolved_furigana`, `kanji_meaning_sources`, `ambiguous_reading_groups`, `multiple_entry_groups`, `unverified_reading_occurrences`, `unresolved_sense_entries`, plus study-only `missing_stroke_order_ids` and `context_source_notes`).
- Optional study-only top-level field: `learning_sets` (added by [`study_reader_data()`](../enrichement-extractor/immersive/study_data.py#L80-L88) during study exports).

### 3.2 `lookup.json` & `quality.json`
- `lookup.json`: `{"schema_version": "1.0.0", "book_id": "book-...", "forms": {"word_or_reading": ["v-...", ...]}}`
- `quality.json`: Exact copy of `reader.json["quality"]` (verified by [`verification.py#L124`](../enrichement-extractor/immersive/verification.py#L124)).

---

## 4. Per-Book Configuration (`books/<book>/study.config.json`)

Validated against [`immersive/config.schema.json`](../enrichement-extractor/immersive/config.schema.json) (`additionalProperties: false`):

```json
{
  "end_before_href": "OEBPS/Text/author-biography.xhtml",
  "include_titles": ["第一章", "第二章"],
  "exclude_titles": ["奥付", "あとがき"],
  "aliases": {
    "dictionary_spelling": ["verified_book_spelling"]
  },
  "context_glosses": {
    "dictionary_spelling": "reviewed gloss override"
  },
  "print_context": {
    "chapter-01": {
      "針": {
        "word": "はり金",
        "surface": "はり金",
        "gloss": "wire",
        "definitions": "wire",
        "source": {
          "name": "National Diet Library authority 00562979",
          "url": "https://id.ndl.go.jp/auth/ndlsh/00562979"
        }
      }
    }
  }
}
```
- Note `dependentRequired` on `print_context.<chapter>.<kanji>`: if `definitions` is supplied (for a word missing from the CSV), `source` is required (and vice versa).

---

## 5. Study & Flashcard JSON Contracts

- **`output/data/<book-id>/vocabulary-sets.json`**:
  - `{"book_id": "book-...", "set_size": 20, "sets": [{"id": "set-001", "number": 1, "size": 20, "words": [{"card_id": "v-...", "word": "...", "reading": "...", "chapter_id": "chapter-01", "study_number": 1}, ...]}]}`
- **`output/data/<book-id>/kanji-context.json`**:
  - Mapping of `chapter_id -> list[contextual_kanji_card]` produced by [`contextual_cards()`](../enrichement-extractor/immersive/context.py#L32-L167).
- **`output/data/flashcards/library.json`**:
  - Produced by [`immersive/card_export.py`](../enrichement-extractor/immersive/card_export.py).
  - Top-level: `{"schema_version": "1.0", "books": [{"id", "title", "chapters": [{"id", "number", "title", "cards": [...]}], "dictionary_sources", "attribution"}]}`.
  - Each card has: `id`, `kind` (`"vocabulary"|"kanji"|"grammar"`), `prompt`, `meaning`, `reading`, `example` (plain Japanese text), `example_ruby`, `notes`, `dictionary_entries`, `dictionary_metadata`, `definition_candidates`, `sense_selection`, `set_number` (`1..N` for vocabulary, `0` for kanji/grammar).

