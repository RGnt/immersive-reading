# Specification 18: Spoiler-gated Character Knowledge Graph

## Status: Python core implemented (2026-11-14): `knowledge-graph/` builds, gates and snapshots; verified against the real model once (plan 18, section 7). C# integration, UI and offset gating are not implemented.
**Scope:** a new Python project `knowledge-graph/` (builder + gate + snapshot export), a read-only C# reader and a build runner in the AI Companion plugin, UI for building/browsing.
**Plan:** [18-character-knowledge-graph-implementation-plan.md](18-character-knowledge-graph-implementation-plan.md) · **Feature file:** [features/14-character-knowledge-graph.feature](features/14-character-knowledge-graph.feature)
**Reached by other features through:** [24](24-knowledge-hub-and-user-memory-spec.md) (knowledge hub). **Consumers:** [17](17-voice-character-companion-spec.md) (persona, voice design, spoken chat). **Uses:** [19](19-prompt-templates-and-conversation-coach-spec.md) (prompt templates). **Reuses the ideas of:** [16](16-deck-llm-enrichment-spec.md) (verbatim evidence, no invented quotes).

---

## 1. Problem and goal
A character you talk to must know what the reader knows about them and **nothing more**. Dumping chapters into a prompt is expensive, slow and can leak: the model reads ahead. Instead, build **once** a structured knowledge base of the book (or series): characters, relationships, events, locations, traits, speech samples, every fact **gated by story position** (volume, chapter) and backed by a verbatim quote. A chat then asks "what is known about X *as of where the reader is*" and gets a small, deterministic, spoiler-free profile.

Model for building: a large-context hosted model (reference: Qwen3.6-35B-A3B via FreeToken, 262,144-token context). The builder is configurable (endpoint, model, key, context size) and independent of the chat model.

