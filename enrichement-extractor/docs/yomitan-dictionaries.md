# Yomitan dictionaries

The pipeline accepts local **format-3 Yomitan/Yomichan dictionary ZIPs**, offline. It can supplement a Jiten CSV or build vocabulary from an EPUB plus term dictionaries without any CSV. Dictionaries remain user-supplied resources; nothing is downloaded or updated automatically.

## Install and run

Put dictionary ZIPs in `data/yomitan/`. Builds discover `*.zip` there in filename order. Alternatively, repeat `--yomitan` to supply an explicit list in priority order; explicit paths replace discovery.

```powershell
# Reader enrichment; the book folder may contain just its EPUB:
.venv\Scripts\python.exe -m immersive.cli enrich --book 'books/my-book' --yomitan 'dictionaries/terms.zip' --yomitan 'dictionaries/frequency.zip'

# Supplementary HTML, PDFs, cards and study-pack ZIP:
.venv\Scripts\python.exe -m immersive.pipeline --book 'books/my-book' --yomitan 'dictionaries/terms.zip'

# Validate selected dictionaries and core offline dependencies:
.venv\Scripts\python.exe -m immersive.pipeline --doctor --reader-only --yomitan 'dictionaries/terms.zip'
```

An EPUB still needs the local Sudachi dictionary, KANJIDIC2 and Kanji alive resources. Yomitan kanji data supplies additional candidates; it does not replace the approved primary-kanji-meaning policy. PDF builds additionally need the usual fonts and KanjiVG. The book folder must contain exactly one EPUB. CSV updates require an actual CSV; dictionary ZIPs are never modified.

## Selection and meanings

With a CSV, its vocabulary list, definitions, examples and IDs remain authoritative study inputs. Yomitan entries supplement those words and configured aliases, filtered by spelling and compatible kana-normalized reading. All compatible senses remain candidates; a match does not establish a contextual sense.

Without a CSV, the importer considers dictionary expressions found literally in the book and expressions identified through offline token lemmas/normalized forms. The existing aligner then checks readings, inflections, phrases and paragraph/token boundaries. Only entries with accepted occurrences enter the reader bundle. This reuses the pipeline's matching rules; it is not Yomitan's complete deinflection/search engine and may miss lexical variants or unusually segmented phrases. Unknown words do not acquire invented definitions.

Entries with the same spelling and kana-normalized reading merge across dictionaries. The first dictionary containing that entry supplies the default definitions, combining its supplied senses in bank/row order. Later dictionaries remain available as separate candidates. Reordering dictionaries changes this default without changing the vocabulary ID. Monolingual dictionaries are supported; definitions stay in their original language, with `gloss_language` recording the source language or `mul`/`und` when appropriate. There is no translation or model step.

CSV examples have priority when supplied. Otherwise examples come from the first accepted occurrence in each chapter, with source furigana and offline reading completion. Dictionary examples embedded inside a definition are retained as dictionary content; they never replace the selected book example. No story sentences are translated. Study cards still deduplicate first appearances and use vocabulary sets of 20.

## Python API and reusable data

```python
from pathlib import Path
from immersive.enrichment import enrich_book
from immersive.orchestration import run_job, BuildOptions
from immersive.yomitan import load_dictionary

book = enrich_book(Path("books/my-book"), Path("data"),
                   yomitan_paths=[Path("dictionaries/terms.zip")])
# yomitan_paths=None discovers resources/yomitan/*.zip.
# yomitan_paths=[] explicitly disables discovery.

result = run_job(Path("books"), Path("output"), Path("data"),
                 book_folder=Path("books/my-book"),
                 options=BuildOptions(),
                 yomitan_paths=[Path("dictionaries/terms.zip")])

# Standalone importer, optionally retaining a limited vocabulary:
dictionary = load_dictionary(Path("dictionaries/terms.zip"),
                             term_filter={"海", "見る"}.__contains__,
                             kanji_filter={"海", "見"})
```

The importer takes an optional cooperative `cancel` token, as do enrichment and orchestration. With no filters, `load_dictionary` retains every entry; book builds filter candidates and process one JSON bank at a time.

Reader JSON adds these optional fields:

