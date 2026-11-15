# Spec 24: Knowledge hub: one interface to every knowledge graph, and a memory of what the user told us

Status: **phases 1 and 2 implemented** (contracts, hub, user memory, story graph as a source, host wiring; characters recall and learn facts, Settings → Memory). Phase 3 is designed here and not built. Decisions confirmed by the owner: facts are shared with **every character** by default (setting to limit to one), and facts used in a prompt may go to the configured model endpoint.
Builds on [spec 18](18-character-knowledge-graph-spec.md) (spoiler-gated story graph), [spec 22](22-story-events-and-items-spec.md) (events and items) and [spec 17](17-voice-character-companion-spec.md) (personas, "per-character memory" left open).

## 1. Problem

The knowledge graph can only be reached by the Character Knowledge plugin's own screens. Three things are wanted that need one shared way to ask:

1. **Other features use the story graph**: a persona you chat with, a coach, a quiz, without referencing the Character Knowledge plugin (plugins may reference only Core, see `wiki/dotnet-architecture.md`).
2. **Graphs of things other than books**: for example the concepts of physics, with prerequisites, built from textbooks and used by a tutor.
3. **Facts the user gave** ("my sister is Yuna", "I work nights"): stored once, recalled by characters in later conversations.

## 2. Decisions

| # | Decision | Why |
| :--- | :--- | :--- |
| D1 | The contract lives in **Core** (`Core.Knowledge`); the hub and the user memory live in a new domain project **`ImmersiveReading.Knowledge`** (references Core only); each graph **registers itself as a source** through the plugin host context (`RegisterKnowledgeSource`) and consumers use `context.Knowledge` | Plugins cannot reference each other, so the interface must sit below them. A new project keeps "what the user told us" out of Library (books) and out of every plugin |
| D2 | A query names a **scope** (`book`, `subject`, `user`), optionally a **viewpoint**, and returns **entries** (kind, title, prompt-ready text, relevance) | Consumers need text for a prompt, not graph internals; the same call works for a book, a subject and the user |
| D3 | **Gating is part of the contract.** A source declares `Gating`. `StoryPosition` sources refuse a query without a viewpoint (the hub refuses before calling them, the source refuses again) | Spec 18's rule "nothing at or after the reader's position" must hold for every new consumer, not by convention |
| D4 | **Sources keep their own storage and builders.** The story graph stays a SQLite file written by the Python builder and read by C#; user memory is a small C# SQLite store | Python builds (batch, LLM), C# stores and recalls at chat time. No cross-language writes to one file |
| D5 | **User memory is separate from story graphs** and never gated by position | A fact about the user has no chapter |
| D6 | **Recall is lexical in v1** (character bigrams weighted by rarity, works for Japanese and English), behind `QueryAsync` | Hundreds of facts do not need a vector store; an embedding source can replace it without any consumer changing |
| D7 | **One broken source never breaks a conversation**: the hub logs and skips it. A query that breaks the gating rule is an error, not skipped | Knowledge only enriches context; a spoiler leak must never be hidden by a catch |

## 3. Contract (`ImmersiveReading.Core.Knowledge`)

* `KnowledgeScope(Kind, Id)`: `book` (a book id, the series is resolved by the source), `subject` (a subject key), `user` (`me`).
* `KnowledgeViewpoint(ChapterIndex)`: for a book scope the chapter the reader is about to read; `Unrestricted` asks for everything and is refused by gated sources.
* `KnowledgeQuery(Scope, Text, Kinds, Viewpoint, Audience, Limit)`: `Text` null lists (most relevant or most recent), otherwise matches; `Audience` (for example `persona:prs-1`) lets a source hide what was shared with someone else only; null is the owner.
* `KnowledgeEntry(SourceId, Scope, Id, Kind, Title, Text, Relevance, Attributes)`; kinds: `character`, `event`, `item`, `concept`, `fact`.
* `IKnowledgeSource` (`SourceId`, `ScopeKinds`, `Gating`, `QueryAsync`), `IWritableKnowledgeSource` (`RememberAsync(FactInput)`, `ForgetAsync(id)`), `IKnowledgeHub` (`Sources`, `QueryAsync`, `RememberAsync`, `ForgetAsync(sourceId, id)`, `RegisterSource`).
* `KnowledgeMatch` (Core): the shared bigram scoring.

## 4. Sources that exist

