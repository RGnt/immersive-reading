# Specification 17: Spoken Character Companion (voice chat with book characters)

## Status: Draft for confirmation (2026-11-14). **No implementation has started.**
**Source:** `temp/ai_companion_voice_128k_context_implementation_plan.md` (the "source plan"), reviewed against the code as of this date.
**Scope:** `ImmersiveReading.Plugins.AiCompanion` (persona chat), host additions needed for audio, one external TTS service contract.
**Plan:** [17-voice-character-companion-implementation-plan.md](17-voice-character-companion-implementation-plan.md) · **Feature file (basic test cases):** [features/13-voice-character-companion.feature](features/13-voice-character-companion.feature)
**Builds on:** [05](05-extensibility-and-ai-companion-spec.md) (AI companion), [11](11-plugin-ui-loading-spec.md) (plugin UI), [16](16-deck-llm-enrichment-spec.md) (LLM client lessons).

This spec covers **Phase A: the voice engine and a turn-based spoken chat with a book character**. **Phase B (avatars: visual character, discovery of characters, multi-character scenes, streaming/duplex conversation, per-character memory) is deliberately not specified here**; it will be planned later (section 9 lists what Phase A must leave open for it).

---

## 0. Decisions recorded (owner, 2026-11-14) and companion specs
Decisions: endpoints configurable (default in Settings → AI plus per-role overrides); **TTS is a separate system** with its own connection; personas build on **`book_personas`**; context behaviour stays as today but the **limit is configurable**; **push to talk** (microphone records only while the key is held, release sends; silence-based capture later); **Japanese books only**; riskiest logic first. New scope that shapes this spec: the **spoiler-gated knowledge graph** ([spec 18](18-character-knowledge-graph-spec.md)) is the preferred source of character knowledge, the system **creates the TTS voice design** from the gated profile, a separate **conversation coach** gives improvement feedback ([spec 19](19-prompt-templates-and-conversation-coach-spec.md)), and all new prompts are **templates with variables inserted at send time** (spec 19, part A). Program order and defaults: [plan 17](17-voice-character-companion-implementation-plan.md).

## 1. Goal
A learner reading a Japanese book can **speak** to a character of that book and hear the character **answer in a voice that fits the character**, with the character knowing only what the reader has read so far (zero spoilers) and speaking at a vocabulary level matched to the learner (existing n+1 budget).

Phase A is *turn-based push-to-talk* ("realtime-ish"): record, send, reply as text and speech. It is not full duplex and not streaming (see 8, non-goals).

## 2. What the source plan proposes (summary)
1. Append-only 128K "story so far" prefix so a local `llama-server` reuses its KV cache between turns.
2. `<|think|>` only for quizzes and character analysis, never for live chat.
3. Native audio input: user speech goes to the model as a 16 kHz mono WAV (`input_audio`), the model returns `{userTranscript, reply, emotionPrompt}`.
4. Character voices: the book text is analysed once to produce a `voiceDesignPrompt` per character; each reply is synthesised by a local TTS (Qwen3-TTS VoiceDesign) with the turn's `emotionPrompt`; a seed and a reference WAV "lock" the timbre.
5. Persona UI: voice designer modal, push-to-talk button, emotion badge, replay button.

The direction is sound and fits the plugin architecture. The review below lists what must change before it can be built.

