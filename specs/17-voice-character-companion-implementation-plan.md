# Implementation Plan 17: Spoken Character Companion (Phase A) and program order for specs 17, 18, 19

Specs: [17 voice companion](17-voice-character-companion-spec.md) · [18 knowledge graph](18-character-knowledge-graph-spec.md) · [19 prompt templates and coach](19-prompt-templates-and-conversation-coach-spec.md).
Plans: this file · [18](18-character-knowledge-graph-implementation-plan.md) · [19](19-prompt-templates-and-conversation-coach-implementation-plan.md).
Feature files (the basic test cases): [13](features/13-voice-character-companion.feature) · [14](features/14-character-knowledge-graph.feature) · [15](features/15-prompt-templates-and-coach.feature).
Source plan: `temp/ai_companion_voice_128k_context_implementation_plan.md`.

**Status: Batch 1 knowledge-graph core (plan 18, K1 to K13) and the Python prompt renderer (plan 19, P1/P3) are implemented and tested; the real-model spike K0 ran once. Batches 2 to 4 and the C# renderer are not started.** ⬜ not started, 🔶 in progress, ✅ done.

## 1. Program order: riskiest logic first
Principle (owner, 2026-11-14): build and harden the riskiest logic first, test-driven, before plumbing and UI. For this program the riskiest logic is whatever can **leak spoilers** or **send wrong/fake content to the model or the speaker**.

```
 BATCH 1  pure logic, no network, no hardware, no UI            ← start here after "go"
   18: K1-K13  knowledge graph core (position, store, evidence, ops, GATE, SCANNER, profile, causal builder, snapshots, LLM client, CLI)
   19: P1-P3   prompt renderer in C# and Python against shared vectors
 BATCH 2  C# plumbing, fakes only
   17: T1-T5 (migration, settings, LlmRequest/provider, fakes), T6-T8 (optional story-prefix strategy)
   17: T9-T11 (TTS client, voice store, persona from knowledge), T13-T14 (voice turn pipeline, RPC)
   18: K14-K17 (shared text extractor, position mapping, build runner + store reader, RPC)
   19: P4-P8   (template store, built-in templates, coach service, turn integration)
 ── G1: backend demo from a test harness (fakes; real servers if available) ──
 BATCH 3  host + UI
   17: T16-T19 (microphone in the window, recorder, drawer UI, self-test)   18: K18   19: P9
 BATCH 4  docs and full verification (T20-T21, K19, P11)

 SPIKES (parallel, need your endpoints/hardware; they TUNE prompts and defaults, they never weaken a gate)
   K0 real model on real chapters (FreeToken) · P10 coach quality · S1-S7 voice stack (llama-server audio, thinking marker, cache reuse,
   microphone in the window, TTS speed/VRAM, voice consistency)
```
Gates: **G-go** (section 8), **G0** after the spikes that decide design points (A3, A4, A6, K0), **G1** before any UI.