* **`story`** (`StoryKnowledgeSource`, Character Knowledge plugin, `book`, gated): characters (the stored profile block), events (title, summary, who took part), items (description, who holds it now). Looks up the precomputed snapshots and cards; adds no gating of its own. An unknown book answers with nothing.
* **`user-memory`** (`UserMemorySource`, built in, `user`, ungated, writable): table `user_facts` in `<data root>/user-memory/user-memory.db`. Rules: a fact is at most 500 characters and is refused, never truncated; the same fact again (compared without case, spacing, width and punctuation) refreshes the existing one; at most 5,000 facts; **forgetting deletes the row**; a fact can be **shared with named audiences only**, and a query from another audience does not see it, not even by searching for its words.

## 5. Privacy

User facts are personal data. They are stored only on this computer, but a fact that is put into a prompt goes to the configured model endpoint like the rest of the chat. Therefore: the owner can list and delete every fact (phase 2 UI), a fact can be limited to one character, and recall can be switched off with one setting. Nothing stores a fact the user did not say (see 6.2).

## 6. Phases

### Phase 1 (done)
Contracts, hub, user memory, story source, `IPluginHostContext.Knowledge` and `RegisterKnowledgeSource`, DI wiring in `AppServices`, an `ArchitectureTests` rule for the new project. Tests: `KnowledgeHubTests` (routing, gating, failure isolation, memory behaviour), `CharacterKnowledgeTests` (story source: spoiler cases through the hub), `UnifiedPluginSystemTests` (one plugin's source reachable from another, gone when disabled).

### Phase 2: characters use it (implemented)
How it was built: `PersonaMemory` (AI Companion) is attached to `PersonaChatService` when the plugin is enabled. **Recall** appends the `persona.memory` template to the system prompt: the facts that fit the message plus the newest ones (a fact worded differently from the message must not be missed), and story events and items from the hub at the reader's chapter. **Learning** runs after the reply is saved and never delays or fails it: the `memory.extract` template gets only the user's own message and the facts already known; a fact is stored only if its `quote` occurs in that message. Settings: `characterMemory`, `characterMemoryShare` (`all` or `thisCharacter`), `memoryMaxFacts`. Both templates are editable in Settings → Prompts ([spec 25](25-editor-prompts-and-custom-css-spec.md)). Not done: the explicit "remember that ..." shortcut that skips the model's judgement; the persona's own `knowledge_character_id` profile in the prompt (the persona record does not carry it yet).

The original design follows.
1. **Persona chat reads it.** Before each reply the AI Companion asks the hub twice: the story graph for the persona's book at the reader's chapter (`Text` = the user's message, plus the character's own profile), and user memory (`Audience = persona:<id>`: the newest facts with `Text` null plus the facts that match the message). Both are inserted into the persona prompt template as `{{story_context}}` and `{{user_facts}}` (spec 19 templates), each with a character budget.
2. **Facts are captured from what the user says.** After a user turn a short extraction call (template `memory.extract`) proposes facts; a fact is accepted **only if it is stated in the user's own message** (a verbatim quote check, the same grounding rule as spec 16/18; the model's own statements and the book's story are never stored as facts about the user). Accepted facts go to `RememberAsync` with `Origin = chat:<id>` and `SharedWith` from a setting (**proposed default: every character; the alternative is this character only. To confirm**). An explicit "remember that ..." in the message is stored without waiting for the model's judgement.
3. **A "What the characters know about you" view** in settings: list, edit the audience, delete, delete all.
4. Settings: `characterMemory` (on/off), `characterMemoryShare` (`all`|`thisCharacter`).

### Phase 3: other kinds of graphs (physics)
The builder already stores typed entities (`concept` exists), typed relations and verbatim evidence. What is story-specific is the **prompt set, the vocabulary** (entity kinds, relation types, attribute keys) **and the meaning of position**. Plan: a **domain profile** for the builder (`story` today; `concepts`: relation types `prerequisite_of`, `part_of`, `explains`, `example_of`, `contradicts`), chosen in the build config; a corpus is a list of documents in learning order, and the gate's "position" becomes **how far the learner has studied**. A `ConceptKnowledgeSource` (`subject` scope, gated by progress) registers like the story source. Consumers do not change.

## 7. Not done / open

* No RPC for the UI yet (phase 2 adds `knowledge.*` actions or uses the AI Companion's).
* No automatic merge of the same fact told in different words (only exact normalised repeats are merged).
* No expiry or "this changed" handling ("I moved to Osaka" does not retire "I live in Tokyo"); the review list is the tool for now.
* Embeddings for recall; cross-book (different series) story knowledge; sources that need a second viewpoint kind.
