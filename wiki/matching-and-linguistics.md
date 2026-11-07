# Matching & Linguistics

This page explains how [`enrichement-extractor`](../enrichement-extractor) parses EPUBs, tokenizes Japanese text, aligns vocabulary occurrences, resolves readings and furigana, imports Yomitan dictionaries, and models ambiguity.

---

## 1. EPUB Extraction & Source Ruby (`immersive/epub.py`)

[`read_epub(path, config)`](../enrichement-extractor/immersive/epub.py#L24-L147) extracts reading-order chapters and paragraphs without allowing `<rt>`/`<rp>` ruby readings to pollute surface text:

1. **Spine & TOC Resolution**:
   - Reads `META-INF/container.xml` -> OPF `manifest` and linear `spine`.
   - Extracts navigation from EPUB3 `<nav epub:type="toc">` or EPUB2 `ncx` `<navPoint>`.
   - Filters out standard front/back matter (`もくじ`, `目次`, `奥付`, `表紙`, `扉`, `Contents`, `Table of Contents`, `Cover`, `Copyright`) plus `config.get("exclude_titles", [])` using NFKC + casefolded + whitespace-stripped comparison.
   - Falls back to spine sections (`Section 1`, `Section 2`, ...) **only** when navigation contains no spine entries at all (never when user `include_titles` / `exclude_titles` filter everything out — that raises `ValueError`).
   - Supports multiple chapter fragments (`href#fragment`) within a single XHTML file and stops at `config["end_before_href"]` if configured.
2. **Private-Use-Area (PUA) Marker Ruby Extraction** ([`epub.py#L85-L110`](../enrichement-extractor/immersive/epub.py#L85-L110)):
   - Inside each top-level `p`, `h1`–`h4`, or `li` block, `<ruby>` elements (both paired `<rb>`/`<rt>` and simple `<ruby>base<rt>reading</rt></ruby>`) are temporarily replaced with unique PUA markers `\ue000{index}\ue001`.
   - Any stray `rt`, `rp`, `script`, `style` tags are decomposed, and whitespace (`[\s\u3000]+`) is removed via `norm()`.
   - Each marker is then replaced in order with its `surface` base text, recording exact `[start, end)` Unicode code-point offsets for `ruby`.
3. **Paragraph & Sentence Construction** ([`epub.py#L127-L144`](../enrichement-extractor/immersive/epub.py#L127-L144)):
   - Heading blocks (`h1`–`h4`) and blocks whose text matches the chapter TOC title are skipped from `chapter.text`.
   - Non-heading blocks become `chapter.paragraphs` with contiguous chapter-relative `[start, end)` offsets.
   - Sentences are split **within each paragraph** using `r"[^。！？!?]+[。！？!?][」』”]*|[^。！？!?]+$"` so a sentence never crosses a paragraph boundary.

---

## 2. Tokenization & The Three Vocabulary Matching Paths (`immersive/build.py`)

For each paragraph in a chapter, [`build_data()`](../enrichement-extractor/immersive/build.py#L180-L226) runs Sudachi in both **SplitMode.A** (short units, stored as `chapter.tokens`) and **SplitMode.C** (longer compound units, added to `all_spans` for candidate matching).

Every candidate match is passed through `accept(entry, start, end, method, matched_tokens)` which delegates to [`MatchPolicy.accept()`](../enrichement-extractor/immersive/matching.py#L70-L106):

1. **Path 1 — `morphology`** ([`build.py#L203-L206`](../enrichement-extractor/immersive/build.py#L203-L206)):
   - Checks every token `t` in `all_spans` (both mode A and mode C).
   - Looks up `t["surface"]`, `t["lemma"]`, and `t["normalized"]` in `by_form` (which indexes `entry["word"]` and `entry["aliases"]`).
2. **Path 2 — `normalized_token_sequence`** ([`build.py#L208-L213`](../enrichement-extractor/immersive/build.py#L208-L213)):
   - Pre-tokenizes each vocabulary `entry["word"]` in mode A (if no token is OOV) into a tuple of `normalized_form()` strings (`phrase_patterns`).
   - Slides across `chapter.tokens` (mode A) to match multi-token and conjugated phrases (e.g., `気になった` matching `気になる`).
3. **Path 3 — `token_boundary_phrase`** ([`build.py#L214-L225`](../enrichement-extractor/immersive/build.py#L214-L225)):
   - For entries with `len(entry["word"]) >= 2`, finds literal regex occurrences in `chapter.text` **only if** `m.start()` is a known token start and `m.end()` is a known token end (never matches substrings inside larger tokens).

---

## 3. Unified Reading & Boundary Policy (`immersive/matching.py`)

[`MatchPolicy.accept(entry, start, end, tokens)`](../enrichement-extractor/immersive/matching.py#L70-L106) enforces two core rules across all three matching paths:

1. **Paragraph Boundary Enforcement (`self.contains(start, end)`)**:
   - Uses binary search (`bisect_right`) over paragraph start offsets to verify that `[start, end)` lies completely within a single paragraph. Prevents false matches formed by concatenating the end of one paragraph with the start of the next.
2. **Source-First Reading Verification**:
   - First attempts `_source_reading(text, rubies, start, end)` using EPUB `<ruby>` annotations and kana characters (`source = "epub"`). Note: group rubies extending past `end` or unannotated kanji/digits return `None`.
   - If the EPUB does not supply a complete reading for the span, reconstructs the reading from the contiguous `tokens` covering `[start, end)` exactly (`source = "sudachi"` or `"epub_and_sudachi"`).
   - Compares hiragana-normalized `actual` reading against `expected = hira(entry["reading"])`:
     - **Exact match (`actual == expected`)**: Accepted with `status: "exact_reading"`.
     - **Unavailable reading**: Accepted with `status: "reading_unverified"`.
     - **Numeric counters / Latin characters**: If any token is `数詞` / `助数詞` (e.g., `本` read `ぽん` instead of `ほん`) or contains ASCII/fullwidth Latin/digits under Sudachi, accepted with `status: "reading_unverified"` and `reason: "counter_pronunciation"` or `"numeral_or_latin_pronunciation"`.
     - **Inflected surface (`inflected_reading_compatible`)**: When surface differs from `entry["word"]` and the final token has a conjugation type (`pos[4] != "*"`), checks stem compatibility for `サ行変格` (`する`/`じる`), `カ行変格` (`くる`), `一段`, `五段`, and `形容詞` (including colloquial `-ねえ` for `-ない`). Accepted with `status: "inflected_reading"`.
     - **Otherwise**: Rejected (`returns None`). This prevents homographs with the wrong reading (e.g., `人気/ひとけ` when the text reads `にんき`) from being accepted.

---

## 4. Furigana Generation & Okurigana Alignment (`immersive/furigana.py`)

[`sentence_ruby(sentence, chapter, worker)`](../enrichement-extractor/immersive/furigana.py#L84-L89) and [`furigana(text, originals, worker)`](../enrichement-extractor/immersive/furigana.py#L77-L81) generate furigana for sentences and examples:

- **Priority 1 (`source: "epub"`)**: All original EPUB `<ruby>` spans within the sentence are preserved verbatim.
- **Priority 2 (`source: "sudachi"`)**: Tokenizes `text` using Sudachi mode C via `worker.tokenize(text, "C")`.
  - Skips tokens without kanji (`KANJI` regex includes `々〆〇`), tokens partially overlapping an EPUB ruby span, or tokens whose reading is not pure hiragana.
  - Calls [`align(surface, reading, relative_originals)`](../enrichement-extractor/immersive/furigana.py#L18-L51) which splits the token into fixed kana/ruby anchors and contiguous kanji runs, then recursively matches kana anchors so **okurigana stays outside `<ruby>` tags** (e.g., `食べる` -> `<ruby>食<rt>た</rt></ruby>べる`).
  - If irregular spelling prevents character-run alignment (and there is no overlapping EPUB ruby), falls back to whole-word ruby.
- Any kanji character left without a ruby span is reported in `book["quality"]["unresolved_furigana"]` via [`unresolved(text, ruby)`](../enrichement-extractor/immersive/furigana.py#L92-L94).

---

## 5. Yomitan Format-3 Dictionary Ingestion (`immersive/yomitan.py`)

[`load_dictionary(path, term_filter, kanji_filter, cancel)`](../enrichement-extractor/immersive/yomitan.py#L113-L217) imports local format-3 Yomitan ZIP archives without extracting files to disk or executing HTML/CSS:

- **Two Operating Modes in `build_data()`** ([`build.py#L117-L147`](../enrichement-extractor/immersive/build.py#L117-L147)):
  1. **Supplementing CSV Decks (`decks` present)**: `term_filter` retains only Yomitan terms matching a CSV word or configured alias. CSV definitions remain default; Yomitan candidates attach to `vocabulary[].dictionary_entries` and `dictionary_metadata`.
  2. **Vocabulary Source without CSV (`not decks`)**: Pre-scans all chapter paragraphs in Sudachi modes A & C to build a `term_filter` of tokens (`surface`, `dictionary_form`, `normalized_form`) and substrings in raw/normalized chapter text. [`vocabulary_entries(dictionaries)`](../enrichement-extractor/immersive/yomitan.py#L220-L239) constructs vocabulary entries where the **first dictionary** in priority order supplies default `definitions`. After alignment, unmatched dictionary candidates are filtered out ([`build.py#L297-L299`](../enrichement-extractor/immersive/build.py#L297-L299)) so the entire dictionary is never shipped in `reader.json`.
- **Safety & Validation**:
  - Rejects path traversal, duplicate archive members, `NaN`/`Infinity` JSON constants, format versions other than `3`, JSON files > 128 MiB, total JSON > 2 GiB, and notice files > 1 MiB.
  - Verifies that the ZIP SHA-256 checksum is identical before and after reading (`"dictionary changed during import"`).

---

## 6. Ambiguity & Sense Selection Model (`immersive/ambiguity.py`)

[`annotate_ambiguity(book)`](../enrichement-extractor/immersive/ambiguity.py#L8-L59) runs at the end of `build_data()`:

1. **Definition Candidates (`vocabulary[].definition_candidates`)**:
   - Each CSV row's `Definitions` string is preserved as a single `source_definition_block` (commas are never split as separate senses because Jiten exports flatten synonyms and parentheticals).
   - Each Yomitan `glossary_text` item is recorded with its `dictionary_id`, `bank`, `row`, and `glossary_index`.
2. **Sense Selection (`vocabulary[].sense_selection`)**:
   - Status is `"reviewed_override"` **only** if `study.config.json` specifies `context_glosses[word]`.
   - Otherwise status is `"unresolved"` (if candidates exist) or `"unavailable"`.
3. **Occurrence & Span Ambiguities (`book["ambiguities"]`)**:
   - Groups all accepted vocabulary occurrences by `(chapter_id, start, end)`.
   - Sets `occurrence["candidate_vocabulary_ids"]` and `occurrence["candidate_status"]`:
     - `"single_candidate"`: 1 entry at this span.
     - `"multiple_entries"`: >1 entries at this span sharing the same hiragana reading.
     - `"ambiguous_reading"`: >1 entries at this span with different readings.
   - Any span with >1 entries is added to `book["ambiguities"]` with `selected_vocabulary_id: None`.

