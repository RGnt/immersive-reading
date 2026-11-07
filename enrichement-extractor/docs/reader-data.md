# Reader JSON contract (v1.0.0)

`reader.json` is a UTF-8 JSON object. It contains no sentence translations and is usable without this Python package. See `immersive/reader.schema.json` for machine validation.

Use the [reader enrichment API or JSON-only command](reader-enrichment.md) to create it independently of PDF generation and CSV updates. Reader-only output omits study-derived `learning_sets`, print-context notes and optional KanjiVG data; readers must treat these fields as optional.

New builds attach `reading_match` evidence to vocabulary occurrences and enforce paragraph boundaries for tokens, occurrences, grammar and sentences. Reading checks preserve explicit uncertainty for counters and Latin spellings. See [matching changes and evidence fields](pipeline-bug-fixes.md#reading-evidence-and-limits). Chapter offsets still refer to normalized text; this does not add an original-DOM source map.

Generated reader files live in `output/data/<book-id>/`; HTML pages live in `output/html/<book-id>/`, and only PDFs live in `output/pdf/<book-id>/`. Original KanjiVG SVG assets are relative to the reader JSON at `assets/kanjivg/`. Print layouts and source provenance are under the data folder's `print/` directory; `kanji-context.json` contains the selected chapter words, readings, glosses and source notes.

| Field | Purpose |
| --- | --- |
| `id`, `source_files` | Content-based book ID and SHA-256 source-file checksums |
| `chapters` | Ordered navigation sections, source href/fragment, normalized text, paragraphs, sentences and tokens |
| `vocabulary` | Stable IDs, spelling, kana, complete source definitions, optional reviewed context gloss, original pitch fields, source deck rows, deck examples and aligned occurrences |
| `kanji` | Character, dictionary fields, occurrences, related vocabulary IDs, first chapter, and KanjiVG `stroke_order` with ordered SVG paths and an original asset link |
| `grammar` | Construction, explanation, first detected chapter, source occurrences and candidate status |
| `quality`, `grammar_coverage` | Unmatched entries, image-only sections and detection limits |
| `learning_sets` | Sets of 20 unique vocabulary spellings in first-appearance order, with global study numbers and owning chapter IDs; only the final set may be shorter |
| `translation_policy`, `attribution` | Explicit display policy and source ownership/licensing |

Every occurrence points to a chapter and usually a sentence. A sentence's `sources` array gives original EPUB href, optional element ID, zero-based paragraph index and paragraph-relative normalized offsets. Paragraph indexes count nonempty source text blocks, including headings. Readings within ruby never contribute to surface-text offsets.

`chapter.text` concatenates nonheading paragraph base text, removing whitespace. Every `start`/`end` span is zero-based, end-exclusive, in **Unicode code points** within that string. Vocabulary occurrences and tokens additionally provide `start_utf16`/`end_utf16` for browser string slicing. Paragraph ruby spans use chapter code-point offsets. Raw EPUB DOM offsets differ because of whitespace, inline elements and ruby readings. A reader must normalize its text and map through the source locators before painting highlights.

Tokens are Sudachi split-mode A units with surface, dictionary form, normalized form, generated kana reading and part of speech. OOV is explicitly flagged. Token `vocabulary_ids` cover exact-span entries. A compound or conjugated phrase may span several tokens: use `vocabulary[].occurrences` to find overlapping/containing candidates, rather than relying only on token-level IDs. Multiple candidates are preserved for ambiguity.

Study-card exports contain only first appearances. Vocabulary cards consolidate identical spellings into one card with combined readings and dictionary glosses, assigned to the earliest matching chapter across those records. Kanji cards likewise occur in their first chapter only. The reader JSON retains individual dictionary records and all occurrences for lookup throughout the book; chapter `vocabulary_ids` and `kanji_ids` describe all encountered items, not just new study cards.

`learning_sets[].words` gives `card_id`, `word`, combined `reading`, `chapter_id` and consecutive `study_number`. Each aligned spelling belongs to exactly one set. Set numbers continue across chapter boundaries, with 20 words per set except the final remainder. The standalone `vocabulary-sets.json` exposes the same membership for study-progress integration. The one-page vocabulary PDFs and HTML study pages use the same memberships and numbering.

`kanji[].stroke_order` is either null (no KanjiVG data) or an object with `view_box`, a `strokes` array ordered by `number`, and `asset`, a relative link to the unchanged original SVG. Each stroke retains its source ID and SVG path `d`. Source attribution is Ulrich Apel / KanjiVG, CC BY-SA 3.0. `build_inputs.kanjivg` records the release provenance and archive SHA-256. This can drive inline stroke-order graphics or animation in a later reader without re-downloading or parsing a dictionary.

Source exceptions are explicit in `quality.context_source_notes` and the print-context data. In the supplied book, 半身 uses the EPUB's はんみ reading instead of the CSV's はんしん. The はり金 gloss is supplemented from National Diet Library authority 00562979 because the CSV lacks the compound; its book spelling and reading are retained. These annotations do not silently change the original deck records.

Example future lookup:

```js
const candidates = data.vocabulary.filter(v =>
  v.occurrences.some(o => o.chapter_id === chapterId &&
    o.start_utf16 <= caretOffset && caretOffset < o.end_utf16));
// Prefer longer phrases when appropriate; let the learner choose ambiguous senses.
// Display v.word, v.reading and v.context_gloss ?? v.definitions.
// Examples remain Japanese and can show the chapter's source ruby.
```

`lookup.json` also maps dictionary spelling and kana reading to vocabulary IDs. It supports a separate lookup field without reparsing an EPUB. Inflected surface forms are resolved through positional annotations/lemmas, not through the dictionary-key index alone.

Vocabulary IDs hash dictionary ID + spelling + reading. Exact duplicates across deck files merge provenance; alternate spellings remain separate records. Sentence IDs hash source navigation target + normalized sentence position + text. They remain stable for an unchanged EPUB/configuration; resegmentation or source edits may change them. Chapter IDs are ordered IDs within one book, so always scope them by book ID.

No machine-generated field is presented as a reviewed sense translation. All grammar entries currently have `rule_based_candidate` status. The source deck's original examples have independent textual-verification status, which does not validate their dictionary sense.

`vocabulary[].selected_examples` maps chapter IDs to the deterministic example choice. A nonblank CSV example has priority; without one, the first aligned occurrence's sentence in that chapter is used. `source` is `csv`, `epub_first_occurrence`, or `unavailable`. The object records text, source file/row, actual example chapter, start/end offsets when aligned, and status. A supplied CSV example can be from a later chapter or can remain explicitly unaligned. `kanji-context.json` and rendered vocabulary card data expose the same `example_provenance` record. Optional editorial corrections are book configuration data; the normal pipeline runs without them or any language-model calls.

[Yomitan dictionary support](yomitan-dictionaries.md) accepts local format-3 ZIPs as supplements or as vocabulary sources for EPUBs without CSV decks. It preserves dictionary candidates, metadata and source notices.

[Explicit ambiguity metadata](ambiguity-handling.md) preserves source definition blocks, reading ties and review status. Matched study cards exclude rejected homograph readings.
