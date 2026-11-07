# Portable Japanese reading pipeline

Windows x64, Python 3.12. The ZIP includes its own isolated Python runtime, required libraries, dictionaries, a redistributable Japanese font, source code and third-party notices. It does not need a Python installation, pip, an API key, a language model or an internet connection to run.

## Use

1. Extract the entire ZIP to a writable folder. Keep its folders together.
2. Under `books`, create one folder per book and place **one EPUB** and optional UTF-8 Jiten CSV decks in it. Without CSVs, supply local Yomitan term dictionaries under `data/yomitan/` or with repeated `--yomitan` arguments (requires a rebuilt release).
3. Double-click `Run.cmd`, or run it in a terminal. Outputs are written under `output`.
4. Open `output/html/index.html`, or use the PDFs under `output/pdf/<book-id>/`.

```text
ImmersivePipeline-windows-x64/
  Run.cmd
  Check.cmd
  python/                 isolated runtime and vendored packages
  app/                    editable modular Python source, scripts and tests
  data/                   KANJIDIC2, KanjiVG and Noto Sans JP
  books/<book-name>/       your EPUB + optional CSV inputs
  output/                 generated PDF / HTML / JSON / previews / reports
  licenses/               dependency inventory and third-party notices
```

No input books, decks or generated study content are included in the distribution. Add only material you are entitled to use. Runs read the files on disk each time; no progress database or cloud service is used.

`Check.cmd` checks the Python runtime, native libraries, Japanese tokenizer, dictionaries and font without needing a book.

```bat
Run.cmd --doctor
Run.cmd --books "D:\My books" --output "D:\Study packs"
```

Default locations are relative to the extracted package, so the launcher works from a shortcut or another working directory. The whole package can be moved or renamed. Do not copy only `python.exe` or `Run.cmd`.

## Deck sort keys

Source CSV updates are explicit: use `--decks-only` as a separate task or `--update-decks` during a study build. Ordinary builds leave source files unchanged. The update task adds or refreshes `first_appearance` in every source deck CSV beside its EPUB. Sort this column **numerically ascending**, with blanks last, to follow the book's reading order. The pipeline keeps the original row order, column order and other field values, including duplicate records and multiline examples. Existing `first_appearance` values are recalculated; no duplicate column is added. Files are replaced atomically after a complete write.

The value is the **1-based Unicode character position** in the concatenation of the included chapters' extracted text, in EPUB reading order. It is not a page number or vocabulary rank. It uses the same token/reading-aware alignments as the study materials (including conjugations), not a raw substring search. Words at the same position share a value. Unmatched entries are blank, rather than assigned an invented position. These are detected appearances, subject to the aligner's coverage and the configured chapter selection.

To update decks without rendering PDFs, HTML or packages:

```bat
Run.cmd --decks-only
```

To produce reader JSON independently of PDFs and CSV modification, use `Run.cmd --reader-only --book "books/my-book"`. See [reader enrichment](reader-enrichment.md) for the API and task boundaries. These options require a portable release rebuilt from the updated source; older ZIPs retain their original behavior.

From a development checkout: `.venv\Scripts\python.exe -m immersive.pipeline --decks-only`.

## Kanji meaning sources

Primary character meanings use Kanji alive's `kmeaning` field, pinned to the commit and SHA-256 in `data/kanji-alive/source.json`. Copyright Harumi Hibino Lory & Arno Bosse, CC BY 4.0: https://kanjialive.com/credits/ . The upstream CSV, license and data documentation are cached locally; normal builds need no network or model. Only the kanji meaning field is consumed, not external example sentences, mnemonic hints or word definitions.

For characters outside the 1,235-character learner dataset, the full KANJIDIC2 English meaning list is retained. KANJIDIC2 does not rank primary senses; fallback entries are explicitly identified rather than arbitrarily taking the first gloss. JSON retains the original dictionary meanings plus `primary_meanings` and per-character `meaning_source` with licensing and source version. Source coverage is recorded in `quality.json`.

PDFs distinguish **KANJI MEANINGS** from **WORD MEANING (DECK)**. The latter remains a deck word sense and can require contextual review; it never supplies the character definition. HTML lookup and kanji flashcards use the same primary character meanings. This change does not claim automatic word-sense disambiguation.

## Example furigana

Every displayed Japanese example uses a shared offline furigana renderer. Original EPUB ruby has priority. SudachiPy/SudachiDict-core supplies missing readings; kana anchors keep okurigana outside ruby where alignment is possible. Unaligned CSV examples receive the same generated readings. Story text and punctuation are retained, with no sentence translation.

Generated readings are estimates, especially for names and homographs. Each annotation records `source: epub` or `source: sudachi`. Original paragraph ruby is never overwritten or used as if generated readings came from the book. `sentences[].display_ruby`, vocabulary `selected_examples[].ruby`, kanji context cards and exported flashcard `example_ruby` retain structured readings for reuse. Their offsets are relative Unicode code points in the displayed sentence. Characters with no usable reading are recorded in `quality.json` under `unresolved_furigana`.

## Example-selection rule