## 3. Review findings: where the source plan does not match this repository
| # | Source plan says | Reality in the repo | Decision for this spec |
| :-- | :--- | :--- | :--- |
| F1 | New table `ai_personas`; `ALTER TABLE ai_chat_messages` | Personas live in **`book_personas`**, sessions in `persona_chat_sessions`, messages in **`persona_chat_messages`** (legacy C# migration `AiCompanionMigrations` v1, main DB). `ai_chat_messages` / `ai_quiz_sessions` (embedded `001_initial_ai_schema.sql`, plugin DB) are not used by `PersonaChatService`. The plan's ALTER would hit a table nobody writes | Extend the existing tables with a **v2 additive migration** (voice columns on `book_personas`, emotion/audio columns on `persona_chat_messages`); no second persona table (FR-20) |
| F2 | Plugin settings `endpointUrl`, `apiKey`, `modelId` (+ more) | The provider is built from the **app config `Llm`** (Settings → AI). The plugin's `endpointUrl`/`apiKey`/`modelId` are only read by the `testEndpoint` action: two competing sources of truth | One source: Settings → AI. Plugin settings keep only voice/TTS/context options, plus an optional `voiceModelId` override (FR-3) |
| F3 | `StoryPrefixCacheService` calls `GetStoryContextUpToProgressAsync(bookId, chapter, viewport, maxContextChars, ct)` and reads `slice.Text` | Real signature is `(bookId, overrideChapterIndex, overrideSpineHref, overrideViewportTokenId, ct)`, the slice property is `PlainText`, and the service **caps earlier chapters at 1,200 chars and the current chapter at 3,500 chars** (it is a summary, not the full text). `using ImmersiveReading.Core;` does not exist | New service reads full chapter text from the chapter token maps up to the viewport (FR-10). The existing method stays unchanged for the quiz/text chat |
| F4 | "quantize to 500-char chunks" (no code) | If the viewport is rounded **up** to a chunk boundary, up to 499 characters the reader has *not* read are sent to the model: a spoiler leak | Round **down**; the unfinished tail chunk is never sent (FR-11, invariant I1). Cost: the model may lag the reader by up to one chunk |
| F5 | `request.EnableThinking`, `UserAudioWavBase64`, `_llm.GenerateStructuredJsonAsync` | `ILlmProvider` takes an `LlmRequest(Messages, Temperature, MaxTokens, TaskKind)`; none of those members exist | Extend `LlmRequest` additively (`EnableThinking`, `Audio`, `CachePrompt`, `Timeout`), keep the interface (FR-4..FR-6) |
| F6 | Voice turns reuse the provider | `OpenAiCompatibleLlmProvider` **silently falls back to canned answers** when the endpoint fails and uses `LlmSettings.TimeoutSeconds` (default 15 s). A first 128K prefill takes far longer, and a canned fake reply would be spoken in the character's voice | Voice and persona-extraction calls must **fail loudly** (no fallback) and use a per-task timeout (FR-6; lesson from spec 16) |
| F7 | Voice turn: model returns `reply`, plugin synthesises it | Text chat audits every reply with the **n+1 vocabulary budget** and may rewrite it (`PersonaChatService`) | The spoken reply is the **audited / rewritten** text, never the first candidate (FR-14) |
| F8 | Frontend in `Plugins.AiCompanion/client/src/...`, `npm run build`, `dist/ui.js` | The plugin UI is `Assets/ui.src.jsx` bundled by `npm run build:plugins` in the app client into `Assets/ui.js` (one ES module; host-provided imports only: react, react-dom, react/jsx-runtime, lucide-react, `@ir/sdk`) | Same mechanism: extra components/hooks live in `Assets/*.src.jsx` and are bundled into `ui.js`; checklist of [plugin-ui-authoring](../wiki/plugin-ui-authoring.md) applies |
| F9 | `getUserMedia` in the drawer | `MainWindowHost` never enables media streams for the Photino/WebView2 window, so the microphone is very likely **blocked today** | Host work item: enable microphone capture for the window (exact Photino/WebView2 API to be confirmed in spike S4) |
| F10 | `ScriptProcessorNode` capture | Deprecated; runs on the main thread | `AudioWorklet` (loaded from a blob URL) with resampling to 16 kHz; fall back to `ScriptProcessorNode` only if the worklet cannot be created |
| F11 | `python/tts_server.py` "launched on port 8081 using the portable Python environment" | The portable Python environment belongs to the offline study pipeline (no torch, no `qwen-tts`, no model download). Nothing defines who starts/stops the server, GPU memory sharing with the LLM, or first-run model download (network) | **v1: the TTS server is an external, user-run service.** The plugin only has `ttsEndpointUrl`, a connection test and a health check. `tts_server.py` ships as a *reference implementation with a README*, not as a managed process (FR-30). Managed lifecycle is a later decision |
| F12 | "Locks the character's voice with a seed and a reference WAV" | In the sketch the server ignores `reference_audio_path`; a fixed seed only makes *identical* requests repeatable. A VoiceDesign model re-derives the voice from the instruction on every call, so timbre may drift between different sentences | Voice consistency is an **open technical question, measured in spike S6** before the UI is built; the persona record stores `voice_design_prompt`, `voice_seed` and optional `reference_wav_path` regardless (FR-21) |
| F13 | `reference_audio_path` sent to the TTS server | An arbitrary path read by another process | Only files inside the plugin's `voices/` directory, validated by containment; `ttsEndpointUrl` must be loopback unless the user explicitly allows a remote host (I4, I5) |
| F14 | "`<120 ms` response latency" | Not achievable for audio-in + LLM + TTS on this hardware; it is a time-to-first-token figure at best | Requirement is stated as a **measured budget** (NFR-1) taken from the spike, not a number from the plan |
| F15 | `<|think|>` prefix, `llama-server` `input_audio`, "Gemma 4 E4B/E2B", "Qwen3-TTS-12Hz-1.7B-VoiceDesign", Python `qwen_tts` API | Cannot be verified from this repository (model, runtime and package names are taken from the plan) | Listed as **assumptions A1-A6**; the spike confirms each on the user's machine before any product code depends on it |
| F16 | Prefix is "append-only" so the KV cache is reused | Cache reuse also requires: the persona/system text and everything dynamic (n+1 targets, the new user turn, timestamps) placed **after** the story prefix, byte-identical text for identical positions, and the request flag that enables prompt caching on the server | Prefix layout rules in FR-12; `cache_prompt` and cache-hit measurement in the spike |
| F17 | One persona per name with `Guid` id | `ListPersonas` already seeds a default persona and `CreatePersona` takes samples and spoiler bounds | Reuse `PersonaChatService` persona CRUD; voice fields are added to `BookPersonaRecord` |

## 4. Functional requirements (Phase A)
### Context and model calls
- **FR-1** The voice chat uses the existing persona chat session (`persona_chat_sessions` / `persona_chat_messages`); a voice turn is a message like any other with extra columns.
- **FR-2** Chat replies use no thinking; quiz generation and persona extraction use thinking when `enableThinkingForQuizzes` / the task asks for it. The provider adds the thinking marker only when `LlmRequest.EnableThinking` is true (marker string confirmed in spike S2, A2).
- **FR-3** Endpoint, key and model come from `AppConfig.Llm` (Settings → AI) as the default. **Per-role overrides** (plugin settings, each blank = default) let the owner point different jobs at different services: `voiceModelId` (audio turns; an audio-capable model is often not the text-chat model), the knowledge builder (spec 18), the coach (spec 19). Every override can set endpoint, model and key. Keys are never logged.
- **FR-4** `LlmRequest` gains optional `EnableThinking`, `Audio` (WAV bytes + format), `CachePrompt` and `Timeout`, all defaulting to the old behaviour. Existing callers and tests do not change.
- **FR-5** With `Audio` set, the provider sends an OpenAI-compatible `input_audio` content part (`format: wav`) before the text part.
- **FR-6** Voice turns and persona extraction **never** use the offline heuristic fallback; HTTP failures surface as errors with status and body. Their timeout comes from the request (default 120 s for a turn, 600 s for extraction), not from `LlmSettings.TimeoutSeconds`.
- **FR-7** The model answers one JSON object `{userTranscript, reply, emotionPrompt}`. A missing `userTranscript` is allowed when the turn was typed text. Malformed JSON is retried once, then reported.

### Story prefix
- **FR-10** `StoryPrefixService` builds the prefix from the **full plain text** of chapters `0..current-1` plus the current chapter up to the reader's position, using the chapter token maps (not the capped summaries).
- **FR-11** The current-chapter portion is cut at `floor(position / ChunkChars) * ChunkChars` characters (default chunk 500). Nothing at or after the reader's position is included (I1). Chapters flagged excluded are skipped.
- **FR-12** Layout, in this order: fixed system/persona block, `=== STORY SO FAR ===`, chapters in order with stable headings, `=== END ===`; **then** per-turn data (n+1 targets, history, the user turn). The prefix contains no timestamps, ids that change per turn, or random ordering. For positions p1 <= p2 in the same book, `prefix(p1)` is a byte prefix of `prefix(p2)` up to the chunk boundary (property tested).
- **FR-13** `maxContextChars` caps the prefix by dropping the **oldest whole chapters** first and replacing them with a one-line marker; it never truncates mid-chapter at the end (that would change the cached bytes every turn).
- **FR-14** The reply that is shown and spoken is the one that passed the n+1 vocabulary audit (including the rewrite step) of the text chat. Spoken text equals displayed text. The learner's utterance is additionally sent to the coach (spec 19) after the reply; the coach never delays or alters the reply.

### Personas and voices
- **FR-20** `book_personas` gains `voice_design_prompt`, `voice_seed`, `reference_wav_path`, `extracted_at_chapter`, `extracted_at_token` (additive migration v2). `persona_chat_messages` gains `emotion_prompt`, `audio_wav_path`, `user_transcript`.
- **FR-21** A persona is created **from the knowledge graph** (spec 18): the user picks a character from the list of characters known **as of the reader's position**; the persona's system prompt (template `persona.system`) uses that character's gated profile snapshot, and its voice design is **created by the system** (template `voice.design`, English acoustic wording) as an editable proposal that is saved only by `savePersona`. If no knowledge base exists for the book, `extractPersonaFromBook` (template `persona.extract`, story text up to the reader's position, thinking enabled) is the fallback. The persona remembers the position it was created at and its `knowledge_character_id`.
- **FR-22** `voiceDesignPrompt` is English acoustic wording (gender, age, pitch, timbre, pace, manner); dialogue stays in the book's language.
- **FR-23** `savePersona` validates lengths and that `referenceWavPath` (if any) is inside the plugin voices directory.

