# 16 — Deck LLM enrichment: function words (particles) and chapter grammar

Status: implemented. Code: `immersive-reader/src/Plugins/ImmersiveReading.Plugins.DeckBuilder/Enrichment/*`, `DeckBuilderPlugin.cs`; Python `enrichement-extractor/immersive/deck_grammar.py`, `build.py`, `study_data.py`, `render_outputs.py`, `master_pdf.py`. Tests: `tests/ImmersiveReading.App.Tests/DeckEnrichmentTests.cs`, `DeckBuilderTests.cs`, `enrichement-extractor/tests/test_deck_function_words.py`. Builds on [spec 15](15-jiten-plugin-and-deck-builder-spec.md).

## 1. Goals
- **FR-1** Particles and auxiliary verbs are **marked** in the deck (`Kind`) so study printouts list them in their own section instead of mixing them into the vocabulary sets.
- **FR-2** An LLM (any OpenAI-compatible endpoint, e.g. FreeToken, 256k context) adds **the grammar used in each chapter** and a **usage note for every particle / auxiliary**.
- **FR-3** Nothing the model says about the *book* can be invented: every example must be a verbatim sentence of the book.
- **FR-4** The Python pipeline stays offline and deterministic.

## 2. Design decisions (and why)
| Decision | Why |
| :--- | :--- |
| `Kind` is derived **deterministically from MeCab's part of speech** (助詞 → `particle`, 助動詞 → `auxiliary`), not by the LLM | Free, instant, reproducible and exact. A model adds nothing to a classification the tokenizer already makes; its value is explaining *how* each particle is used |
| The LLM runs **only in the .NET DeckBuilder plugin**; Python reads the result as data | AGENTS.md invariant 1 (no LLM / no network in `enrichement-extractor`) is untouched. `deck-grammar.json` is an input file like the CSV; `deck_grammar.py` has no network or model code |
| A **dedicated small chat client** (`OpenAiChatClient`), not the AI companion's `ILlmProvider` | The companion provider falls back to canned quiz answers when the endpoint fails (`EnableOfflineHeuristicFallback`). A deck must come from the model or not at all. The client reports HTTP status and body, retries 429/5xx/timeouts with backoff, retries once without `response_format` if the server rejects it, and treats bad key / URL / model as fatal |
| Endpoint, model and key: the **plugin's own LLM endpoint** when set (`llmEndpointUrl`, `llmModelId`, `llmApiKey`), otherwise **Settings → AI** (`LlmSettings`) | The deck model is often a different (larger, local) model than the chat companion's, e.g. a FreeToken host. Empty = unchanged behaviour. The own endpoint accepts a base URL (`…/v1`, `/chat/completions` is appended) or a full chat-completions URL, needs no model id (the server's loaded model is used) and never borrows the model or key of Settings → AI. `llmThinking` (`off` default) sends `chat_template_kwargs.enable_thinking=false` for the own endpoint only, because reasoning models otherwise spend the answer budget thinking; Settings → AI requests are never changed. `llmContextTokens` (0 = unknown) sizes requests for servers that enforce less than they advertise: answer ≤ 40 % of it, prompt ≤ (context − answer − 1,500) / 1.5 characters, points ≤ answer/200. A **Test connection** button (settings, `testEndpoint` RPC) lists the models of `GET …/models` and the advertised context |
| The model **cites sentences by id** and a `surface` substring; the plugin maps ids to the real text | The model never writes a quote of its own, so a hallucinated sentence cannot enter the material. Points whose examples do not verify are dropped and counted (`Warnings`) |
| Python **re-verifies** examples against the EPUB chapters | Defence in depth and independence from the plugin: a hand-edited or stale file cannot inject text that is not in the book |
| Chunking by chapter, `MaxChunkChars` (default 60,000) | A 256k-token model could take a whole book in one request, but per-chapter requests give per-chapter grammar with progress, resumability and a bounded blast radius. Raise the setting to use fewer, larger requests |
| Result persisted in `<plugin data>/enrichment/<bookId>.json`, keyed by the EPUB write time | A restart, a rebuilt deck or a re-export never costs tokens again; a changed book invalidates it |

## 3. Contracts

### 3.1 `deck.csv` (extension columns, appended after jiten's nine)
`Kind` (`vocabulary` \| `particle` \| `auxiliary`), `PartOfSpeech` (MeCab major POS, e.g. `助詞`), `UsageNote` (LLM, particles/auxiliaries only). Readers that know only jiten's columns ignore them; unknown `Kind` values mean `vocabulary`.

### 3.2 `deck-grammar.json` (next to `deck.csv`)
```json
{ "schema": "immersive-deck-grammar/1",
  "status": "llm_generated_examples_text_verified",
  "book_title": "...",
  "generator": { "plugin": "deckBuilder", "model": "...", "generated_at": "ISO-8601" },
  "grammar": [ { "id": "dg-<hex>", "pattern": "〜てしまう", "category": "grammar|expression|conjugation",
                 "meaning": "...", "explanation": "...", "first_chapter_index": 0, "first_chapter_title": "...",
                 "examples": [ { "text": "<verbatim book sentence>", "surface": "<verbatim substring>", "chapter_index": 0 } ] } ],
  "function_word_notes": { "に": [ { "function": "destination", "note": "...", "example": "<verbatim sentence or null>" } ] } }
```
`id` = `dg-` + first 10 hex of SHA-1 of the normalised pattern (stable across runs, merges `〜` / `～` / spacing variants).

### 3.3 Python consumption
- `build.load_decks` reads `Kind` / `PartOfSpeech` / `UsageNote` into each vocabulary entry (`kind`, `part_of_speech`, `usage_note`); reader schema: optional properties.
- `deck_grammar.load` validates the file strictly (`ValueError`, never `assert`); `merge` keeps a point only if ≥ 1 example is found verbatim in a chapter **and** its `surface` is inside it. Kept points become `book["grammar"]` entries with `status: "deck_llm_candidate"` (schema enum extended), real occurrences (offsets, UTF-16 offsets, sentence ids) and `model`; dropped ones are listed in `book["deck_grammar"]["dropped"]` with a reason. `book["deck_grammar"]` also records file name, SHA-256, schema, model, generated_at, `points_in_file`, `points_verified`.
- The file is part of the reader **cache key** (`cache.reader_inputs`) but not of `source_files` (those stay EPUB + CSV, so orchestration's input-stamp checks are unchanged). `deck_grammar.py` is in `READER_FILES`.
- `function_word_notes` fills `usage_note` only where the CSV has none.

### 3.4 Study material
- `vocab_cards(..., function_words=False)` excludes function words; `function_words=True` returns them (kind `function`, `details` = usage note, numbered across the book). Learning sets, `vocabulary-sets.json`, vocabulary PDFs and chapter vocabulary pages therefore contain **no particles**.
- New outputs, only when the deck has function words: `html/<book>/function-words.html`, `pdf/<book>/function-words-master.pdf` (columns Nr. / WORD / READING / USAGE / MEANING, own cover text), flashcard cards of kind `function`, a line per chapter in the overview.
- **Live progress and silent failures (added later):** while a run is in progress the status reports the job's own **live counters** (grammar points, particles with notes, failed requests, warnings, the latest warnings), updated at every chunk. Before, the counters were read from the result, which exists only when the run ends, so a running job always showed 0 and a re-run showed the previous run's numbers. A chunk whose answer is valid but contributes nothing (no grammar point with a verifiable example, no usable particle note) now adds a warning that says what the answer contained ("grammarPoints: 3, particleNotes: 0"); a run that ends with nothing usable shows an error box. Nothing is saved until the run finishes (a cancelled or failed run keeps nothing).
- **Printable chapter booklets (added later):** next to the kanji and vocabulary chapter PDFs, `pdf/<book>/chapter-NN-grammar.pdf` and `chapter-NN-function-words.pdf` are written for every chapter that introduces at least one grammar entry / particle or auxiliary (no empty booklets; same layout and cover as the masters, cover heading is the chapter). The LLM explanations therefore reach every printable: master sheets and chapter booklets. In the HTML overview the chapter row links the booklet PDFs. The study pack drawer lists them (`chapter-grammar`, `chapter-function-words` kinds) and the new `function-words-master.pdf`.
- **LLM grammar cards in print:** an LLM-written point prints its provenance line on the page ("LLM-written explanation (model) · N example(s) found verbatim in the book · review in context"), not only on the cover, and shows **up to 3 verbatim examples** (first occurrence first, then other distinct sentences in book order, same verification as before); rule-based candidates keep one example and no label. The HTML cards show the same extra examples.
- LLM grammar appears in `grammar.html`, `grammar-master.pdf` and flashcards, marked "LLM-written explanation (model) · N example(s) found verbatim in the book · review in context"; the PDF cover lists the grammar file and how many points verified.
- Verification: `validate_study`, `verify_html` and `verify_pdfs` expect vocabulary sets/pages without function words, one extra master PDF and exactly one grammar / particle booklet per chapter that has entries (`study_data.grammar_chapters`, `function_word_chapters`); `publication.py` recognises `function-words.html` and `function-words-master.pdf`.

## 4. Plugin behaviour
RPC (`ext.deckBuilder.*`): `estimateEnrichment` (chapters, requests, characters, ≈ input tokens, whether an LLM is configured, last run), `startEnrichment` (background job, refuses when no LLM is configured), `getEnrichmentStatus` (progress, results, warnings; the UI polls every 1.5 s), `cancelEnrichment`; `exportDeck` writes `deck-grammar.json` next to `deck.csv` when an enrichment exists (`includeGrammar`, default true) and asks before replacing either file.

Prompt rules (system prompt in `DeckEnricher`): never translate or paraphrase story sentences; English only in `meaning` / `explanation` / `function` / `note`; examples by sentence id + exact `surface`; known patterns are passed back so the model reuses the identical pattern text; at most `LlmMaxPointsPerChunk` points; particles only for words that exist in the deck and occur in the chunk.

## 5. Invariants
- No story sentence is translated or generated; English is limited to pattern meaning/explanation and usage notes.
- Examples are verbatim book text, verified twice (plugin, Python).
- `enrichement-extractor` makes no network or model call; it only reads `deck.csv` and `deck-grammar.json`.
- Book text is sent to the configured endpoint **only** when the user presses "Improve deck with LLM" (the UI shows the estimated request/token count first).
- The EPUB is never modified; deck files are replaced only on explicit confirmation, via temp file + move.

## 6. Limits / open work
- Explanations are model output and unreviewed (flagged in every printout). There is no in-app review/edit UI yet.
- The Python grammar `status` is `deck_llm_candidate`; rule-based candidates and LLM points can overlap in topic (different ids).
- (Done) Per-chapter grammar and particle booklets exist; a chapter with no entries has none, and rule-based plus LLM grammar share the same booklet.
- Very long chapters are split by `MaxChunkChars`; a point that spans a chunk boundary can be missed.
