# Attribution: jiten.moe

The **Deck Builder** plugin re-implements, in C# on top of this application's own tokenizer and dictionary
stack, the way [jiten.moe](https://jiten.moe) builds its vocabulary decks. The design, the data contract and the
selection rules come from the open-source jiten.moe project:

- Project: **Jiten** by Sirush and contributors, <https://github.com/Sirush/Jiten>
- License: **Apache License 2.0** (a copy is in `LICENSE-Jiten-Apache-2.0.txt`)
- Website: <https://jiten.moe>

## What was adapted, and from where

| Deck Builder piece | Based on (Jiten source) | What we kept | What is different here |
| :--- | :--- | :--- | :--- |
| `DeckParser` word counting | `Jiten.Parser/Parser.cs` (`ProcessSentencesToDeck`) | Tokenise sentence by sentence, resolve each token to a dictionary word, de-duplicate in **first-appearance order**, the first entry carries the **occurrence total** | MeCab (this app's Japanese plugin) instead of Sudachi.rs; no resegmentation / misparse-repair stages |
| Deck statistics | same (`Deck` fields) | `CharacterCount`, `WordCount`, `UniqueWordCount`, `UniqueWordUsedOnceCount`, `UniqueKanjiCount`, `SentenceCount`, `DialoguePercentage` (dialogue = text inside 「…」 / 『…』, max 200 chars) | none |
| `ExampleSentenceSelector` | `Jiten.Parser/ExampleSentenceExtractor.cs` | A sentence is eligible with ≥ 6 distinct characters and ≥ 2 content words; three passes with length windows 15–35 / 10–45 / 10–55 over the first 25 % / 50 % / 100 % of the book; longest candidates first; each sentence is used once and claims every still-waiting word it contains; bracket balancing and trailing-punctuation normalisation | Words are matched by lemma key rather than Jiten's JMdict word id; no difficulty scoring |
| `DeckExporter` | `Jiten.Api/Services/DeckDownloadService.cs`, `DeckFormat`, `DeckOrder`, `DeckDownloadType` | CSV column contract (`Word, ReadingFurigana, ReadingKana, Occurences, ReadingFrequency, PitchPositions, Definitions, ExampleSentence, JmDictWordId`, the historic `Occurences` spelling included), `**word**` marking in the example sentence, TXT and repeated-TXT formats, orders (chronological, global frequency, deck frequency) and download types (full, top N, target coverage, occurrence count) | Definitions come from the dictionaries imported into this app (Yomitan), not from JMdict directly; no Anki `.apkg` export |

The `Kind`, `PartOfSpeech` and `UsageNote` columns and `deck-grammar.json` are extensions of this application, not part of jiten.moe's format.

No Jiten source file is copied verbatim; the algorithms are re-written. Per Apache-2.0 §4 this notice states what
was derived and from where.

## Credits inherited from the Jiten project

Jiten credits the tools behind its parser; the ideas above rely on them upstream:
[Sudachi.rs](https://github.com/WorksApplications/sudachi.rs),
[Nazeka](https://github.com/wareya/nazeka) and [JL](https://github.com/rampaa/JL) (deconjugation),
[Ichiran](https://github.com/tshatrov/ichiran),
[JMdict](https://www.edrdg.org/wiki/index.php/JMdict-EDICT_Dictionary_Project) and
[JmdictFurigana](https://github.com/Doublevil/JmdictFurigana).

This plugin is not affiliated with or endorsed by jiten.moe. Decks built here are generated locally from your own
copy of a book, no data is sent to jiten.moe.