### TTS
- **FR-30** TTS is a **separate system**: an HTTP service with its own connection settings (`ttsEndpointUrl`, optional `ttsApiKey`, `ttsTimeoutSeconds`, `ttsLanguage`), contract in 6.3. The app never starts, stops or installs it; a reference implementation lives in its own project folder `tts-service/` (not in the plugin). `testTtsEndpoint` reports reachability, latency and the reported model; unreachable TTS never breaks text chat (the turn returns text with `audio: null` and a visible reason).
- **FR-31** `synthesize(persona, text, emotionPrompt)` sends text, the combined instruction (`voiceDesignPrompt` + current delivery), `seed` and language; the returned WAV is validated (RIFF/WAVE header, PCM, plausible duration) and stored under `<plugin data>/voices/<personaId>/`.
- **FR-32** Audio storage is bounded: oldest files are evicted over a configurable limit (default 500 MB), same approach as the POS cache. The user's **microphone recordings are not stored** unless a setting says so (default off).

### Audio capture and UI
- **FR-40** The host enables microphone capture for the app window; the first use shows a clear permission result, and a denied or missing microphone is reported in the drawer, never as a silent failure.
- **FR-41** **Push to talk:** the microphone records **only while** the talk key (`pushToTalkKey`) or the on-screen talk button is held; **releasing sends** the recording; Escape cancels without sending. Output is mono 16 kHz 16-bit PCM WAV (resampled if the device does not provide 16 kHz), with a maximum duration (default 30 s) and an input level indicator. Capture is behind a recording-mode seam (`pushToTalk` now; `silenceTimeout`, i.e. stop after a period of silence, is reserved for later and must not require changes to the turn pipeline).
- **FR-42** The drawer shows the model's **transcript of what it heard** under the user's bubble so mishearing is visible, an emotion badge on replies, a replay button, and auto-play per setting.
- **FR-43** Persona voice modal: character name, "Design voice from book", editable prompt and seed, preview line, save.
- **FR-44** All text from the model that is shown goes through the existing enrichment (`ISnippetEnricher`) so words are clickable; audio never replaces text.

