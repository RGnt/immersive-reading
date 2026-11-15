# Spec 20 — Character Knowledge plugin (build and browse the character graph from the app)

Status: **implemented** (plugin `characterKnowledge`, UI verified by `--verify-ui`, 22 C# tests). Builds on [spec 18](18-character-knowledge-graph-spec.md) (the Python builder and its contracts). Feature file: [features/16-character-knowledge-plugin.feature](features/16-character-knowledge-plugin.feature).

## 1. Purpose

Spec 18's builder is a command line tool. This plugin lets the user build the knowledge graph of a book or a whole series from the library and the reader, watch the progress, and browse "who is this character as of the chapter I am reading", without touching a terminal, except for the one-time setup of the builder's Python environment.

## 2. Scope and non-goals

In scope: setup check, model endpoint test, estimate, build / continue / rebuild / cancel, progress, deletion, browsing characters and profiles gated by reading position, UI in three places.

Not in scope here: the spoken companion that consumes the profiles (spec 17), prompt templates and the coach (spec 19), offset-level (inside a chapter) gating.

## 3. Architecture

```
UI (Book Info tab, series modal, reader drawer)
   │  JSON-RPC  ext.characterKnowledge.<action>
CharacterKnowledgePlugin ── SeriesResolver (book → series key + volume ordinals)
   │                       ── BookTextExporter (EPUB → book-text.json, text hashes)
   │                       ── KnowledgeBuildRunner (python -m immersive_knowledge build, JSON-lines progress)
   └─ KnowledgeDb (read-only SQLite: status, characters, profile snapshot lookup)
```

* **No gating logic in C#.** The builder precomputes a gated profile snapshot per chapter boundary (spec 18). C# only picks "the latest snapshot at or before the reader's chapter" and never reads facts directly. The current chapter is hidden: a reader in chapter N sees what was known before N.
* **Storage:** `<plugin data>/knowledge/<seriesKey>/{knowledge.db, volumes.json, job/book-text.json, job/config.json}`. `seriesKey` is `series-<sha1(lowercase series name)[..12]>` or `book-<sha1(bookId)[..12]>` for standalone books.
* **Volumes:** volume ordinals are 1..n in reading order (`SeriesIndex`, then sort title, then id). The numbering used by a build is stored in `volumes.json`; later volumes get the next number, built volumes are never renumbered. If a new volume sorts *before* built ones the status reports `orderChanged` and the UI asks for a rebuild.
* **Chapters** are the library's chapters (`book_chapters.chapter_index`), so the reader position maps directly. Excluded chapters are not exported. A spine file belongs to the first chapter that references it.
* **Staleness:** the exporter hashes each chapter text (SHA-256 of UTF-8) and the builder compares with `chapters.text_sha`; changed text rebuilds that chapter and everything after it.
* **One build at a time**, and a build is a child process: cancel kills the process tree, finished chapters are committed one by one, so cancelling or crashing is resumable.

## 4. RPC contract (`ext.characterKnowledge.*`)

| Action | Payload | Result |
|---|---|---|
| `getStatus` | `{bookId}` or `{seriesName}` | series + volumes (built/failed/chapterCount), `currentVolume`, `readingChapterIndex`, builder `{found, problem}`, llm `{configured, endpoint, model, usesSettingsAi}`, knowledge counts + failed chapters, `job` |
| `testEndpoint` | `{}` | `{ok, latencyMs, model, advertisedContext}` or `{ok:false, error}` |
| `estimateBuild` | `{bookId|seriesName, scope: upToVolume|series}` | chapters, characters of text, already up to date, `requests`, `requestsIfSmallContext` |
| `startBuild` | same + `rebuild` | `{started, chapters}`; errors if the builder is missing, no endpoint is configured, or a build is running |
| `getBuildStatus` / `cancelBuild` | `{bookId|seriesName}` | job state (`running|done|failed|cancelled`, done/total/skipped, ops, splits, learned context limit, current chapter, error, log tail) |
| `listCharacters` | `{bookId, chapterIndex?}` | `{asOf:{volume,chapter}, characters:[{characterId,name,prominence}]}`; chapter defaults to the reading position |
| `getProfile` | `{bookId, chapterIndex?, characterId}` | the stored snapshot (traits, relationships, timeline, speech samples) and its prompt block; any member id of an identity group resolves |
| `listEvents` | `{bookId, chapterIndex?, characterId?, kind?, limit?}` | `{asOf, total, events[]}`: the stored event cards as of the chapter, newest first (spec 22) |
| `listItems` | `{bookId, chapterIndex?, holderId?}` | `{asOf, items[]}`: the stored item cards (holder, history), optionally only what one character has (spec 22) |
| `deleteKnowledge` | `{bookId|seriesName}` | removes the series' knowledge folder (refused while building) |

## 5. Configuration

Plugin settings (`characterKnowledge`): endpoint URL (default `http://127.0.0.1:1919/v1`), model id, API key, thinking (`off` default), context tokens (0 = let the builder discover the server's real limit), output language, max input characters, request timeout, minimum prominence, **events and items (story pass, default on, spec 22)**, knowledge-graph folder, Python executable. **If the endpoint is left empty the plugin uses Settings → AI.** The API key is passed to the builder only through the `KNOWLEDGE_API_KEY` environment variable: never on the command line, never in `config.json`, never logged.

## 6. Privacy

The text of the book is sent, chapter by chapter, to the endpoint the user configured (a local server by default). The UI says so next to the Build button. Nothing is sent anywhere else, and nothing is built without a click.

## 7. UI

* **Book Info → Characters tab:** *Characters* view (chapter selector defaulting to the reading position, filter, list, profile, a notice that the current chapter is hidden) and *Build* view (status, scope "this volume and earlier" / "whole series", test connection, estimate, build/continue, rebuild, cancel, delete, progress with polling every 1.5 s, failed chapters).
* **Series action "Characters"** (library): the Build view for the whole series with the per-volume table.
* **Reader drawer "Story":** characters, events and items, following the chapter being read.
* **Events and Items views** (spec 22): filterable event graph (larger happenings contain their parts) and the items each character has, with history; the profile shows equipment.

## 8. Failure behaviour

| Situation | Behaviour |
|---|---|
| Builder folder or venv missing | Build disabled; the panel shows what is missing and the exact setup command |
| No endpoint configured / unreachable | Build disabled / clear message from the test; the builder never falls back to another answer |
| Server limit smaller than advertised | Handled by the builder (learns the limit, shrinks, splits); surfaced as "chapters split" and "server limit N tokens" |
| Chapter fails | Marked failed, build ends with exit code 4, "Build / continue" retries it |
| App closed during a build | The child process is cancelled with the plugin; resume later |
| Database being written while read | Read-only open with a 5 s timeout; a locked or half-created database reads as empty |

## 9. Tests

`CharacterKnowledgeTests` (App.Tests): snapshot lookup (fallback to earlier boundary, never a later one, identity-group member ids, no file lock), series numbering (standalone, order, stability, order change), text export (ruby removed, each file once, volume cut-off, excluded chapters, hash vector), builder location and setup messages, progress-event parsing, process start/exit/cancel with a real child process, the key never on a command line, config never contains the key, up-to-date counting. UI: `--verify-ui` checks the tab, drawer and series action register.