- A nonblank example in the word's relevant CSV record is used first. The `**` target markers are removed.
- The example is matched against the EPUB to recover source furigana when possible. A location in the selected chapter is preferred, then a location elsewhere in that same book. A CSV example may therefore come from a later chapter.
- A supplied CSV example that cannot be aligned is retained as source-provided text and explicitly marked unaligned. Furigana is not invented for it.
- If the CSV provides no example, the **first sentence containing the word in the selected chapter** is used. Inflected forms are resolved by the Japanese token annotations.
- If neither source provides usable text, the entry is marked unavailable. No example is generated or translated.

The same selector is used by vocabulary cards, kanji context cards and the flashcard-data export. Provenance fields record `csv`, `epub_first_occurrence` or `unavailable`, the source file/row, the actual example chapter, and alignment status. Original deck examples remain in the reader JSON.

## Automatic processing and limits

The pipeline reads EPUB navigation and ruby, parses Jiten decks, tokenizes Japanese, aligns entries and conjugated phrases, groups first appearances into chapters and vocabulary sets of 20, creates covers/PDFs/HTML, verifies outputs, exports flashcard JSON and packages `study-pack.zip`.

Grammar uses finite, editable rules in `app/immersive/grammar.json`. It is explicitly labelled as candidate detection, not an exhaustive grammar analysis. Dictionary senses are not semantically disambiguated by a model. Print glosses are short excerpts of source dictionary definitions.

An absent contextual word gloss does not stop a new book: the card uses a clearly labelled general character meaning from KANJIDIC2, retaining the book reading when available. Missing readings, stroke-order data, unmatched vocabulary and source differences are reported. The pipeline does not invent translations or require a manual annotation step.

Optional `study.config.json` in a book folder can set chapter exclusions/end boundaries, verified aliases and reviewed glosses. Optional `print_context` corrections are data, not executable book-specific logic. They can select a deck word, a source surface or a reviewed gloss. Existing reviewed corrections for the supplied development book remain in that book's config; they are not needed for a new book to run and are not shipped as personal inputs in this ZIP.

## Outputs

Optional kana chart JPGs named `hiragana-chart-by-tofugu.jpg` and `katakana-chart-by-tofugu.jpg` can be placed under `data/charts/`. When present, each PDF ends with one full-page chart per image, rotated 90 degrees and scaled proportionally within the page margins. The supplied images retain their embedded Tofugu attribution.

- `pdf/<book-id>/`: chapter kanji and vocabulary PDFs plus kanji, vocabulary and grammar masters. Vocabulary has one master, with a page per set of up to 20 words, and one PDF per chapter with flowing tables of that chapter's new words. No per-set files are created. Covers carry sources and license notices; study pages use white backgrounds. Each bordered kanji card includes a practice row below its stroke diagrams, with extra space between cards.
- `html/<book-id>/`: chapter pages, complete word/kanji lookups and masters.
- `data/<book-id>/`: reader data, lookup indexes, selected examples, context provenance, quality notes and print layouts.
- `data/flashcards/library.json`: reusable book -> chapter -> card data, independent of a reviewing application.
- `previews/` and `reports/`: rendered QA previews and verification results.
- `study-pack.zip`: the generated study library, separate from this tool distribution.

Running the pipeline builds all stages by default. `--skip-verify` is available for a faster draft. A missing EPUB or CSV is an input error; encrypted, malformed or image-only content is not silently OCR-transcribed.

## Editing and redistributing

The implementation is ordinary Python source under `app/immersive/`. Main stages are `epub.py`, `build.py`, `examples.py`, `context.py`, `kanji_pdf.py`, `master_pdf.py`, `render_outputs.py` and `pipeline.py`. Resources and source modules can be updated independently as long as compatible package versions are kept together. There is no relocation-dependent virtual environment.

Python and dependency license/NOTICE files are retained. See `licenses/dependencies.json`, `licenses/THIRD-PARTY-NOTICES.txt`, and the package license files under `python/Lib/site-packages`. In particular, the bundled PyMuPDF/MuPDF components have AGPL/commercial license terms; source code for this pipeline is included. Dictionary extracts and stroke graphics retain their own EDRDG/KanjiVG share-alike attribution. Noto Sans JP is under SIL OFL 1.1. Redistribution must follow these included licenses; the input book's copyright is separate.

This archive targets Windows x64 only. A macOS or Linux release needs a runtime and native wheels built for that platform; changing a filename is insufficient.

See [reliable output publication](reliable-publication.md) for immutable reader/study snapshots, compatibility paths, partial exports, cancellation and recovery. The existing distributed portable ZIP must be rebuilt to include these source changes.

[Yomitan dictionary support](yomitan-dictionaries.md) accepts local format-3 ZIPs as supplements or as vocabulary sources for EPUBs without CSV decks. It preserves dictionary candidates, metadata and source notices.

[Shared resources and worker ownership](shared-resources.md) describes reusable resource stores, explicit paths, compiled morphology dictionaries and worker-scoped tokenizers.

See [profiling and caching](profiling-caching.md) for measured results, invalidation and bypass controls, and [ambiguity handling](ambiguity-handling.md) for dictionary candidates and reviewed glosses.