## 5. Non-functional requirements
- **NFR-1 Latency budget:** defined from spike S3/S6 on the reference hardware (RTX 5080, 16 GB; Ryzen 9 9950X3D; 64 GB) and written into the plan before UI work: *warm* turn (prefix cached) time to reply text; time to first audio; first (cold) prefill time. No figure is promised before measurement.
- **NFR-2 Resource limits:** the plan records measured VRAM for the LLM and TTS together; if both do not fit, text-only fallback is the supported behaviour (FR-30).
- **NFR-3 Testability:** every network dependency (LLM, TTS) is replaceable by an in-process fake so the suite runs without GPU, microphone or models.
- **NFR-4 Privacy:** voice and book text go only to the endpoints the user configured; the UI states when an endpoint is not loopback.

## 6. Contracts
### 6.1 Settings (plugin, additive)
`pushToTalkKey` (default decided in the plan), `prefixChunkChars` (500), `maxContextChars` (existing key, new default for prefix mode is set from spike S3), `nativeAudioInput` (bool), `voiceModelId` (string, optional), `enableThinkingForQuizzes` (true), `ttsEnabled`, `ttsEndpointUrl` (default `http://127.0.0.1:8081`, `x-rpc-action: testTtsEndpoint`), `ttsLanguage` (`Auto`), `autoPlayCharacterVoice`, `maxRecordingSeconds` (30), `voiceCacheMaxMb` (500), `storeUserRecordings` (false), `allowRemoteEndpoints` (false; I5). Context: `contextStrategy` is `auto` (default: knowledge-graph profile if one exists for the book, otherwise the current summary), `summary`, `knowledge` or `appendOnlyPrefix`; `maxContextChars` stays the configurable limit of the summary/prefix text. Added by the other specs: knowledge builder settings (spec 18, section 7) and coach settings (spec 19, B3). Plugin-level `endpointUrl`/`apiKey`/`modelId` duplicates are not added (F2); role overrides replace them.

