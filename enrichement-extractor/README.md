# Immersive reading study packs

Local tooling for Japanese EPUBs, exported Jiten vocabulary decks and local Yomitan dictionaries. An EPUB can use a CSV deck or Yomitan term dictionaries as its vocabulary source. The current book has already been built: open [the reading library](output/html/index.html) in your browser. No server, account or network connection is needed to use the generated materials.

Reusable vocabulary, kanji and grammar card data is generated at `output/data/flashcards/library.json`. The card generator and pipeline export remain available without a reviewing application. See [card data and export instructions](docs/card-data.md).

For the code review and proposed reader integration work, see the [reader-first refactoring plan](docs/reader-integration-refactoring-plan.md). It covers the LLM audit, matching correctness, source locations, reusable APIs and optional PDF exports.

Reader enrichment is available independently of study exports. See the [reader enrichment API and commands](docs/reader-enrichment.md) for JSON-only processing, supplementary exports and explicit CSV updates.

[Shared study data](docs/study-data.md) supplies vocabulary, kanji and grammar cards to HTML, PDF and card JSON exporters. It can be prepared without print libraries and reused across exports without modifying reader enrichment.

Both commands use [shared orchestration and layered validation](docs/orchestration-validation.md). Reader validation is independent of print checks; HTML, PDF, card and ZIP outputs can be selected separately.

The fully offline pipeline is distributable as `dist/ImmersivePipeline-windows-x64.zip`. It includes isolated Python, all required libraries, dictionaries, an open Japanese font and editable source. Extract it, put books/decks under `books/<book-name>/`, and run `Run.cmd`. No Python installation, language model, API key or package download is needed. See [portable pipeline instructions](docs/portable-pipeline.md).

## What is generated

- Chapter kanji PDFs and a kanji master use the approved compact layout: a large character at left, dictionary on/kun lists in the middle, and primary kanji meanings plus a separately labelled chapter word/reading/gloss at right. The Japanese example spans a row beneath, followed by unlabelled KanjiVG stroke steps. Each card has a complete outer border and its own practice row below the stroke diagrams, with centre crosshairs. Cards are separated by 7 mm of white space. Pages fit as many full-size entries as space permits, usually three. Covers contain book/publication details, sources, usage guidance and license notices. No card backgrounds are filled.
- Vocabulary has one master PDF, organized into sets of 20 new words. Each chapter also has a vocabulary PDF containing only words first introduced there; set headings flow without forced page breaks. There are no individual set PDFs. Chapter vocabulary remains available in HTML with complete meanings and Japanese examples.
- A grammar master in HTML and PDF, grouped by the first chapter where each supported pattern is detected. Entries have Japanese examples and English construction explanations.
- An offline word/phrase lookup page. Search Japanese spellings, kana readings or English glosses. Chapter pages can hide furigana and blur meanings for practice.
- Chapter HTML and PDFs contain only new items: each vocabulary spelling and each kanji appears as a study card in its first chapter only. Identical vocabulary spellings share one card with their readings and definitions combined. Consolidated master PDFs contain the same unique cards, grouped by first appearance. Complete word and kanji lookup pages remain available.
- Vocabulary learning sets teach 20 new words at a time in first-appearance order across the book: 64 full sets plus a final set of 16 words (1,296 unique aligned spellings). A set may cross a chapter boundary. The single vocabulary master PDF has a cover and one page per set, using white word / reading / meaning rows with recall checkboxes. `vocabulary.html` contains all sets on one page, with complete meanings and expandable Japanese examples. All PDF types have covers, source/license notices and white print backgrounds.
- `reader.json`: source-linked text, book ruby, token annotations, vocabulary, kanji and grammar. `lookup.json`: word/reading to vocabulary-ID index. `quality.json` and a review page retain unmatched entries.

**Story sentences are never translated.** English is used only for dictionary glosses and grammar explanations. HTML and JSON preserve complete deck definitions; vocabulary PDF lists explicitly use gloss excerpts and point to the HTML study pages for full definitions and Japanese examples. Example sentences retain source furigana. HTML can hide furigana. EPUB implementation paths are omitted from study pages and PDFs; source locators remain in the JSON.

