# 15 — jiten.moe plugin and Deck Builder plugin

Status: implemented. Code: `immersive-reader/src/Plugins/ImmersiveReading.Plugins.JitenMoe`, `.../ImmersiveReading.Plugins.DeckBuilder`. Tests: `tests/ImmersiveReading.Core.Tests/JitenMoeAndBookInfoSpecsTests.cs`, `tests/ImmersiveReading.App.Tests/DeckBuilderTests.cs`, `AppServicesContainerTests`, and `--verify-ui` (`pluginUi=ok(... jitenMoe tab+badge+series action, deckBuilder tab registered)`).

## 1. Goals
- **FR-1** All jiten.moe functionality (search, deck detail, difficulty/stats/vocabulary preview, apply metadata, series matching, deck.csv download) lives in the `jitenMoe` plugin. Core has no jiten-specific code except the library's `jiten_moe_url` / `jiten_difficulty` custom-field definitions (data).
- **FR-2** The library UI exposes generic plugin slots instead of hard-coded jiten widgets (see [wiki/dotnet-architecture.md](../wiki/dotnet-architecture.md) §10).
- **FR-3** A second plugin, `deckBuilder`, builds a vocabulary deck from a book **locally**, copying the *way* jiten.moe builds decks, and writes the `deck.csv` the Python pipeline already consumes.
- **FR-4** Attribution: the DeckBuilder plugin carries `NOTICE-Jiten.md` and the Apache-2.0 text, source files carry a header comment, the UI shows the credit.

## 2. Plugin 1: `jitenMoe`
| Old (core) | New |
| :--- | :--- |
| `library.searchJitenMoe`, `getJitenDeckDetail`, `getJitenDeckDifficulty`, `getJitenDeckStats`, `getJitenVocabularyPreview` | `ext.jitenMoe.search`, `getDeckDetail`, `getDeckDifficulty`, `getDeckStats`, `getVocabularyPreview` |
| `library.applyJitenMetadata`, `matchSeriesOnJiten`, `applySeriesJitenMapping` | `ext.jitenMoe.applyMetadata`, `matchSeries`, `applySeriesMapping` |
| `library.downloadJitenDeckCsv`, `downloadSeriesDecksCsv` | `ext.jitenMoe.downloadDeckCsv`, `downloadSeriesDecksCsv` |
| `library.getSeriesVolumes` | removed (unused by the UI; the plugin lists books itself via `IHostLibrary.ListBooks`) |
| `JITEN_API_KEY` env / `.env` only | plugin setting `apiKey` first, then env / `.env` |
| deck.csv fallback dir `AppContext.BaseDirectory/downloads` | `<plugin data dir>/downloads` |

Host additions: `IHostLibrary` + `IPluginHostContext.Library`; UI slots `registerBookBadge`, `registerSeriesAction`, Book Info tabs actually rendered, `refreshLibrary()` in `@ir/sdk`.
The Book Info "jiten.moe & Custom Fields" tab became a generic "Custom Fields" tab; the jiten UI is now the plugin's "jiten.moe" tab, which applies metadata directly through the plugin RPC (no more "click Save to persist").

## 3. Plugin 2: `deckBuilder` — how jiten.moe builds decks (and what we copied)
Source studied: <https://github.com/Sirush/Jiten> (Apache-2.0): `Jiten.Parser/Parser.cs` (`ProcessSentencesToDeck`), `ExampleSentenceExtractor.cs`, `Jiten.Api/Services/DeckDownloadService.cs`, `DeckFormat/Order/DownloadType`.

1. **Text** → paragraphs (`BookTextExtractor`, ruby `rt/rp` dropped) → sentences (`SentenceSplitter`: 。！？… ends a sentence, a 「…」 pair never splits inside).
2. **Parse** each sentence with the app's Japanese tokenizer (MeCab); drop symbols and tokens without kana/kanji.
3. **Resolve** each distinct (lemma, reading) to a dictionary entry in **one batch** (`SqliteDeckDictionary`: imported Yomitan dictionaries, priority order, reading prefix breaks homograph ties; frequency rank and pitch from term-meta).
4. **De-duplicate in first-appearance order**; the first entry carries the occurrence total (`DeckParser`). Key = dictionary sequence + expression (jiten: `WordId` + `ReadingIndex`).
5. **Statistics** as jiten's `Deck`: characters, words, unique words, unique-used-once, unique kanji (+ used once), sentences, dialogue % (text inside 「…」/『…』 up to 200 chars).
6. **Example sentences** (`ExampleSentenceSelector`): eligible = ≥ 6 distinct characters and ≥ 2 content words; passes 15–35 / 10–45 / 10–55 characters over the first 25 % / 50 % / 100 % of the book; longest first; a sentence is used once and serves every word still waiting; brackets balanced, repeated final mark collapsed.
7. **Export** (`DeckExporter`): orders chronological / deck frequency / global frequency; selections full / top N / target coverage % / min occurrences; formats CSV, TXT, TXT-repeated. CSV columns are jiten's: `Word, ReadingFurigana, ReadingKana, Occurences, ReadingFrequency, PitchPositions, Definitions, ExampleSentence, JmDictWordId` (all quoted, newlines flattened so every record is one line; the Python `load_decks` rejects malformed rows).

Differences: MeCab instead of Sudachi.rs and no resegmentation / misparse-repair stages; definitions from the user's imported dictionaries instead of JMdict directly (a JMdict-based Yomitan dictionary gives the same ids and glosses); no sentence-difficulty scoring; no Anki `.apkg` (needs jiten's Lapis template).

### Invariants
- **Offline**: no network call; nothing leaves the machine.
- **Source input immutability**: the EPUB is only read. `deck.csv` is written only when the user presses Build; an existing file is replaced only after an explicit "Replace existing file" (`overwrite: true`), via temp file + move.
- No story sentence is translated; English appears only as dictionary glosses (existing project rule).

## 4. Risks / open work
- Quality depends on the imported dictionaries: with none, every word is "not in dictionary" (the UI says so) and Definitions are empty.
- Ordering by *global* frequency needs a frequency dictionary; unknown ranks sort last.
- Not done: "build decks for a whole series" action (the `registerSeriesAction` slot is ready for it), progress reporting for very long books.