### 6.2 RPC (`ext.aiCompanion.*`, additive; existing actions unchanged)
| Action | Request | Response |
| :--- | :--- | :--- |
| `extractPersonaFromBook` | `{ bookId, characterName, chapterIndex, viewportTokenId }` | persona record (proposal, saved only by `savePersona`) |
| `savePersona` | persona record | persona record |
| `sendVoiceTurn` | `{ bookId, personaId, chapterIndex, viewportTokenId, audioWavBase64 }` (or `text`) | `{ userTranscript, userTranscriptHtml, assistantHtml, rawReply, emotionPrompt, audioWavBase64?, audioError?, audit }` |
| `synthesizeMessageAudio` | `{ personaId, text, emotionPrompt? }` | `{ audioWavBase64 }` |
| `testTtsEndpoint` | `{ ttsEndpointUrl }` | `{ ok, latencyMs, model, error? }` |
`listPersonas` keeps its name and now returns the voice fields.

### 6.3 TTS service (HTTP, loopback by default)
`GET /health` → `{ ok, model }`. `POST /v1/tts/voice-design` with `{ text, instruct, language, seed, reference_audio_path? }` → `audio/wav` (mono PCM; sample rate reported in the WAV header, not assumed). Non-200 responses carry a text body that is shown to the user.