Kanji meanings use the openly licensed Kanji alive learner dataset, with an explicitly credited KANJIDIC2 fallback. Character definitions and chapter-word senses are separate. See [meaning sources and provenance](docs/portable-pipeline.md#kanji-meaning-sources).

## Add another book

Future PDF builds insert one completely blank, unnumbered page after every cover, including chapter PDFs and all masters. Study content starts on physical page 3, with printed content numbering starting at 1. Existing PDFs are retained until a rebuild is requested.

Place one `.epub` and one or more UTF-8 Jiten `.csv` files in `books/<book_name>/`. Required CSV fields are `Word`, `ReadingKana`, and `Definitions`; the supplied export's other columns are retained. Multiple decks merge exact dictionary-ID/spelling/reading duplicates without dropping alternate spellings. Builds leave input files unchanged by default. Use `--update-decks` during a study build, or the separate `--decks-only` task, to add or refresh `first_appearance` in source CSVs. See [deck sort keys](docs/portable-pipeline.md#deck-sort-keys).

On this Windows workspace:

```powershell
.venv\Scripts\python.exe -m immersive.cli build
```

For the complete automatic build -> verify -> flashcard JSON -> ZIP workflow:

```powershell
.venv\Scripts\python.exe -m immersive.pipeline
```

Examples follow a deterministic rule: use a nonblank CSV example first; if none exists, use the first sentence where the word appears in the chapter. Aligned CSV examples receive EPUB furigana; unaligned CSV examples remain explicitly source-provided. Examples may come from a later chapter when supplied by the deck. No example is generated or translated. The shared selector is `immersive/examples.py`.

To build one book:

```powershell
.venv\Scripts\python.exe -m immersive.cli build --book 'books/<book_name>'
```

To set up a new environment:

```powershell
$env:UV_CACHE_DIR = "$PWD/.cache/uv"
uv venv --python 3.12
uv pip install --python .venv/Scripts/python.exe -e .
```

The included `uv.lock` pins dependencies for reproducibility (`uv sync --frozen` is an alternative setup). Downloads are only needed during setup. Building does not call online services or use an LLM.

To refresh the cached kanji dictionary and rebuild against it:

```powershell
Invoke-WebRequest 'https://www.edrdg.org/kanjidic/kanjidic2.xml.gz' -OutFile data/kanjidic2.xml.gz
.venv\Scripts\python.exe -m immersive.cli build
```

The Japanese dictionary is cached as `data/kanjidic2.xml.gz`. If absent, download it from the [EDRDG KANJIDIC2 distribution](https://www.edrdg.org/kanjidic/kanjidic2.xml.gz). Use `--kanjidic <path>` for another copy. PDFs prefer the bundled Noto Sans JP TrueType font, embedded into each PDF, with an installed Japanese font as a fallback. To choose another font set `--font <Japanese .ttf or glyf-based .ttc>`. Use `--skip-pdf` for HTML/JSON only.

The portable workflow instead uses the bundled SIL-OFL Noto Sans JP font, with no Windows font dependency. To rebuild the portable tool ZIP from this development environment, run `.venv\Scripts\python.exe scripts/build_portable.py`. It bundles the current compatible dependency versions and preserves package/license metadata; it excludes input books, decks and generated output. Dependencies include PyMuPDF/MuPDF with their AGPL/commercial license notices. See the portable README and bundled license inventory for redistribution terms.

KanjiVG stroke-order data is cached in `data/kanjivg/source.zip`, with release provenance in `source.json`. The supplied cache is the official `r20260714` main SVG release. To refresh it or set up another machine:

```powershell
$release = Invoke-RestMethod 'https://api.github.com/repos/KanjiVG/kanjivg/releases/latest'
$asset = $release.assets | Where-Object name -Match '-main\.zip$' | Select-Object -First 1
New-Item -ItemType Directory -Force data/kanjivg | Out-Null
Invoke-WebRequest $asset.browser_download_url -OutFile data/kanjivg/source.zip
@{ tag=$release.tag_name; url=$asset.browser_download_url; author='Ulrich Apel'; license='CC BY-SA 3.0' } | ConvertTo-Json | Set-Content -Encoding utf8 data/kanjivg/source.json
```

Use `--kanjivg <archive.zip>` for another official main SVG archive. Each build records the archive hash. Original SVGs used by that book and an attribution notice are copied into its output assets. JSON stores ordered path data for reuse by the future reader. Missing diagrams are listed in `quality.json` and are never invented. All 238 characters in the supplied book have KanjiVG data. Printed diagrams show previous strokes in dark ink, the current stroke in red, and future strokes as faint guides. Blank practice boxes follow the diagrams.

Outputs use a content-based book ID and separate formats:

```text
output/
  pdf/<book-id>/       chapter-01-kanji.pdf ... chapter-12-kanji.pdf
                      kanji-master.pdf, vocabulary-master.pdf, grammar-master.pdf
  html/index.html     library entry point
  html/<book-id>/      chapter pages, master pages, offline lookups, CSS/JS
  data/<book-id>/      reader.json, lookup.json, quality.json, vocabulary-sets.json
                      kanji-context.json, original KanjiVG assets, print/ metadata
  previews/<book-id>/  rendered PDF previews/contact sheets
  reports/            verification.json
  study-pack.zip      complete packaged library
```

The PDF directory contains PDFs only. Identical inputs and configuration rebuild the same destinations. Changing EPUB bytes creates a new book ID. Sources are never edited. `vocabulary-sets.json` and `reader.json.learning_sets` preserve study-set membership. Kanji layout and source manifests are stored in `data/<book-id>/print/`. The library index lists books processed by the current build command; earlier book outputs remain on disk.

## Optional book configuration

`study.config.json` in a book folder can set:

```json
{
  "exclude_titles": ["奥付"],
  "include_titles": [],
  "end_before_href": "OEBPS/Text/author-biography.xhtml",
  "aliases": {"dictionary spelling": ["verified spelling in this book"]},
  "context_glosses": {"dictionary spelling": "a reviewed word/phrase gloss"}
}
```

`include_titles` is optional; an empty list means use all non-front-matter navigation entries. `end_before_href` excludes trailing unlisted back matter. Aliases must be verified spellings, not loose homophones. Context glosses replace displayed glosses for that word while the original definitions remain in JSON. Configuration is versionable and reused on each build.

For the supplied book, the navigation has 10 text chapters and 2 illustrated appendices. Both appendices are image-only and are explicitly reported as having no extractable text; their study sheets say so. The book configuration stops before the later author biography. **OCR is not implemented.**

## Accuracy and coverage

Chapter boundaries come from EPUB navigation and spine order, including multiple chapter anchors in a single file. Base text excludes `rt`/`rp` ruby readings. Source ruby is retained separately. Reading examples may span several source paragraphs when an EPUB wraps one sentence over multiple paragraphs.

Vocabulary alignment uses Sudachi tokenization, normalized token sequences, and exact phrases aligned to token boundaries. This handles conjugated phrases and many spelling variants. It does not establish the intended dictionary sense. Proper names, homographs, onomatopoeia and kana ambiguity can still need review. A text-verified deck example is not a claim that its highlighted dictionary sense is correct. Unmatched entries include source front/back matter, unsupported variants and deck extraction errors; they stay searchable, with no invented chapter.

Grammar coverage is deliberately explicit: the current finite rules find common constructions, **not every grammatical feature**. Every grammar entry is a `rule_based_candidate`. First appearance means first detected occurrence in included chapters; it is not a claim that the book teaches that construction there. The editable rules are in `immersive/grammar.json`. Contextual interpretation and broad grammar completeness require a later review/enrichment pass.

The deck exports flattened definitions. Original definitions remain in JSON; print-context cards select a short source gloss and one attested chapter word/reading. The book reading takes precedence when it differs from the deck, and the discrepancy is logged. For this book, 半身 is read はんみ in the EPUB rather than はんしん in the deck. はり金 is missing from the CSV; its supplementary “wire” gloss is attributed to [National Diet Library authority 00562979](https://id.ndl.go.jp/auth/ndlsh/00562979). Source notes are in the quality and print-context JSON. Pitch fields remain opaque source values; no accent diagrams are inferred from undocumented encodings. Dictionary character fields come from KANJIDIC2, and stroke shapes/order from KanjiVG.

## Reader integration

See [the JSON data contract](docs/reader-data.md) and [the JSON schema](immersive/reader.schema.json). IDs connect chapter spans to definitions and Japanese examples. JavaScript can use UTF-16 offsets directly; Python offsets use Unicode code points. A future EPUB reader should map the stored source href/paragraph locators and verify source hashes, rather than treating normalized offsets as raw EPUB DOM positions.

## Verification

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests -v
.venv\Scripts\python.exe scripts/verify_outputs.py
.venv\Scripts\python.exe scripts/package_outputs.py
```

The verifier checks schema, referential integrity, offsets, ruby, local HTML links and anchors, stroke assets, unique study entries, sets of 20 in one vocabulary master, cover/license pages, white print backgrounds, one separate practice row per kanji, PDF text bounds and embedded fonts. It renders contact sheets under `output/previews/<book-id>/` and writes `output/reports/verification.json`. These checks do not replace linguistic review.

## Attribution

Examples, chapter names and source furigana come from the user-supplied EPUB. Word definitions and readings come from the user-supplied [Jiten](https://jiten.moe) deck, with its JMdict IDs retained. JMdict and kanji dictionary fields are copyright James William Breen and the Electronic Dictionary Research and Development Group, under [CC BY-SA 4.0](https://www.edrdg.org/edrdg/licence.html). Kanji fields are derived from [KANJIDIC2, EDRDG](https://www.edrdg.org/wiki/KANJIDIC_Project.html). The original KANJIDIC2 archive is cached unchanged; derived dictionary fields retain attribution and share-alike licensing. SudachiPy/SudachiDict provide segmentation and generated token readings; their installed package notices apply. These dictionary licenses do not grant redistribution rights for the EPUB or its extracted text. Outputs are intended for your local study library.

Stroke paths/order: [KanjiVG](https://github.com/KanjiVG/kanjivg), copyright Ulrich Apel, [CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/). Original SVG assets are unchanged. The cumulative printed diagrams are adaptations that retain this attribution and license. This license applies to KanjiVG-derived graphics/data, separately from the EDRDG dictionary fields.

See [reliable output publication](docs/reliable-publication.md) for immutable reader/study snapshots, compatibility paths, partial exports, cancellation and recovery. The existing distributed portable ZIP must be rebuilt to include these source changes.

[Yomitan dictionary support](docs/yomitan-dictionaries.md) accepts local format-3 ZIPs as supplements or as vocabulary sources for EPUBs without CSV decks. It preserves dictionary candidates, metadata and source notices.

[Shared resources and worker ownership](docs/shared-resources.md) describes reusable resource stores, explicit paths, compiled morphology dictionaries and worker-scoped tokenizers.

See [profiling and caching](docs/profiling-caching.md) for measured results, invalidation and bypass controls, and [ambiguity handling](docs/ambiguity-handling.md) for dictionary candidates and reviewed glosses.