| Field | Contents |
| --- | --- |
| `dictionary_sources` | Archive SHA-256 identity, original index metadata, revision, bank counts, tags and supplied notice files |
| `build_inputs.yomitan` | Ordered archive filenames and SHA-256 fingerprints |
| `vocabulary[].dictionary_entries` | Matching term candidates with original glossary JSON, plain-text previews, reading, tags, rules, score and sequence |
| `vocabulary[].dictionary_metadata` | Reading-compatible frequency, pitch and IPA records; original payloads retained |
| `kanji[].dictionary_entries` / `dictionary_metadata` | Character dictionary candidates, stats and frequency metadata |
| `vocabulary[].vocabulary_source` | `yomitan` for vocabulary constructed without a CSV |
| `vocabulary[].definition_dictionary_id` | Dictionary providing that dictionary-only entry's default definitions |

Each imported record refers to its source by `dictionary_id`, bank filename and **one-based row**. Its title and revision are also retained. Dictionary-only `sources` records use these dictionary locators instead of `file`/`csv_row`; consumers must distinguish source types. Original EPUB/CSV hashes remain in `source_files`; external dictionaries are fingerprinted separately. The book identity still derives from EPUB bytes. Dictionary-only vocabulary IDs hash spelling and normalized reading in a separate namespace from Jiten IDs.

HTML exposes escaped plain-text candidate definitions in expandable sections and a dictionary-notices page. Card JSON retains candidates and source notices. PDFs use default definitions and identify Yomitan word glosses; covers summarize supplied attribution and point to complete notices in reader JSON. Raw structured glossary data must remain untrusted data in future readers; do not assign it directly to HTML or execute its links/styles.

## Supported format and limits

Supported banks: `term_bank_N.json`, `kanji_bank_N.json`, `term_meta_bank_N.json`, `kanji_meta_bank_N.json`, `tag_bank_N.json`. Index `format: 3` and its legacy `version: 3` alias are accepted. Format 1/2 archives are rejected with an actionable error.

Definitions may be strings, text objects, structured content or image references. Structured content is retained losslessly, with a plain-text preview for current displays. Archive media and CSS are **not extracted, rendered or fetched**; image references/alt text remain in JSON. This is dictionary-data support, not a full reproduction of Yomitan's rich dictionary UI. Metadata supports frequency values (including reading-specific/display values), pitch candidates and IPA. It does not substitute dictionary frequency/pitch data into original Jiten fields.

The importer rejects malformed bank shapes, missing index metadata, conflicting versions, duplicate ZIP members, unsafe member paths, nonfinite JSON numbers and unsupported metadata modes. JSON members are limited to 128 MiB each and 2 GiB total; supplied notice files to 1 MiB each. Very large dictionaries need appropriately split banks. Identical archives selected from different filenames are rejected rather than producing duplicate source IDs.

Index metadata and common LICENSE/LICENCE/COPYRIGHT/attribution notice files are preserved. Dictionary licenses belong to their individual publishers; the Yomitan file format does not assign one license to all dictionary content. Study ZIPs include the selected book's dictionary data/notices, not the original dictionary ZIPs. Publication checks selected dictionary hashes and discovered file lists before committing, alongside EPUB/CSV/config inputs.

The implementation follows the official [index schema](https://github.com/yomidevs/yomitan/blob/master/ext/data/schemas/dictionary-index-schema.json), [term schema](https://github.com/yomidevs/yomitan/blob/master/ext/data/schemas/dictionary-term-bank-v3-schema.json), [kanji schema](https://github.com/yomidevs/yomitan/blob/master/ext/data/schemas/dictionary-kanji-bank-v3-schema.json) and [term metadata schema](https://github.com/yomidevs/yomitan/blob/master/ext/data/schemas/dictionary-term-meta-bank-v3-schema.json). Runtime parsing is local and does not fetch these schemas.

## Verification

All 97 tests passed. Authored ZIP fixtures cover structured definitions, licenses/tags, frequency/pitch/IPA, dictionary priority, homographs, inflections and phrases, CSV preservation, dictionary-only EPUB examples with furigana, HTML escaping, card/ZIP export, invalid archives, cancellation, source changes and reader-only execution with network/print imports blocked. A dictionary-only full-study fixture also produced and verified seven PDFs (37 pages), HTML, cards and a ZIP; representative cover, kanji and vocabulary pages were visually inspected. Existing books and the distributed portable ZIP were not rebuilt.

[Shared resources and worker ownership](shared-resources.md) describes reusable resource stores, explicit paths, compiled morphology dictionaries and worker-scoped tokenizers.
