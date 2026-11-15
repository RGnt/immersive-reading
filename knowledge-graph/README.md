# knowledge-graph: spoiler-gated character knowledge graph

Builds, **once per book/series, chapter by chapter**, a knowledge base of characters, relationships, events and speech
samples, where every fact is gated by story position (volume, chapter) and backed by a verbatim quote. A chat with a
character asks "what is known about X as of where the reader is" and gets a small, spoiler-free profile.

Spec: [`specs/18-character-knowledge-graph-spec.md`](../specs/18-character-knowledge-graph-spec.md) · plan:
[`specs/18-character-knowledge-graph-implementation-plan.md`](../specs/18-character-knowledge-graph-implementation-plan.md).

**This project is deliberately separate from `enrichement-extractor`.** The study pipeline must stay offline and LLM-free
(AGENTS.md invariant 1). This builder *does* call an LLM (any OpenAI-compatible endpoint, for example your FreeToken host
at `http://127.0.0.1:1919/v1`), so it lives in its own project with its own environment. Nothing here is imported by the
study pipeline, and the study pipeline is not imported here.

## Setup
```powershell
cd k:\immersive-reading\knowledge-graph
uv venv --python 3.12 .venv
uv pip install --python .venv\Scripts\python.exe -e .
.venv\Scripts\python.exe -m unittest discover -s tests -v
```
Dependencies: `networkx` only (the HTTP client is the standard library).

## Build a knowledge base
1. `book-text.json` (the app writes it; for a manual run see the contract in spec 18, section 6.1):
   `{"series_key", "title", "language": "ja", "volumes": [{"volume": 1, "chapters": [{"index": 0, "title": "...", "text": "..."}]}]}`
2. `config.json`, no secrets in it:
   ```json
   { "endpoint": "http://127.0.0.1:1919/v1", "model": "", "output_language": "Japanese",
     "temperature": 0.2, "max_tokens": 16000, "timeout_seconds": 900, "thinking": "off",
     "max_input_chars": 90000, "max_ops": 120 }
   ```
   `model` may be empty when the server lists exactly one model. `thinking` is `off` (default), `on` or `default` (leave it to the server);
   it is passed as `chat_template_kwargs.enable_thinking`. **Keep it off for extraction**: a reasoning model otherwise spends its whole
   output budget on hidden reasoning and returns nothing (measured on Qwen3.6-35B-A3B: 5,992 reasoning tokens and an empty answer, versus
   a valid answer in 5 s with thinking off). The key, if the server needs one, goes in the environment variable
   `KNOWLEDGE_API_KEY` (or the variable named by `api_key_env`), never in the file or on the command line.
3. Run:
   ```powershell
   .venv\Scripts\python.exe -m immersive_knowledge build --db knowledge.db --book-text book-text.json --config config.json
   ```
   Progress is one JSON object per line on stdout. A second run continues where a failed run stopped; unchanged chapters
   are skipped, a changed chapter rebuilds itself and everything after it (later chapters were built on its facts).
   `--from VOLUME:CHAPTER` forces a rebuild from a chapter, `--no-resume` rebuilds everything.

Exit codes: `0` ok · `1` verify found problems · `2` invalid input · `3` LLM failure (bad key, unreachable, ...) · `4` a chapter
failed (resumable).

## Pronouns
A pronoun (わたし, 僕, 俺, 彼 ...) is never a character's name. The prompts tell the model to resolve it to the person it stands for (the narrator, the speaker); the snapshot names the current `narrator`; `ops` refuses pronoun names, and an answer that still uses one is asked once more. Databases built before this change may contain a character called わたし: rebuild from the chapter where it first appears (`--from`), or link it to the real character.

## Events and items (the story pass)
A second, optional pass (`"story_pass": true` in `config.json`; the app switches it on) records what happened and who has which item, with the same
causal rules and verbatim evidence (spec 22). It runs after the character pass, can be added to an already built series, and never touches the
character data. Extra config keys: `story_snapshot_characters`, `story_snapshot_others`, `story_snapshot_items`, `story_snapshot_events`, and `prompts_dir`: a folder of `<name>.prompt.md` files that replace the built-in prompts of the same name (the app writes the user's edited, validated copies there; a file that does not load, or a name this builder does not have, stops the build with exit code 2 before anything is sent).
```powershell
.venv\Scripts\python.exe -m immersive_knowledge events --db knowledge.db --position 1:3 [--character e0001] [--kind hunt]
.venv\Scripts\python.exe -m immersive_knowledge items  --db knowledge.db --position 1:3 [--holder e0001]
```
Databases of schema version 1 are migrated in place the first time this version opens them.

## Query
```powershell
.venv\Scripts\python.exe -m immersive_knowledge characters --db knowledge.db --position 1:3
.venv\Scripts\python.exe -m immersive_knowledge profile    --db knowledge.db --character e0001 --position 1:3
.venv\Scripts\python.exe -m immersive_knowledge verify     --db knowledge.db
```
`--position VOLUME:CHAPTER` means "the reader is at the start of this chapter (0-based)": everything established in earlier
chapters is visible, nothing from this chapter on.

## How the spoiler safety works (summary)
1. **Causal extraction:** the model only ever receives the snapshot of what is already known and the one chapter being processed.
2. **Evidence positioning:** every operation cites verbatim quotes; code locates them and positions the fact at the end of the
   latest quote. A quote that is not in the text rejects the operation.
3. **One gate** (`gate.py`) decides visibility for everything; it is tested against a brute-force oracle on random stores.
4. **Hidden-name scanner** (`scanner.py`): any text that names someone the reader has not met yet is blocked before it is stored.
5. Precomputed **profile snapshots** per chapter boundary; the app only looks them up.

## Layout
`position.py` positions · `store.py` SQLite · `evidence.py` quote locator · `ops.py` validation and apply · `gate.py` visibility ·
`scanner.py` hidden names · `profile.py` profiles and snapshots · `story.py` event and item cards (spec 22) · `builder.py` causal loop · `llm.py` client ·
`templates.py` prompt renderer (shared test vectors with the C# app, `specs/fixtures/prompt-vectors.json`) · `cli.py` ·
`prompts/*.prompt.md`. Tests use a synthetic "Test Saga" with a secret reveal and a canary word (`tests/saga.py`) and a "Loot Saga" with hunts, drops and gear changes for the story pass (`tests/loot_saga.py`).