## 2. Decisions taken (from the owner's answers, 2026-11-14) and proposals awaiting confirmation
| # | Decision | State |
| :-- | :--- | :--- |
| K-D1 | Graph handled with a Python graph library | **Given.** Proposal: **NetworkX** for in-memory graph views and analytics. Kuzu is **not** recommended: its repository was archived on 2025-10-10 and it no longer receives updates ([The Register](https://www.theregister.com/2025/10/14/kuzudb_abandoned/)); forks exist but are young |
| K-D2 | Storage "is a different story" | **Proposal:** one **SQLite file per series** is the source of truth (durable, inspectable, readable from C#); NetworkX graphs are built from its rows when needed. Alternative: append-only JSON-lines operation log. **To confirm** |
| K-D3 | Gated on Volume/Chapter | **Given.** v1 granularity is whole chapters; see 4.2 for the current-chapter rule |
| K-D4 | The builder calls an LLM from Python | **Proposal:** a **new sibling project** `knowledge-graph/` (own dependencies: `networkx`, an HTTP client), **outside** `enrichement-extractor`, so AGENTS.md invariant 1 (no LLM / no network in the study pipeline) stays true. AGENTS.md gets a note that this project is deliberately different. **To confirm** |
| K-D5 | Spoiler gating as strong as possible | **Given.** Defence in depth, section 5 |
| K-D6 | Character profile and "what happened to them" both gated | **Given** (profile and timeline come from the same gated store) |

## 3. Position model
`StoryPosition = (volume, chapter, offset)` compared lexicographically. `volume` is the **ordinal** of the book among the volumes of its series in the library, ordered by `SeriesIndex` (1-based; a book without a series is volume 1); `chapter` is the library chapter index (`book_chapters.chapter_index`); `offset` is a character offset inside the chapter text (v1: always 0, see 4.2).
- The reader's position comes from `reading_progress` of the open book (`current_chapter_index`, later `locator_char_offset`) plus the book's volume ordinal.
- **Earlier volumes** of the series count as fully read by default (`assumeEarlierVolumesRead`, true). **Later volumes** are never visible.
- `as_of(volume, chapter)` means "everything established **strictly before** the start of that chapter".

## 4. Functional requirements
### 4.1 Knowledge model (property graph in SQLite)
| Element | Fields | Notes |
| :--- | :--- | :--- |
| `entities` | `id` (opaque, e.g. `e0007`), `kind` (character, location, organization, item, concept), `first_pos` | **Ids never contain names**, so an id cannot leak a name that is revealed later |
| `names` | `entity_id`, `name`, `type` (canonical, alias, nickname, title, reading), `from_pos`, `evidence_id`, `preferred` | A name is visible only from its own position (identity reveals) |
| `attributes` | `entity_id`, `key`, `value`, `from_pos`, `to_pos` (superseded), `evidence_ids` | keys: age, gender, appearance, occupation, personality, speech_style, goal, status, `speech_sample` (a verbatim line spoken by the character), ... |
| `relations` | `src`, `dst`, `type`, `directed`, `summary`, `from_pos`, `to_pos`, `evidence_ids` | changes over time are a closed relation plus a new one, never an overwrite |
| `events` / `event_participants` | `summary`, `pos`, `location`, participant roles | the "what happened to them" timeline |
| `identity_links` | `a`, `b`, `same_as`, `from_pos`, `evidence_id` | "the masked man is X": gated; entities are linked, never destructively merged |
| `evidence` | `volume`, `chapter`, `start`, `end`, `quote`, `text_sha` | the quote is verbatim book text |
| `ops_log` | `chapter_key`, `op_json`, `status` (applied/rejected), `reason` | audit, replay, debugging |
| `build_state` | `chapter_key`, `status`, `model`, `text_sha`, `built_at` | resume and staleness (book text changed) |
| `profile_snapshots` | `character_id`, `volume`, `chapter`, `profile_json`, `block_text` | precomputed gated profiles (4.4) |

### 4.2 Gating rules
- **FR-1** An element's position is the **maximum** end position of its evidence quotes (the point where the reader has seen everything that supports it). Elements without verified evidence do not exist.
- **FR-2** `visible(element, P)` is true iff `element.from_pos < P` and (`to_pos` is null or `to_pos >= P`). Relations and attributes are *valid at P* in the same way.
- **FR-3 (v1)** Position granularity is the chapter: the **current chapter's facts are not visible** (the reader may be at the start of it). Setting `includeCurrentChapter` (default off) is reserved for offset gating, which needs the reader-offset to extracted-text-offset mapping to be proven first (plan, risk R4).
- **FR-4** Display names at P are the names visible at P; entities with no visible name are not listed at all.
- **FR-5** `same_as` links apply only from their own position; before it the two entities stay separate in every view.

### 4.3 Building (causal, incremental)
- **FR-10** Chapters are processed **in reading order, one request per chapter** (long chapters split into parts, processed sequentially). The request contains **only**: the gated snapshot as of the start of this chapter (everything from earlier chapters) and the chapter text. The model can never see later chapters, so it cannot back-date a later reveal into an earlier fact (the dominant leak path of "read the whole book then tag").
- **FR-11** The model returns **operations** (add entity/name/attribute/relation/event/same_as, end relation), each with one or more **verbatim evidence quotes** from the chapter. Operation schema in 6.2.
- **FR-12** Validation (deterministic, no model): JSON schema; referenced entity ids exist in the snapshot or were declared earlier in the same answer; every quote is found in the chapter text (whitespace-normalised) and its position computed by code, not by the model; operations that fail are logged `rejected` with a reason and counted.
- **FR-13** After each chapter the store is committed and `build_state` updated: a failed or cancelled build **resumes** at the first unfinished chapter; rebuilding a chapter replaces only that chapter's operations; a changed book (`text_sha`) marks affected chapters stale.
- **FR-14** Series: volumes are built in order into the **same** store; the snapshot for volume 2 chapter 1 contains all of volume 1.
- **FR-15** Cost control: before a build the UI shows chapters, requests and an estimated token count (as in spec 16). Request size is capped by `knowledgeMaxInputChars` (default sized from the 262,144-token context with a safety margin).
- **FR-16** Output language of extracted values (`output_language`, default Japanese) is a template variable; quotes are always the original text.

### 4.4 Querying (single source of gating logic)
- **FR-20** All gating logic exists **once**, in Python. After a build (and after any change) the builder precomputes, for every chapter boundary, a **gated profile snapshot** for each character with enough presence: names, traits, relationships (with names as known at that point), timeline, speech samples, status, voice cues, and a rendered text block. Snapshots are rows in `profile_snapshots`; the C# side only **looks up** the row for the reader's `(volume, chapter)`. No gating code is duplicated in C#.
- **FR-21** `list_characters(P)`: characters with at least one visible name and a snapshot, ordered by prominence at P; this feeds the "choose a character" list of the voice companion.
- **FR-22** Rendering a profile for a prompt passes through the **hidden-name scanner** (5, layer 4).

### 4.5 Builder as a program
- **FR-30** CLI contract (6.3): `build`, `snapshots`, `characters`, `profile`, `verify`; progress as JSON lines; the API key is passed by **environment variable**, never on the command line.
- **FR-31** The C# plugin exports the book text as `book-text.json` (6.1), launches the builder, shows progress, and reads snapshots from the SQLite file read-only.
- **FR-32** The builder's LLM client: OpenAI-compatible chat, JSON object answer, retry with backoff on 429/5xx/timeouts, a repair retry on invalid JSON, **no fallback** answers, errors with status and body (same lessons as spec 16).

## 5. Spoiler defence in depth
1. **Causal extraction** (FR-10): the model has no access to later text.
2. **Evidence positioning** (FR-1): a fact is as late as the latest quote that supports it; positions are computed by code.
3. **Gate on read** (FR-2..FR-5): strict filtering for every view and snapshot.
4. **Hidden-name scanner:** before a snapshot or a prompt block leaves the builder, every name/alias that is *not yet visible* (from the same store) is searched in the rendered text; a hit fails the render (and is a test failure). Cheap, catches leaks through free text (summaries that name a later-revealed identity).
5. **Prompt instruction** at chat time (existing spoiler boundary line).
6. **Residual risk, stated plainly:** a free-text summary can still contain an inference the quote does not support. Mitigations: short summaries, verbatim quotes preferred (`speech_sample`), the scanner, and a later review UI listing facts per chapter with their evidence. An optional second "spoiler audit" model pass is out of scope for v1.

## 6. Contracts
### 6.1 `book-text.json` (C# → builder)
`{ "series_key", "title", "language": "ja", "volumes": [ { "volume": 1, "book_id", "title", "series_index", "chapters": [ { "index": 0, "title", "text" } ] } ] }`. Chapter text is the plain visible text (ruby readings removed, whitespace normalised) produced by one shared extractor (moved to Core, plan K-T0), so offsets are stable.

### 6.2 Operations returned by the model
```json
{ "ops": [
  {"op":"add_entity","ref":"n1","kind":"character","names":[{"name":"…","type":"canonical"}],"evidence":[{"quote":"…"}]},
  {"op":"add_name","entity":"e0003|n1","name":"…","type":"alias","evidence":[…]},
  {"op":"set_attribute","entity":"e0003","key":"personality","value":"…","evidence":[…]},
  {"op":"add_relation","src":"e0003","dst":"e0001","type":"sibling","directed":false,"summary":"…","evidence":[…]},
  {"op":"end_relation","relation":"r0004","evidence":[…]},
  {"op":"add_event","summary":"…","participants":[{"entity":"e0003","role":"…"}],"location":"e0009","evidence":[…]},
  {"op":"same_as","a":"e0003","b":"n1","evidence":[…]}
] }
```
`ref` names are local to one answer; real ids are assigned by the store.

### 6.3 CLI (`python -m immersive_knowledge …`)
`build --db <file> --book-text <file> --config <file> [--resume] [--from VOL:CH]` · `snapshots --db <file>` · `characters --db <file> --position VOL:CH` · `profile --db <file> --character <id> --position VOL:CH` · `verify --db <file>` (integrity, canary scan). `config.json` has endpoint, model, context tokens, temperature, output language, limits; the key comes from `KNOWLEDGE_API_KEY`. Progress lines: `{"event":"chapter_done","volume":1,"chapter":3,"ops_applied":14,"ops_rejected":2}`, `{"event":"error","message":…}`. Exit code 0 ok, 2 invalid input, 3 LLM failure (fatal), 4 partial (some chapters failed, resumable).

### 6.4 Store location
`<app data>/.library-data/knowledge/<series_key>/knowledge.db` (series_key: hash of series name, or of the book id for a standalone book).

## 7. Settings (AI Companion plugin, additive)
`knowledgeEndpointUrl`, `knowledgeModelId`, `knowledgeApiKey` (password), `knowledgeContextTokens` (default 262144), `knowledgeMaxInputChars`, `knowledgeOutputLanguage` (default `ja`), `assumeEarlierVolumesRead` (true), `includeCurrentChapter` (false, reserved), `knowledgeBatchChapters` (1; higher values trade spoiler-safety for speed and are labelled so). Blank endpoint/model/key fall back to Settings → AI.

## 8. Invariants
- **I-K1** No fact is visible at a position earlier than the latest evidence supporting it.
- **I-K2** Extraction input at chapter k contains no text and no fact from chapter k or later beyond the chapter being processed (and nothing after it).
- **I-K3** No quote is invented: every stored quote occurs verbatim in the book text.
- **I-K4** Snapshots and rendered blocks never contain a name that is not yet visible (scanner).
- **I-K5** The study pipeline `enrichement-extractor` is untouched and stays offline.
- **I-K6** Secrets never appear on a command line or in logs.
- **I-K7** Books are read-only inputs; knowledge files live under the app data directory.

## 9. Non-goals (v1)
Offset-precise gating inside a chapter; a fact review/edit UI; cross-book (different series) knowledge; automatic spoiler audit by a second model; graph visualisation; editing the graph by hand; a graph database server.

## 10. Risks
| # | Risk | Mitigation |
| :-- | :--- | :--- |
| R1 | Extraction quality/JSON validity of the chosen model | spike K0 on real chapters: validity rate, evidence pass rate, cost; the validator rejects, never repairs silently |
| R2 | Identity reveals and aliases | gated `same_as`, names with own positions, canary test |
| R3 | Free-text leaks | scanner, short summaries, quotes preferred |
| R4 | Reader position (token id/offset) vs extracted text offsets | v1 chapter granularity; mapping spike before offset gating |
| R5 | Cost and time for a long series | per-chapter resume, estimate before run, only characters above a prominence threshold get snapshots |
| R6 | Two languages (C# app, Python builder) | one contract, gating only in Python, C# reads rows |
| R7 | Python environment for the builder | own `pyproject`; documented venv; plugin reports a clear message when it is missing |

## 11. Amendments from the implementation (2026-11-14)
- **Rebuild semantics:** a changed or failed chapter is rebuilt **together with every later chapter** (they were built on its facts); ids are never reused.
- **"as of 1:3" means before chapter 3:** a relationship that ends in chapter 3 is still current as of 1:3 and ended as of 2:0; ended relations are kept as history.
- **Scanner semantics:** a name is hidden until the entity is visible **and** the word has occurred in the text read so far; names are matched as words of their own.
- **Real-world limits are discovered:** the builder learns the server's real context budget from its errors and adapts (smaller snapshot, then smaller text parts). See plan 18, section 7.
- **Thinking is off by default** for extraction.
- **Reuse instead of rejection:** re-creating a known entity binds to it.
- **Story pass (2026-11-14, [spec 22](22-story-events-and-items-spec.md)):** a second, optional pass records typed events (with parent/child) and item holdings (newer gear replaces older gear). Schema version 2 (`events.kind/title/parent`, `holdings`, `origin` tags, `story_*` tables; version 1 databases migrate in place), entity kind `creature`, new CLI commands `events` and `items`. The `add_event` operation of the character pass stays; when the story pass recorded events for a chapter it replaces that chapter's untyped events.
- **Pronouns are not names (2026-11-15):** a first-person book made a real build record the narrator as the character "わたし" (2,392 event roles, 539 relations) beside a separate protagonist, because the model re-created the pronoun every chapter and the name matched the old entity. Now: (1) `pronouns.py` lists personal pronouns (all three persons, whole names only); (2) `ops` refuses an `add_entity` of a character whose names are all pronouns, drops a pronoun that comes with a real name, refuses `add_name` of a pronoun and any operation on a character of an earlier build that is called only by a pronoun; (3) the attribute key `narrator` (the program writes its value; only the evidence is the model's) marks who tells a chapter in the first person, and both snapshots carry `"narrator": "<id>"` (most recent narrator) so the model can resolve わたし; pronoun-only characters are hidden from the snapshots; (4) the character and story prompts (version 2) say how to resolve a pronoun (narrator in narration, speaker inside quotes, addressee for あなた, last mentioned for 彼/彼女); (5) an answer that still names a character by a pronoun is asked once more with the reason (`retry_note`, `BuildReport.pronoun_retries`); a repeat is refused operation by operation. Nothing is mapped by guessing: a pronoun is never silently turned into the narrator, because 俺 inside a quote is someone else. Tests: `tests/test_pronouns.py`.
