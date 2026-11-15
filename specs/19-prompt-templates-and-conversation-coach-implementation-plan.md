# Implementation Plan 19: Prompt Templates and the Conversation Coach

Spec: [19-prompt-templates-and-conversation-coach-spec.md](19-prompt-templates-and-conversation-coach-spec.md) · Feature file: [features/15-prompt-templates-and-coach.feature](features/15-prompt-templates-and-coach.feature).
Program order: [plan 17 section 1](17-voice-character-companion-implementation-plan.md).

**Status: P1 (shared vectors) and P3 (Python renderer) done; the C# renderer (P2) and everything else not started.**

## 1. Order (riskiest first)
The renderer is tiny but everything depends on it and it exists twice (C#, Python), so it is built first, against **shared test vectors**, before any feature code. The coach is built after the voice turn pipeline exists, because it needs the transcript and the turn events.

## 2. Tasks
| # | Task | Files | Done when | Status |
| :-- | :--- | :--- | :--- | :--- |
| P1 | Shared vectors: `prompt-vectors.json` (cases for every rule in spec A2: variables, defaults, enums, types, `#if`, `#each`, untrusted fencing incl. forged closing marker, errors with messages, determinism) | `specs/fixtures/prompt-vectors.json` (read by both suites) | file reviewed against spec A2 | ✅ done (33 vectors) |
| P2 | C# renderer `PromptTemplate` (parse front matter, validate declared vs used variables, render, untrusted blocks) | AI Companion plugin `Prompts/Templates/` | all vectors pass; extra unit tests for malformed front matter | ⬜ |
| P3 | Python renderer (same behaviour) | `knowledge-graph/immersive_knowledge/templates.py` | all vectors pass (task K11) | ✅ done |
| P4 | Template store: embedded defaults, user override folder, validation, error surfacing, hot reload on file change | `Prompts/Templates/TemplateStore.cs` | override tests (valid replaces, invalid ignored + reported) | ⬜ |
| P5 | Initial built-in templates (`persona.system`, `persona.turn`, `voice.design`, `persona.extract`, `coach.improve`; `knowledge.extract` lives in the Python package) | `Prompts/*.prompt.md` (embedded) | each template validates; every declared variable is used | ⬜ |
| P6 | Settings and schema for coach options (+ `promptDebug`); settings guard test | `settings.*.json` | guard test green | ⬜ |
| P7 | `CoachService`: build variables, render `coach.improve`, call the LLM (own model option, thinking option, retry once on invalid JSON, no fallback), parse/validate the contract, cap items, store feedback | `Services/CoachService.cs` + migration v2 addition `coach_feedback` | all `@fake` coach scenarios | ⬜ |
| P8 | Turn integration: start the coach after the character reply, deliver via event `coachFeedback`, `requestCoachFeedback` with `transcriptOverride`, failure isolation | `AiCompanionPlugin.cs`, `VoiceTurnService` | "does not delay the reply" and "failing coach never affects the conversation" tests | ⬜ |
| P9 | UI: collapsible feedback card under the learner bubble, "heard as", edit transcript and re-run, language/strictness quick settings | `Assets/ui.src.jsx` | `--verify-ui` step | ⬜ |
| P10 | Spike: 20 real utterances on the chosen model: language compliance, over-correction rate, usefulness of naturalness tips, latency; decide `enableThinkingForCoach` default | `specs/19-spike-report.md` | report committed | ⬜ |
| P11 | Docs: wiki, user guide (how to change the coach language, how to override a prompt), lessons | consistent | ⬜ |

## 3. Tests
Shared vectors (both languages), override tests, coach scenarios with the fake LLM (request bodies recorded to assert template name, language, absence of book knowledge, ordering after the reply), failure isolation, storage/removal with the chat. No network.

## 4. Decisions taken
Minimal custom syntax instead of a templating library (identical behaviour in two languages, no dependency, small surface for injection); user overrides are plain files in the plugin data folder; prompts for existing quiz/persona-text features migrate later.

## 5. Rollback
Additive: new templates, new table/columns with defaults, new settings. `coachEnabled=false` returns today's behaviour.
