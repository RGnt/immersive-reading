# Specification 19: Prompt Templates and the Conversation Coach

## Status: Part A implemented in both languages with 33 shared vectors (`specs/fixtures/prompt-vectors.json`): Python `knowledge-graph/immersive_knowledge/templates.py`, C# `Core.Prompts.PromptTemplate`. The template store with user overrides (PT-5) is `Core.Prompts.PromptCatalog`, host-wide (`<data root>/prompts/`, not per plugin), edited in Settings → Prompts ([spec 25](25-editor-prompts-and-custom-css-spec.md)). Coach (part B): not implemented.
**Scope:** (A) a prompt-template system used by the new AI features (C# in the AI Companion plugin, Python in `knowledge-graph/`); (B) the **Conversation Coach**: separate feedback ("improvements") on what the learner said, in a configurable language.
**Plan:** [19-prompt-templates-and-conversation-coach-implementation-plan.md](19-prompt-templates-and-conversation-coach-implementation-plan.md) · **Feature file:** [features/15-prompt-templates-and-coach.feature](features/15-prompt-templates-and-coach.feature)
**Used by:** [17](17-voice-character-companion-spec.md), [18](18-character-knowledge-graph-spec.md).

---

# Part A: Prompt templates

## A1. Why
Today prompts are C# raw strings with compile-time interpolation (`PersonaPrompts`, `QuizPrompts`, `GradingPrompts`). The new features need prompts that (1) live in files that can be read, versioned and tuned without recompiling, (2) take **variables inserted at send time** (language, learner level, character name, utterance, ...), (3) treat untrusted text (book text, speech transcripts) safely, and (4) behave identically in C# and Python.

## A2. Requirements
- **PT-1 Template files.** A prompt is a text file `<name>.prompt.md`: a short front-matter block (`---` fenced, simple `key: value` lines and a `variables` list) followed by the body. Front matter: `name`, `version` (integer), `purpose`, `variables` (each: `name`, `required` or `default`, optional `enum`, optional `type`: string | int | list), optional `response: json` marker.
- **PT-2 Syntax.** `{{name}}` inserts a variable. `{{#if name}}…{{/if}}` includes a section only when the variable is non-empty. `{{#each name}}…{{this}}…{{/each}}` repeats for list variables. Nothing else (no expressions, no loops over objects, no includes): the syntax is small on purpose so both implementations stay identical.
- **PT-3 Strict rendering.** A variable used in the body but not declared, a required variable that is missing, a value outside its `enum`, or a wrong type is an **error** naming the template and variable; nothing is sent. Declared-but-unused variables are reported by the validator (a warning in tests).
- **PT-4 Untrusted content blocks.** Variables flagged `untrusted: true` (book text, transcripts, user messages, quotes) are inserted inside a fenced block `<<<NAME … NAME>>>` and any occurrence of the closing marker inside the value is neutralised, so text cannot close the block or inject instructions that look like part of the prompt. Trusted variables (language, level, names chosen by the user in settings) are inserted as is.
- **PT-5 Defaults and overrides.** Built-in templates ship embedded in the plugin (and in the Python package for builder prompts). A user override file with the same name in `<plugin data>/prompts/` replaces the built-in after passing the same validation (front matter, declared variables, `version` ≥ built-in's major contract, required variables still present). An invalid override is ignored and the error is shown in the plugin settings, never half-applied.
- **PT-6 Language variables.** `language` and similar variables are plain trusted strings (`English`, `日本語`, `Español`...) taken from settings at send time. Templates state the language rule in the body ("Write explanations in {{coach_language}}; keep Japanese examples in Japanese").
- **PT-7 Shared test vectors.** One JSON file of render cases (template, variables, expected output or expected error) is run by **both** the C# and the Python test suites; a change to either implementation that diverges fails a test.
- **PT-8 Determinism.** Same template + same variables ⇒ same bytes (needed for prompt-cache reuse and for tests).
- **PT-9 Debugging.** `promptDebug` setting (default off) writes rendered prompts to `<plugin data>/prompt-debug/` (rendered text contains book text: off by default, documented as such). Rendered prompts are never written to normal logs.
- **PT-10 Scope.** New features first (persona turn, voice design, persona extraction, coach, knowledge extraction). **The existing quiz, grading and persona prompts have been migrated** (spec 25): same instructions, untrusted text now fenced.

## A3. Built-in templates (initial set)
| Name | Used for | Main variables |
| :--- | :--- | :--- |
| `persona.system` | character roleplay system prompt | `character_name`, `profile_block` (from spec 18), `speech_samples` (list), `reader_position`, `learner_level`, `reply_language` |
| `persona.turn` | one turn (after the story/knowledge block) | `known_target_words` (list), `history` (untrusted), `user_message` (untrusted) |
| `voice.design` | profile → English TTS voice instruction | `profile_block`, `tts_language` |
| `persona.extract` | (optional) persona proposal when no knowledge graph exists | `character_name`, `story_text` (untrusted) |
| `knowledge.extract` | per-chapter operations (spec 18) | `snapshot_json`, `chapter_text` (untrusted), `chapter_title`, `output_language`, `max_ops` |
| `coach.improve` | learner feedback (Part B) | `coach_language`, `learner_level`, `strictness`, `utterance` (untrusted), `character_reply` (untrusted), `character_register`, `max_items` |

---

# Part B: Conversation Coach ("improvements")

## B1. Goal
After the learner speaks or types to a character, a **separate** helper looks only at *what the learner said* and offers: grammar fixes, particle/conjugation/word-choice corrections, politeness-register fixes, and **naturalness tips** (how to sound less textbook or "mechanical": contractions, fillers, sentence-final particles, dropped subjects, more idiomatic phrasing). It is independent of the character: the character never sees it and keeps answering in character.

## B2. Requirements
- **CO-1 Separate call.** The coach is its own LLM call (own template `coach.improve`, optional own model `coachModelId`, optional thinking), started **after** the character reply is produced so the reply latency is unchanged; its result arrives as a second event and appears under the learner's bubble as a collapsible card.
- **CO-2 Language.** Explanations are written in `coachLanguage` (setting; default English; free text so any language the model supports works; changing it applies to the next turn). Japanese examples and corrections stay Japanese. Story sentences are never translated (project invariant); only the learner's own utterance and its corrections are discussed.
- **CO-3 Output contract** (JSON): `{ "hasIssues": bool, "heardAs": "…", "corrected": "…", "items": [ { "category": "grammar|particle|conjugation|vocabulary|register|naturalness", "original": "…", "suggestion": "…", "explanation": "…", "severity": "minor|moderate|major" } ], "naturalness": [ { "tip": "…", "alternatives": ["…","…"] } ], "praise": "…" }`. Invalid JSON is retried once, then shown as "feedback unavailable" with the reason; the chat is not affected.
- **CO-4 Level-aware.** `learnerLevel` (setting: JLPT N5..N1 or "auto" from the known-vocabulary profile) and `strictness` (low/medium/high) steer how much is corrected; `maxItems` (default 3) caps the card so a beginner is not flooded; the most important issue is first.
- **CO-5 ASR honesty.** For spoken turns the coach receives the model's **transcript**, which can contain recognition errors that look like grammar mistakes. The card shows "heard as: …" first; the user can correct the transcript and re-run the coach on the corrected text; the template tells the model to say so when an error is probably a mishearing rather than a learner error. Pronunciation is **not** judged (only a transcript is available).
- **CO-6 Placement.** Not spoken by default (`coachSpeak`, false; future). Stored linked to the learner message so history keeps it; deletable with the chat.
- **CO-7 Optional and cheap to turn off.** `coachEnabled` (default true for spoken turns, user-configurable); disabled ⇒ no call is made.
- **CO-8 Privacy and spoilers.** The coach sees only the learner's utterance, the character's last reply (already shown to the reader) and the character's speaking register; it receives **no book knowledge**, so it cannot leak spoilers.
- **CO-9 No fallback.** If the endpoint fails the card says so; no canned feedback is ever produced.
- **CO-10 Failure isolation.** Coach errors never delay, block or alter the character reply or its audio.

## B3. Settings (AI Companion plugin, additive)
`coachEnabled`, `coachLanguage` (default `English`), `coachModelId` (blank = chat model), `coachEndpointUrl`/`coachApiKey` (blank = Settings → AI), `learnerLevel` (`auto`), `strictness` (`medium`), `coachMaxItems` (3), `enableThinkingForCoach` (decided from a spike), `coachSpeak` (false), `promptDebug` (false).

## B4. RPC (`ext.aiCompanion.*`, additive)
`requestCoachFeedback { messageId, transcriptOverride? }` → `{ feedback }`; `getCoachFeedback { messageId }`; the turn result of `sendVoiceTurn`/`sendMessage` carries `coachPending: true` and the card arrives as event `coachFeedback { messageId, feedback | error }`.

## B5. Invariants
- **I-C1** The coach never receives book text or knowledge-graph content.
- **I-C2** The character reply path never waits for, or fails because of, the coach.
- **I-C3** No canned feedback.
- **I-C4** Rendered prompts follow PT-3/PT-4 (strict variables, untrusted blocks).

## B6. Non-goals
Pronunciation scoring, spaced repetition of the learner's mistakes, grading over time, spoken feedback, correcting the character's Japanese.

## B7. Risks
Over-correction of casual speech (mitigated by level/strictness/`maxItems`); ASR errors presented as learner errors (CO-5); model explains in the wrong language (template rule + a test with a fake model that ignores it is not possible, so a spike checks real compliance); added token cost per turn (separate model and the off switch).
