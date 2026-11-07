# Reusable card data

The extractor generates cards without a review application or progress storage.

Run the complete pipeline, or export cards from already built books:

```powershell
.venv\Scripts\python.exe scripts/export_flashcards.py
```

The implementation lives in `immersive.card_export`, independently of HTML/PDF rendering. You can also run `.venv\Scripts\python.exe -m immersive.card_export`, or call `export_book(book, study=prepared)` with an existing model. See [shared study data](study-data.md) for preparation and reuse.

Default output: `output/data/flashcards/library.json`. Use `--input <output-root>` and `--output <json-path>` for other locations. The main pipeline exports the same file automatically and includes it in `output/study-pack.zip`.

UTF-8 JSON uses `schema_version: "1.0"` and a `books` array. Each book has `id`, `title`, `attribution`, and `chapters`; each chapter has `id`, `number`, `title`, and `cards`.

Card fields:

| Field | Contents |
| --- | --- |
| `id` | Stable card identifier within its book |
| `kind` | `vocabulary`, `kanji`, or `grammar` |
| `prompt` | Word, character, or grammar construction |
| `meaning` | English word/phrase explanation or primary kanji meanings |
| `reading` | Kana or dictionary on/kun readings |
| `example` | Untranslated Japanese example from the supplied sources |
| `example_ruby` | Furigana spans with relative Unicode-code-point `start`/`end`, `surface`, `reading`, and `source` (`epub` or `sudachi`) |
| `notes` | Context, source attribution, and grammar review notes |
| `set_number` | Existing vocabulary learning set; `0` for other card kinds |

Each vocabulary spelling and kanji is assigned to its first chapter only. Grammar cards are rule-based candidates. No sentence translations, review history, or scheduling data are added. Richer dictionary records, offsets and stroke-order assets remain in each book's `reader.json` and associated output data.

[Yomitan dictionary support](yomitan-dictionaries.md) accepts local format-3 ZIPs as supplements or as vocabulary sources for EPUBs without CSV decks. It preserves dictionary candidates, metadata and source notices.

[Explicit ambiguity metadata](ambiguity-handling.md) preserves source definition blocks, reading ties and review status. Matched study cards exclude rejected homograph readings.