## 2. Decisions recorded (owner answers, 2026-11-14)
| # | Decision |
| :-- | :--- |
| 1 | Endpoints are **configurable**. One default in Settings → AI plus **per-role overrides** (chat/voice model, knowledge builder, coach), each with endpoint, model, key |
| 2 | **TTS is a separate system** with its own connection settings; the app never starts or manages it. A reference service lives in its own folder `tts-service/` (not inside the plugin) |
| 3 | Persona data builds on **`book_personas`** (additive migration v2) |
| 4 | Context: keep the current summary behaviour and limit, **make the limit configurable** (`maxContextChars`); knowledge-graph context is the new preferred source; the 128K append-only prefix stays an optional strategy |
| 5 | Interaction: **push to talk**, microphone records **only while the key/button is held**, release sends. A recording-mode seam leaves room for silence-based capture later |
| 6 | **Japanese books only** for voice |
| 7 | **Riskiest logic first** (this plan's ordering) |
| 8 | New: **spoiler-gated knowledge graph** per series built with a large-context model (reference Qwen3.6-35B-A3B on FreeToken, 262,144 tokens), Python graph library (NetworkX), gated by volume/chapter (spec 18) |
| 9 | New: the system **creates the character's TTS voice design** from the gated profile (interpretation confirmed in section 8) |
| 10 | New: separate **coach** for improvements (grammar, naturalness), message language configurable (spec 19) |
| 11 | New: **prompt templates** with variables inserted at send time (spec 19, part A) |

## 3. Spike milestone M0 (no product code; scripts + reports only)
| # | Task | Done when | Status |
| :-- | :--- | :--- | :--- |
| S1 | `llama-server` + audio model: send a 16 kHz mono WAV as `input_audio`, JSON answer with transcript; 5 Japanese phrases | samples + accuracy noted | ⬜ |
| S2 | Thinking marker with/without; where reasoning text lands in the response | parsing rule recorded | ⬜ |
| S3 | 128K prefix: cold vs warm vs after-advance prefill time, cache reuse | numbers; verdict "worth it?" | ⬜ |
| S4 | Microphone in the app window: enable media stream in `MainWindowHost`, `getUserMedia` + AudioWorklet in the real window | working or exact blocker/API | ⬜ |
| S5 | TTS: install, run the reference service, load time, VRAM next to the LLM, time to audio for short/long lines | numbers | ⬜ |
| S6 | Voice consistency across 10 sentences and 3 emotions | verdict: seed enough / reference mode / not feasible | ⬜ |
| S7 | Report `specs/17-spike-report.md`; fill NFR-1 budgets and chosen defaults | committed | ⬜ |
| K0 | Knowledge extraction on real chapters (plan 18) | `specs/18-spike-report.md` | ⬜ |
| P10 | Coach quality and language compliance (plan 19) | `specs/19-spike-report.md` | ⬜ |

## 4. Tasks of this spec (voice companion)
### M1: foundations (additive; old behaviour unchanged)
| # | Task | Files | Done when | Status |
| :-- | :--- | :--- | :--- | :--- |
| T1 | Migration v2: `book_personas` (`voice_design_prompt`, `voice_seed`, `reference_wav_path`, `extracted_at_chapter`, `extracted_at_token`, `knowledge_character_id`), `persona_chat_messages` (`emotion_prompt`, `audio_wav_path`, `user_transcript`) and `coach_feedback` (plan 19) | `Schema/AiCompanionMigrations.cs` | migration test on a v1 database: rows kept, columns added, idempotent | ⬜ |
| T2 | Settings: role overrides (`voiceModelId`, knowledge, coach), TTS (`ttsEndpointUrl`, `ttsApiKey`, `ttsTimeoutSeconds`, `ttsLanguage`), context (`contextStrategy` `auto`/`summary`/`knowledge`/`appendOnlyPrefix`, `maxContextChars`, `prefixChunkChars`), voice (`pushToTalkKey`, `maxRecordingSeconds`, `autoPlayCharacterVoice`, `voiceCacheMaxMb`, `storeUserRecordings`, `allowRemoteEndpoints`); typed settings class | `settings.*.json`, `AiCompanionSettings.cs` | schema guard test | ⬜ |
| T3 | `LlmRequest` additive fields (`EnableThinking`, `Audio`, `CachePrompt`, `Timeout`) | `Core/Extensions/LlmContracts.cs` | existing LLM tests untouched | ⬜ |
| T4 | Provider: thinking marker, `input_audio` part, `cache_prompt`, per-request timeout, **no fallback** for voice/extraction/coach, errors with status and body, per-role endpoint resolution | `Providers/OpenAiCompatibleLlmProvider.cs` (+ role resolver) | provider tests with a fake HTTP handler | ⬜ |
| T5 | Fake LLM and fake TTS HTTP doubles shared by all tests | `tests/.../Voice/Fakes.cs` | used by T4 tests | ⬜ |

### M2: optional story-prefix strategy
| # | Task | Done when | Status |
| :-- | :--- | :--- | :--- |
| T6 | Full chapter text up to a token position from the token maps (the capped summary method stays) | unit tests, synthetic book | ⬜ |
| T7 | `StoryPrefixService`: layout (spec FR-12), **round-down chunking**, oldest-whole-chapters dropping | property tests: byte-prefix across positions, nothing at/after the position, deterministic | ⬜ |
| T8 | `PersonaChatService` uses the configured strategy; per-turn data after the stable block; `summary` output identical to today | layout test, regression test | ⬜ |

### M3: speech
| # | Task | Done when | Status |
| :-- | :--- | :--- | :--- |
| T9 | `SpeechClient` (separate connection, loopback guard, timeouts, WAV validation) and `testTtsEndpoint` | fake-TTS tests: ok, 500+body, non-WAV, oversize, non-loopback refused | ⬜ |
| T10 | Voice storage: per-persona folder, containment, size-bounded eviction | traversal rejected, eviction keeps newest | ⬜ |
| T11 | `CharacterVoiceService`: persona **from the knowledge graph** (profile snapshot at the reader position → `persona.system`), **voice design creation** (`voice.design` template → English instruction, editable, saved), synthesize | tests with fake LLM/TTS; the profile used is the snapshot for the reader's chapter, never a later one | ⬜ |
| T12 | `tts-service/` sibling project: reference server + README (install, port, VRAM, first-run model download) | README reviewed; runs in S5 | ⬜ |

### M4: voice turn and RPC
| # | Task | Done when | Status |
| :-- | :--- | :--- | :--- |
| T13 | `VoiceTurnService`: validate WAV (header, 16 kHz mono, max seconds) → LLM (audio + context per strategy) → JSON → n+1 audit/rewrite → enrich → synthesize → persist; start coach after the reply | feature-file smoke scenario with fakes | ⬜ |
| T14 | RPC: `extractPersonaFromBook`, `createPersonaFromKnowledge`, `savePersona`, `sendVoiceTurn`, `synthesizeMessageAudio`, `testTtsEndpoint`; `listPersonas` returns voice fields | RPC contract tests | ⬜ |
| T15 | Backend demo harness (G1) | reviewed with you | ⬜ |

### M5: host and UI (after G1)
| # | Task | Done when | Status |
| :-- | :--- | :--- | :--- |
| T16 | Enable microphone in `MainWindowHost` (API from S4); denied-permission path | manual check documented | ⬜ |
| T17 | Recorder: **push-to-talk** (key from `pushToTalkKey` or on-screen hold button; records only while held; release sends; Escape cancels), AudioWorklet, 16 kHz resample, WAV encoder, max duration, level meter; recording mode seam (`pushToTalk` now, `silenceTimeout` reserved) | encoder unit-tested in Node; manual capture | ⬜ |
| T18 | Persona creation from the knowledge character list, persona voice modal (design prompt, seed, preview), drawer (selector, push-to-talk, transcript line, emotion badge, replay, autoplay, "TTS unavailable" notice, coach card slot) | `npm run build:plugins`, guard tests | ⬜ |
| T19 | Self-test: drawer registers, voice controls render, a typed turn against the fake works | `--verify-ui` shows `voice=ok(...)` | ⬜ |

### M6: documentation and verification
| # | Task | Done when | Status |
| :-- | :--- | :--- | :--- |
| T20 | Wiki (`LLM-WIKI.md`, architecture, plugin-ui-authoring audio note), lessons, `USERGUIDE.md` (LLM roles, TTS setup, knowledge build, coach, privacy), AGENTS.md note on `knowledge-graph/` and `tts-service/` | consistent | ⬜ |
| T21 | Full verification: `dotnet test`, Python suites (`enrichement-extractor` and `knowledge-graph`), `--verify-ui`, hardware scenarios | all green, hardware results recorded | ⬜ |

## 5. Test strategy
- **Default (CI, no hardware, no network):** every `@fake` scenario of the three feature files. Doubles: scripted fake LLM (records prompts), fake TTS (valid/invalid WAV), synthetic book and series with a canary secret.
- **Property tests:** gate monotonicity and no-leak (18), story prefix byte-prefix/round-down (17), renderer determinism (19).
- **Shared vectors:** one JSON file run by the C# and Python suites (19).
- **Security tests:** path containment, loopback guard, secrets only in environment variables, oversize/invalid inputs.
- **Hardware / real-model (`@hardware`, `@spike`, opt-in):** after Batch 2 and Batch 3; results go into the spike reports.
- Existing suites stay green at every step.

## 6. Files expected to change
AI Companion plugin (settings, migration, provider, services, templates, RPC, UI sources), `Core/Extensions/LlmContracts.cs`, `Core/Extensions/IReaderServices.cs` + `Enrichment/ReaderContextService.cs`, `Core/Epub` (shared text extractor moved from DeckBuilder), `App/Hosting/MainWindowHost.cs`, `client/src/services/selfTestRunner.ts`, tests; **new projects** `knowledge-graph/` and `tts-service/`. **Not touched:** `enrichement-extractor`, the library, the deck builder (apart from the extractor move).

## 7. Risks (summary; details in each spec)
Spoiler leak through free text or retro-interpretation (scanner, causal extraction, evidence positioning) · microphone unavailable in the window (S4) · voice drift (S6) · extraction quality of the hosted model (K0) · coach language drift (P10) · GPU memory for LLM + TTS (S5) · two languages implementing prompts (shared vectors) · reader position to text offset mapping (chapter granularity first).

## 8. "Go" checklist: what I need from you to start Batch 1
Defaults I will use unless you say otherwise (each is reversible):
1. **Python project:** new sibling `knowledge-graph/` (not inside `enrichement-extractor`, so the offline invariant stays true). It uses `networkx` and an HTTP client.
2. **Store:** one SQLite file per series (source of truth), NetworkX built from it. (Kuzu not used: archived Oct 2025.)
3. **Gate granularity v1:** completed chapters only; the current chapter's facts stay hidden until offset mapping is proven.
4. **Earlier volumes** count as read; later volumes never visible.
5. **Extraction:** causal, one chapter per request, hosted model via per-role override (blank = Settings → AI).
6. **"The system should create the TTS"** = the system *creates the voice design (instruction text) for the TTS from the gated character profile*, editable by you; the TTS service itself stays separate. Is that what you meant?
7. **Push-to-talk key:** I need a default that does not collide with your reader's Vim motions. My suggestion: hold the on-screen talk button, or hold **`T`** while the companion drawer has focus; both configurable (`pushToTalkKey`). Which key do you prefer?
8. **Coach:** separate call after the reply, default language English, not spoken, on by default for spoken turns.
9. **Prompt syntax:** the small `{{variable}}`/`#if`/`#each` syntax with shared test vectors (no templating library).
10. **Python environment:** `knowledge-graph/` gets its own documented venv; the plugin tells you the exact command if it is missing.

Say **"go"** (or change any default) and I start with Batch 1: tasks K1 to K13 and P1 to P3, test-first, no network.
