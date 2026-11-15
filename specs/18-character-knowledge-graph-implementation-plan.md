# Implementation Plan 18: Spoiler-gated Character Knowledge Graph

Spec: [18-character-knowledge-graph-spec.md](18-character-knowledge-graph-spec.md) · Feature file: [features/14-character-knowledge-graph.feature](features/14-character-knowledge-graph.feature).
Program order across specs 17/18/19: see [plan 17 section 1](17-voice-character-companion-implementation-plan.md).

**Status: Batch 1 (K1 to K13) implemented and tested (147 tests, no network); K0 spike run once on the real endpoint, results in section 7. K14 to K19 (C# side, plugin UI, docs) implemented as the `characterKnowledge` plugin, see [spec 20](20-character-knowledge-plugin-spec.md).** ⬜ not started, 🔶 in progress, ✅ done.

## 1. Principle: riskiest logic first, test-driven, no network
The spoiler-safety of the whole feature rests on a few small deterministic pieces. They are built **first**, as pure Python with a scripted fake LLM and a synthetic book containing a canary secret, and hardened with property tests **before** any real model, UI or C# code exists. Real-model work (spike K0) runs in parallel when the endpoint is available and only *tunes prompts*; it can never weaken a gate.

Risk ranking (highest first): **(1)** gate + evidence positioning + hidden-name scanner; **(2)** causal snapshot (what the model is allowed to see); **(3)** operation validation (invented quotes, bad ids); **(4)** build resume/idempotence; **(5)** position mapping reader→chapter (C#); **(6)** real-model quality; **(7)** process/UI plumbing.

## 2. Project layout (new, outside `enrichement-extractor`)
```
knowledge-graph/
  pyproject.toml            # deps: networkx, an HTTP client; dev: pytest-free (unittest) like the rest of the repo
  immersive_knowledge/
    position.py             # StoryPosition, parse/format, ordering
    store.py                # SQLite schema + data access (no gating logic)
    evidence.py             # quote locator (verbatim, whitespace-normalised)
    ops.py                  # operation schema, validation, apply
    gate.py                 # visible/valid-at rules, graph views (NetworkX), snapshots
    scanner.py              # hidden-name scanner
    profile.py              # profile assembly + rendering block (template from spec 19)
    builder.py              # causal incremental loop, resume, idempotent chapter rebuild
    llm.py                  # OpenAI-compatible client (no fallback, retries, repair retry)
    templates.py            # prompt renderer (shared vectors with C#, spec 19)
    cli.py                  # build | snapshots | characters | profile | verify
    prompts/*.prompt.md     # knowledge.extract, ...
  tests/                    # unittest, synthetic "Test Saga" fixture with the canary
  README.md                 # venv setup, config.json, FreeToken example, invariants
```
Run tests from `knowledge-graph/` with its own venv: `python -m unittest discover -s tests -v` (documented in AGENTS.md next to the other two commands).

## 3. Tasks (in build order)
| # | Task | Done when | Status |
| :-- | :--- | :--- | :--- |
| K1 | Skeleton: `pyproject`, package, README, test runner command, AGENTS.md/LLM-WIKI note that this project is separate from the offline pipeline | `unittest discover` runs (empty suite) | ✅ done |
| K2 | `position.py`: `StoryPosition`, `as_of`, parse/format `V:C[:O]`, ordering | scenarios "Positions compare…" as unit tests | ✅ done |
| K3 | `store.py`: schema (spec 4.1), opaque id allocator (`e0001`), transactions, per-chapter ops tagging | schema test, id test ("Entity ids carry no names") | ✅ done |
| K4 | `evidence.py`: locate a quote in chapter text (normalise whitespace/full-width spaces, first occurrence, ambiguity flag), return start/end | tests: unique, repeated, missing, whitespace variants, very short quotes rejected | ✅ done |
| K5 | `ops.py`: validation (schema, evidence required, ids exist or declared earlier in the answer, kinds/keys whitelist) and apply with position = max evidence end | all "rejected" scenarios; fixtures with scripted ops | ✅ done |
| K6 | **`gate.py`**: `visible`/`valid_at`, names at P, relations windows, `same_as`, graph view via NetworkX `subgraph_view`, snapshot dict as of P | gate scenarios + **property tests**: for random positions, nothing visible has a position ≥ P; views are monotonic (visible(P1) ⊆ visible(P2) for P1<P2, except superseded values) | ✅ done |
| K7 | **`scanner.py`**: build the set of not-yet-visible names for P, search rendered text (also normalised width/kana variants), fail with the hits | scanner scenarios; **canary tests** at positions 1:1 and 1:2 | ✅ done |
| K8 | `profile.py`: assemble profile (names, traits, relationships, timeline, speech samples, status, voice cues), render block via template, scan | profile tests; snapshot determinism (same input → same bytes) | ✅ done |
| K9 | **`builder.py`**: causal loop with a scripted fake LLM; request content = snapshot(as_of chapter) + that chapter only; per-chapter commit; resume; idempotent rebuild; stale detection by `text_sha`; volume carry-over | "The model only sees the past", resume/rebuild/stale/volume-2 scenarios; the fake records every request so tests assert nothing from the future appears | ✅ done |
| K10 | Snapshot generation for every chapter boundary + `characters` listing by prominence | snapshot scenarios | ✅ done |
| K11 | `templates.py` (spec 19 renderer, Python side) + `knowledge.extract` template | shared vectors pass | ✅ done |
| K12 | `llm.py`: HTTP client, retries, repair retry, no fallback, errors with status/body, key from environment | tests against a fake HTTP handler (shape, retries, fatal auth error) | ✅ done |
| K13 | `cli.py` + JSON-lines progress + exit codes + config file; secrets only from env | CLI tests (arguments contain no key, exit codes) | ✅ done |
| K0 | **Spike (real model, parallel):** run `knowledge.extract` on 3 real chapters via FreeToken; record valid-answer rate, evidence-verified rate, rejected reasons, time, tokens; tune the prompt; check the canary on a real book with a known late reveal | `specs/18-spike-report.md`; prompt changes committed with the numbers | 🔶 first run done (see 11) |
| K14 | C#: move `BookTextExtractor`/`SentenceSplitter` to `ImmersiveReading.Core.Epub` (shared); `book-text.json` exporter (volumes, chapter text) | DeckBuilder tests still pass; exporter test with a synthetic book | ✅ done |
| K15 | C#: **position mapping** (reader `current_chapter_index` + volume ordinal of the book in its series) → `StoryPosition`; later offset mapping spike | unit tests with a synthetic series (ordering by `SeriesIndex`, standalone, missing index) | ✅ done |
| K16 | C#: `KnowledgeBuildRunner` (launch Python, env key, progress lines, cancel) + `KnowledgeStoreReader` (read-only SQLite; list characters, profile snapshot by position) | tests with a fake Python script and a pre-built sample `knowledge.db` | ✅ done |
| K17 | C#: RPC (`estimateKnowledgeBuild`, `startKnowledgeBuild`, `getKnowledgeStatus`, `cancelKnowledgeBuild`, `listKnowledgeCharacters`, `getCharacterProfile`) | RPC contract tests | ✅ done |
| K18 | UI: build panel (estimate, progress, resume, cancel, results counts), character list (as of reader position) feeding the persona creation of spec 17 | `npm run build:plugins`, `--verify-ui` step | ✅ done |
| K19 | Docs: wiki pages, user guide section (what the graph is, cost, privacy: book text goes to the endpoint you configured), lessons | consistent | ✅ done |

K1–K13 are Python-only and need no network. K14–K17 need no model. Only K0 needs the hosted model.

## 4. Test strategy
- **Fixture:** the synthetic "Test Saga": 2 volumes, chapters with a masked man, a reveal (ch 2), a culprit canary word (ch 3), a friendship that ends, an alias, a quote that appears twice, a chapter with full-width spaces. A scripted fake LLM returns hand-written operations per chapter and **records its prompts**.
- **Property tests:** random positions (volume, chapter) over random generated stores: no leak, monotonic visibility, deterministic rendering, scanner never misses an inserted hidden name.
- **Adversarial cases:** model "back-dates" a fact (quote from chapter 3 returned during chapter 2 processing is not found in chapter 2's text → rejected); model returns an id that was revealed later; model returns a name only visible from chapter 3 in a summary for chapter 2 (scanner at render time).
- **Contract tests:** CLI arguments/exit codes/progress lines; `book-text.json` schema; SQLite schema version.
- **No test uses the network.**

## 5. Risks and decisions still open
| Item | Default taken in this plan |
| :--- | :--- |
| Store (K-D2) | SQLite per series, NetworkX built from rows |
| Python project location (K-D4) | sibling `knowledge-graph/` |
| Python environment for the builder | own venv documented in the README; the plugin shows "builder not installed" with the exact command when missing (not bundled in the portable study-pack env) |
| Gate granularity (FR-3) | chapter-complete only, current chapter hidden |
| Offset gating | later, after K15's mapping spike proves reader offsets map to extracted text |
| Chapters per request | 1 (setting exists; higher = labelled less spoiler-safe) |
| Prominence threshold for snapshots | characters with ≥ 3 mentions or ≥ 1 event, tunable |

## 6. Rollback
Nothing existing is modified except the shared-extractor move (K14, covered by existing DeckBuilder tests). The feature is inert until a user builds a knowledge base; deleting `.library-data/knowledge/<series>` removes it.

## 7. Spike K0 results (real model, 2026-11-14)
Endpoint `http://127.0.0.1:1919/v1` (FreeToken), model `Huihui-Qwen3.6-35B-A3B-abliterated.Q4_K_M.gguf`, book: `くま クマ 熊 ベアー` vol. 1 (28 short chapters, 2,000 to 7,800 characters each). Command: `python -m immersive_knowledge build ...`.

Full volume, thinking off, context limit unknown at the start (learned on the way): **28 of 28 chapters built, exit 0**; 42 requests (4 text splits); learned context limit 8,241 tokens; **510 operations applied, 96 rejected (16%, mostly multi-line dialogue quotes joined into one quote)**; 79 entities, 256 attributes (including speech samples), 51 relations, 120 events, 519 verified evidence quotes; **456 gated snapshots stored, 0 blocked**; `verify`: 0 problems. Time: about 12 minutes for the volume on the FreeToken instance. **Real-data spoiler check:** for five terms introduced later in the book (`ブラッディベアー`, `タイガーウルフ`, `ゴブリン`, `クマハウス`, `召喚獣`) no snapshot at or before the chapter where the term first appears in the text mentions it (for example `タイガーウルフ`: first in the text at chapter 19, first in a snapshot as of chapter 20).

What the real model taught us (each is a code change with a test, none weakens a gate):
| Finding | Change |
| :--- | :--- |
| The reasoning model "thinks" by default: 5,992 reasoning tokens, empty answer, `finish_reason=length` | `thinking` defaults to **off** (`chat_template_kwargs.enable_thinking=false`): a valid answer in about 5 s; an answer cut off by reasoning is reported with that hint |
| The server rejects `response_format` | client retries once without it (already in place) |
| **Advertised context 262,144, real budget 8,241 tokens per request (prompt + generation)**; output capped near 6,000 tokens | `context_length_exceeded` is understood, the real limit is parsed from the message and learned; the prompt gets a smaller snapshot (3 levels) and then the text is split; parts are sized to a quarter of the real context afterwards; a cut-off answer splits the text |
| Quotes: `「お帰りなさいませ、ユナ様」` vs text `お帰りなさいませ、ユナ様。` | edge brackets/sentence-end marks are ignored when locating a quote (body must still match) |
| A 3-character quote (`ユナ様`) introducing a character was rejected as "too short" | entity/name quotes may be as short as the name; the quote must contain the name; the name must occur in the text |
| One paraphrased quote among several voided a whole operation | an operation with at least one verified quote is kept and positioned at the end of the text the model was shown |
| The model "re-created" a known character (chapter told from another viewpoint): all 16 dependent operations rejected | `add_entity` of a name the model was shown binds the ref to the existing entity (new names become aliases) |
| No `speech_sample` at all (needed for personas) | prompt now asks explicitly for 1 to 3 samples per speaking character and a `speech_style` |
| Generic roles (maid, parent, guard) became entities | prompt: no entities for unnamed generic roles |
| Scanner blocked 40 of 110 snapshots: names such as `クマ`, `ギル` (inside `ギルド`) are ordinary words that were on every page | a name counts as hidden only until it has **occurred in the text the reader has read** (`names.seen_key`, indexed by the builder) and is matched as a word of its own (script boundaries, honorifics) |
| A character with no snapshot at the reader's chapter (blocked) had no profile | `get_profile` uses the character's latest **earlier** snapshot, never a later one |
| A failed chapter showed only "invalid JSON" | the end of the unusable answer is stored in `chapters.error` (not on stdout) |

**Action for the user:** the FreeToken instance enforces about 8k tokens per request (KV cache budget) although it reports 262,144. The builder works with that (it splits and shrinks), but each chapter then needs several requests and a small snapshot. Raising the KV cache budget in FreeToken (for example to 64k or more) lets whole chapters and full snapshots go in one request; set `context_tokens` in the config if the limit is known.