### 6.4 Audio formats
Input to the LLM: WAV, PCM 16-bit, mono, 16,000 Hz. Output of TTS: WAV, PCM, any sample rate (read from the header).

## 7. Invariants
- **I1 Zero spoiler:** nothing at or after the reader's current position is ever sent to a model, whether in the story prefix or in persona extraction. Guarded by property tests.
- **I2 No silent fakes:** voice/extraction paths never return canned text; failures are visible.
- **I3 Spoken = shown:** the audio is the synthesis of the audited reply.
- **I4 Path containment:** every file path exchanged with the TTS server or stored in the DB is inside the plugin data directory.
- **I5 Loopback by default:** non-loopback LLM/TTS endpoints work only with an explicit user setting and are labelled in the UI.
- **I6 Source inputs untouched:** books are read, never modified; no audio is written outside the plugin data directory.
- **I7 Python study pipeline unaffected:** nothing here adds torch, a model or a network call to `enrichement-extractor`.

## 8. Non-goals (Phase A)
Avatars or any visual character; several characters in one scene; full-duplex or barge-in; streaming TTS; speech-to-text without the LLM; voice cloning of real people; mobile; managing the TTS server process; training or fine-tuning.

## 9. Left open for Phase B (design hooks only)
- Persona and voice are addressed by `personaId`; an avatar can attach to the same id.
- Voice engine is behind a small `ISpeechSynthesizer`-style seam so other TTS backends can be added.
- Turn result carries `emotionPrompt` (and may later carry viseme/emotion tags) without a contract break.
- Conversation state is the existing chat session; per-character memory would extend it, not replace it.
- The character list now comes from the knowledge graph (spec 18); an avatar image/expression set could be attached to a `knowledge_character_id`.

## 10. Assumptions to verify (spike, see plan M0)
| # | Assumption (from the source plan) | Spike |
| :-- | :--- | :--- |
| A1 | A local `llama-server` serves the chosen Gemma 4 E4B/E2B model with its `mmproj` and accepts `input_audio` WAV on `/v1/chat/completions` | S1 |
| A2 | The thinking marker is `<|think|>` placed at the start of the system prompt, and thinking output does not pollute `content` (or is separable) | S2 |
| A3 | `cache_prompt` reuse works across turns with an append-only 128K prefix on that model (iSWA), with measurable `cache_n` / timing | S3 |
| A4 | WebView2 hosted by Photino can capture the microphone once media streams are enabled | S4 |
| A5 | The TTS package exposes a VoiceDesign API as sketched (`generate_voice_design(text, language, instruct)`) and fits next to the LLM in 16 GB VRAM | S5 |
| A7 | The hosted large-context model (reference Qwen3.6-35B-A3B on FreeToken) returns valid operation JSON with verifiable quotes for the knowledge builder (spec 18) | K0 |
| A6 | Voice timbre stays recognisably the same across different sentences for a fixed `voice_design_prompt` and seed; the emotion text changes delivery without changing the speaker | S6 |

## 11. Risks
Highest risk first: A6 (consistency) and A4 (microphone in the window host) can each change the UI plan; A3 decides whether the 128K prefix is worth its complexity versus the current summary context; GPU memory may force text-only or sequential model use; transcript hallucination by the audio model (mitigated by showing the transcript, FR-42); first-turn prefill latency (mitigated by warming the prefix when the drawer opens, to be decided after S3).

## 12. Acceptance (Phase A)
All scenarios in [features/13-voice-character-companion.feature](features/13-voice-character-companion.feature) pass with in-process fakes; the opt-in hardware scenarios pass on the reference machine; `dotnet test` and `--verify-ui` stay green; wiki, user guide and lessons updated (AGENTS.md rules 7 and 8).
