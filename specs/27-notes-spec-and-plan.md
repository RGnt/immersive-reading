# Spec 27: Notes (plan, not implemented)

Status: **proposed. Nothing in this document is built yet.** It is the plan for the notes feature that [spec 25](25-editor-prompts-and-custom-css-spec.md) left open ("the editor component is ready"). Decisions that need the owner's confirmation are collected in section 13, each with a recommended answer; the plan assumes the recommendations.

## 1. Goal and non-goals

**Goal.** Let the reader write notes about a book while reading it, in a real editor (Monaco), tied to the place in the book they are about, findable later (by book, chapter and text), and owned by the user (exportable as plain Markdown). Notes must be quick to start: select words, press a key, type.

**Non-goals for the first versions.** Real-time sync, collaboration, images and attachments, a plugin API for notes, two-way sync with an external vault (Obsidian), AI-written notes, handwriting, notes for series as a stored object (see decision D1).

**Why now.** Bookmarks already store a place and one line (spec 26); the editor exists; the knowledge hub (spec 24) can later give characters and tutors read access to what the user wrote.

## 2. Decisions (recommended)

| # | Decision | Recommended | Alternative | Why |
| :--- | :--- | :--- | :--- | :--- |
| D1 | Scope of a note | **Book** (anchored to a place or not) and **Global** (not about one book: study notes). A "series view" is a query over its books' notes, not a stored scope | A note stored per series name | A series is only a name string: renaming it (bulk edit, spec 23) would orphan the notes, the same root cause as review finding 1 of the knowledge graph key |
| D2 | Storage | **Rows in `library.db`** (`notes`, `note_revisions`, `notes_fts`) with export to Markdown files | Markdown files in a folder ("vault") as the source of truth | Same transaction and cascade as the book (delete, duplicates), one backup, FTS. A vault mirror can be added later as an export option |
| D3 | Format | **Markdown source**, edited in Monaco, with a preview toggle | Rich text | Monaco is the editor the project chose; Markdown is portable |
| D4 | Anchor | **Chapter + token range + the quoted text**; re-anchored by searching the quote when the chapter was re-enriched | Character offsets only | Token ids change when a chapter is re-enriched; the quote survives (bookmarks already carry offsets and token ids) |
| D5 | Creating a note | A small **floating toolbar on text selection** in the book page (Note, Bookmark, Look up) plus a shortcut | Only a button in a drawer | The shortest path from "this sentence" to typing |
| D6 | Where the editor lives | A **Notes drawer** in the reader (list + editor), a **Notes tab** in the book dialog, and a **Notes view** in the library (all books, search) | One of them | The reader drawer is where notes are made, the library view is where they are found |
| D7 | Characters and tutors reading notes | **Off by default**, one setting; only notes anchored at or before the reader's chapter | On | Notes are private and may contain spoilers of later chapters; see section 8.4 |
| D8 | Revisions | Keep the **last 20** per note, restore from the editor | None | An autosaving editor needs an undo that survives closing it |
| D9 | Core or plugin | **Core** (Library domain + client), a knowledge source registered by the host | A plugin | Notes are tied to books and their lifecycle like bookmarks; plugins stay replaceable |

## 3. Requirements

**Functional**
* N-1 Create, edit, delete a note; title is the first line (or the first heading) of the text, derived, never typed separately.
* N-2 A book note can be anchored: chapter, token range and quote. Without a selection it is anchored to the chapter being read, or to nothing (a book-level note).
* N-3 Starting a note from a selection puts the selection in as a Markdown quote (`> quote`) at the top; the cursor is placed below it.
* N-4 Autosave: the text is saved 800 ms after the last keystroke, on losing focus, on closing the drawer, and when the window closes. The editor shows "Saved", "Saving…" or "Not saved: <reason>"; a failed save never discards the text.
* N-5 Notes of the open book are listed with the current chapter's notes first; each shows its title, chapter and date; click opens it; "Open in book" jumps to the anchor.
* N-6 Library-wide search over title and text, Japanese included (substrings of two or more characters), filter by book, chapter, global, date; results show a snippet.
* N-7 Markdown preview (headings, lists, quotes, emphasis, links, code); internal links `[[ch12]]` jump to that chapter of the same book; `#tags` are indexed and clickable (search by tag).
* N-8 Revisions: the last 20 saved versions of a note can be listed and restored.
* N-9 Export: a book's notes (or all) as Markdown files into a folder the user picks (one file per book, notes separated by headings, front matter with book, chapter, quote and dates), and "copy as Markdown" for one note.
* N-10 Deleting a book deletes its notes; resolving duplicates moves the notes (and the bookmarks) of the removed books to the kept book when "merge data" is chosen.
* N-11 Notes show the same in all themes (spec 25 section 4b: contrast checked), use square corners and the editor follows the app theme; Custom CSS hooks `data-ir="notes"`, `notes-list`, `notes-editor`.

**Non-functional**
* N-12 A note is at most 200,000 characters; refusing a larger paste says so. Up to 50,000 notes stay fast (paged lists, FTS).
* N-13 Opening the editor costs nothing until used (Monaco is lazy); the list opens in under 100 ms for 1,000 notes.
* N-14 No note text is ever sent anywhere by this feature. Section 8.4 is the only way it reaches a model, and it is opt-in.
* N-15 Markdown is sanitised before it is shown; a note can never run script or load a remote resource (the EPUB-sandbox finding of the 2026-11-15 review applies to notes too).

## 4. Domain and architecture

Follows `wiki/dotnet-architecture.md` section 1 and is enforced by `ArchitectureTests`.

```
Core        Core.Notes: NoteModels (records), INoteCatalog (read side for other features), NoteAnchor, NoteMarkdown helpers that are pure (title, tags, links)
Library     Notes/NoteService (all SQL), NoteSearch (FTS query building), NoteExporter, NoteAnchorResolver   <- data access lives here
App         Rpc/Modules/NotesRpcModule (parameters in, result out, no SQL: the SQL ceilings in ArchitectureTests do not grow)
Knowledge   (phase 4) none: the notes source is registered by the App, because Knowledge may only reference Core
Client      stores/useNotesStore.ts, components/notes/*, editor via components/editor/CodeEditor (markdown language added to loadMonaco.ts)
```

* **No new project.** Notes are a Library concern (they live and die with books). `INoteCatalog` in Core is the contract a knowledge source or a future plugin reads through.
* **The knowledge source** (phase 4) is a small class in the App project that implements `IKnowledgeSource` over `INoteCatalog` and is registered in `AppServices` next to the user memory.
* **Client state.** One `useNotesStore` (list of the open book's notes, the open note, save state, search results). The editor component is the existing `CodeEditor`; one Monaco instance is reused (documents are models, as in the prompt editor).

## 5. Data model

Added to `LibraryDatabase.SchemaSql` (`CREATE TABLE IF NOT EXISTS`) and `EnsureColumn` where a column is added later (the migration list is not applied: see the lesson of spec 26).

```sql
CREATE TABLE IF NOT EXISTS notes (
    note_id          TEXT PRIMARY KEY,
    book_id          TEXT REFERENCES books(book_id) ON DELETE CASCADE,   -- NULL = a global note
    title            TEXT NOT NULL DEFAULT '',                           -- derived from the text, stored for listing
    body_md          TEXT NOT NULL DEFAULT '',
    chapter_index    INTEGER,                                            -- NULL = book-level
    spine_href       TEXT NOT NULL DEFAULT '',
    start_token_id   INTEGER,
    end_token_id     INTEGER,
    quote            TEXT NOT NULL DEFAULT '',                           -- the selected text the note is about
    anchor_state     TEXT NOT NULL DEFAULT 'none',                       -- none | exact | moved | detached
    pinned           INTEGER NOT NULL DEFAULT 0,
    created_at_utc   TEXT NOT NULL,
    updated_at_utc   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_notes_book ON notes(book_id, chapter_index, updated_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_notes_updated ON notes(updated_at_utc DESC);

CREATE TABLE IF NOT EXISTS note_tags (note_id TEXT NOT NULL REFERENCES notes(note_id) ON DELETE CASCADE, tag TEXT NOT NULL, PRIMARY KEY (note_id, tag));
CREATE INDEX IF NOT EXISTS idx_note_tags_tag ON note_tags(tag);

CREATE TABLE IF NOT EXISTS note_revisions (
    revision_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    note_id          TEXT NOT NULL REFERENCES notes(note_id) ON DELETE CASCADE,
    body_md          TEXT NOT NULL,
    saved_at_utc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_note_revisions ON note_revisions(note_id, revision_id DESC);

-- trigram: Japanese has no word spaces, and unicode61 (used by books_fts) cannot find a word inside a sentence
CREATE VIRTUAL TABLE IF NOT EXISTS notes_fts USING fts5(note_id UNINDEXED, title, body_md, quote, tokenize = 'trigram');
```

* **Search.** A query of three or more characters uses `notes_fts MATCH`; one or two characters (common in Japanese) fall back to `LIKE` over title, body and quote (a short scan; the table is small). `#tag` queries use `note_tags`.
* **Revisions** are written when the saved text differs from the last revision by more than whitespace, at most once a minute per note; rows beyond 20 per note are deleted in the same transaction.
* **Anchor states.** `exact`: the token range still spells the quote. `moved`: the quote was found elsewhere in the chapter and the range was updated. `detached`: the quote cannot be found (the chapter changed); the note keeps its chapter and quote and the UI says "the text has changed". Resolution runs when a chapter is opened and when a note is opened, never in a background sweep.
* **FTS rows** are maintained by `NoteService` inside the same transaction as the note (explicit inserts and deletes, as `FtsIndexer` does for books); deleting a book cascades to `notes`, and `DeleteBookInTransaction` also clears the matching `notes_fts` rows (its list of statements gets one more entry).

## 6. API (JSON-RPC, `notes.*`)

| Method | Params | Result |
| :--- | :--- | :--- |
| `notes.list` | `{bookId?, scope?: "book"\|"global"\|"all", chapterIndex?, tag?, limit?, offset?}` | `{notes: [NoteSummary], total}` (no body, only title, snippet, anchor, dates) |
| `notes.get` | `{noteId}` | `{note}` with body and anchor state (resolved) |
| `notes.create` | `{bookId?, chapterIndex?, spineHref?, startTokenId?, endTokenId?, quote?, body?}` | `{note}` (the body starts with `> quote` when a quote is given) |
| `notes.update` | `{noteId, body, expectedUpdatedAtUtc}` | `{note}` or `{conflict: true, note}` when the stored version is newer (the same note open in two places) |
| `notes.delete` | `{noteId}` | `{ok}` |
| `notes.search` | `{query, bookId?, limit?, offset?}` | `{results: [{note, snippet}], total}` |
| `notes.revisions` / `notes.restore` | `{noteId}` / `{noteId, revisionId}` | list / `{note}` |
| `notes.export` | `{bookId?, folder}` | `{files, notes}` (the folder comes from the folder picker the host already has) |
| `notes.counts` | `{}` | `{perBook: {bookId: n}, global, total}` for the badges |

Events: `notes.changed {bookId, noteId, kind}` so a drawer and a library view open at the same time stay in step.

## 7. User interface

* **Selection toolbar (reader).** The reader script in the chapter iframe reports the selection (text, token range, rectangle) to the host with `postMessage`; the host shows a small toolbar next to it: **Note**, **Bookmark**, **Look up**. It disappears when the selection does and never covers the selected text. Shortcut: `Ctrl+Shift+N` (note from the selection or the current chapter). This reuses the selection capture that **Bookmark here** already needs.
* **Notes drawer (reader).** Right side like the bookmarks drawer. Top: search box and **New note**. The list shows this chapter's notes first, then the rest of the book. Selecting a note opens the editor in the same drawer; a button expands the drawer to a wide editor with the preview beside it.
* **Editor.** `CodeEditor` with language `markdown`; status line (Saved / Saving… / error), character count, **Preview** toggle, **Revisions…**, **Delete**. The quote block of an anchored note is shown above the text as a header ("ch12: 「…」", with Open in book).
* **Book dialog.** A **Notes** tab (count in the tab label), the same list and editor.
* **Library Notes view.** A **Notes** entry in the sidebar next to Lists: search, filters (book, global, tag, date), result list with snippets; **Open** shows the editor in a dialog, **Open in book** jumps to the anchor.
* **Keyboard.** `Esc` closes the editor (after a save); `Ctrl+S` saves now; list navigation with arrows; the drawer is reachable from the reader top bar and by shortcut.
* **Looks.** Square corners and the contrast rules of spec 25 section 4b; notes surfaces are added to the contrast audit; `data-ir` hooks listed above.

## 8. Integration with the rest of the app

1. **Delete and merge.** Notes cascade with the book. `DuplicateService.Resolve` with `mergeData` also moves notes **and bookmarks** of removed books to the kept book (today bookmarks are lost on a merge: found while writing this plan; fixed in the same change with a test).
2. **Bulk edit and series rename.** Nothing to do: notes do not reference series names (D1).
3. **Bookmarks.** Separate objects. A bookmark's one-line `user_note` stays. The bookmark list shows a note icon when a note is anchored at the same chapter and quote; "Add note here" in the bookmark drawer is the same action as the selection toolbar's Note.
4. **Knowledge hub (phase 4, opt-in).** `NotesKnowledgeSource` (`SourceId = "notes"`, scope `book`, gating `StoryPosition`). It returns only notes anchored at a chapter before the viewpoint; notes without an anchor are never returned to a gated query (they could mention anything). Setting "Characters may read my notes" (AI companion), default off, with a visible list of which notes were used in a reply. The persona prompt gets a `{{notes}}` block through a new template `persona.notes` (editable in Settings → Prompts, untrusted variable, fenced).
5. **Prompts.** No LLM feature in phases 1 to 3. Phase 4 adds one template only.
6. **Custom CSS and themes.** Hooks as in section 7; no hard-coded colours (`--text-primary`, `--hover-bg`, `--border-color`).
7. **Backups.** `library.db` already is the backup unit; export gives a human-readable copy.

## 9. Security and privacy

* Markdown is rendered with `marked` and sanitised with `DOMPurify` (both versions pinned, bundled, lazy-loaded with the preview); links open in the system browser only after the host's existing external-link handling; `javascript:` and `data:` URLs are refused; images are not rendered in v1 (an image line shows its alt text).
* No remote resource is fetched by the preview (the sanitiser removes `src`/`srcset`).
* Notes never appear in logs; `notes.export` writes only into the folder the user picked.
* The knowledge source is off by default and its use is visible (section 8.4).

## 10. Phases

Each phase ends with the full test suites green, `--verify-ui` extended, the specs and wiki updated, and a lessons entry for anything non-obvious.

### Phase 1: write and keep notes (core)
1. Core: `NoteModels`, `INoteCatalog`, pure Markdown helpers (title, tags, `[[chN]]`), with unit tests.
2. Library: schema (`SchemaSql`), `NoteService` (create, get, list, update with `expectedUpdatedAtUtc`, delete, tags, FTS maintenance, revisions with the cap and the one-per-minute rule), `LibraryManager` facade, `DeleteBookInTransaction` clears `notes_fts`.
3. App: `NotesRpcModule`; client types; `useNotesStore` with the autosave state machine (debounce, flush on blur/close, conflict handling, failure keeps the text).
4. Client: Notes drawer in the reader (list, editor, save status), Notes tab in the book dialog, `markdown` language registered in `loadMonaco.ts`.
5. Tests: service (CRUD, cascade, tags, revisions cap, conflict), RPC contract, architecture (no SQL in the module), `--verify-ui` (create a note through the real RPC, edit in the real Monaco, see the saved state, delete it).
Done when: a note can be written, found in the list, survives restart, and is gone with its book.

### Phase 2: anchored notes and the library view
1. Selection reporting from the reader script; floating toolbar (Note, Bookmark, Look up); `Ctrl+Shift+N`.
2. `NoteAnchorResolver` (exact, moved, detached) run when a chapter or note is opened; "Open in book" jump; "the text has changed" state.
3. Library **Notes** view: search (trigram FTS plus the short-query fallback), filters, snippets, paging.
4. Duplicate merge moves notes and bookmarks (with a test); `notes.counts` badges.
5. Tests: anchor resolution (text unchanged, shifted, removed), CJK search (two-character query, three-character query, mixed), duplicate merge, selection toolbar geometry in `--verify-ui`.
Done when: selecting a sentence and pressing Note gives an anchored note whose "Open in book" lands on that sentence, and a two-character Japanese search finds it.

### Phase 3: Markdown preview, links, tags, export, revisions UI
1. Preview with sanitising; `[[chN]]` links; `#tag` chips and tag search.
2. Revisions dialog (list, compare to current, restore).
3. Export to Markdown files and "copy as Markdown".
4. Tests: sanitiser cases (script, event handlers, `javascript:`, remote images), export format, link jump, `--verify-ui` preview check; notes surfaces in the contrast audit.
Done when: the preview of a hostile note runs nothing and loads nothing, and exported files read well in a plain editor.

### Phase 4: characters and tutors can read notes (opt-in)
1. `NotesKnowledgeSource` registered by the host; gating by anchor chapter; setting and the "notes used" list in the persona drawer.
2. Template `persona.notes`; PersonaMemory builds the block; budget and fencing.
3. Tests: no unanchored note and no note after the viewpoint is ever returned; off by default; fenced prompt; the hub refuses an unrestricted query.
Done when: with the setting on, a character mentions what the user wrote about an earlier chapter and never about a later one.

## 11. Test plan (summary)

* **Unit (Core.Tests, App.Tests):** title/tag/link extraction; service rules; FTS and fallback search with Japanese fixtures; anchor resolution; revisions cap; export format; sanitiser; knowledge gating.
* **Contract:** every `notes.*` method with the payload the client sends (the tag-filter bug of spec 23 came from a client/server name mismatch: tests send the exact client payloads).
* **Architecture:** `ArchitectureTests` stay green: no SQL in RPC modules; Notes SQL only under `ImmersiveReading.Library`.
* **Real window (`--verify-ui`):** create, edit (real Monaco), autosave state, reload from the server, search, delete; selection toolbar position; preview of a hostile note; contrast across the four themes on the new surfaces; the test restores or deletes everything it creates.
* **Manual before release:** Japanese IME composition inside Monaco (the in-window test cannot type through an IME), vertical-writing books (toolbar placement), a 5,000-character paste, closing the window during a save.

## 12. Risks

| Risk | Mitigation |
| :--- | :--- |
| Selection coordinates from inside the iframe are wrong in vertical writing mode or when zoomed | Report the range rectangle in iframe coordinates and convert in one place; geometry test in both writing modes; fall back to a toolbar at the top of the page |
| An anchor is lost when a book is re-imported or re-enriched | Quote re-anchoring and the `detached` state; the note is never deleted by an anchor failure |
| Losing text on a crash or a failed save | Autosave plus flush on blur/close; failed save keeps the editor text and shows the reason; revisions |
| The same note open in the drawer and the library view | `expectedUpdatedAtUtc` conflict result and the `notes.changed` event |
| Monaco memory with many open notes | One editor, documents are models; a model is disposed when its note is closed |
| `notes_fts` falls out of step with `notes` | Maintained inside the service transaction only; a test mutates through every path (create, update, delete, cascade, merge) and compares counts |
| Spoiler leakage to characters | Off by default, anchor-based gating, test that unanchored and later notes are never returned |
| Scope creep into a note-taking app | The non-goals list; phases end with a usable feature |

## 13. To confirm before implementation

1. **D1** Book and global notes only, series as a view (recommended), or also a stored series scope?
2. **D2** Rows in `library.db` with Markdown export (recommended), or Markdown files as the source of truth?
3. **D5** The floating selection toolbar (recommended), or only a drawer button and the shortcut?
4. **D7** Characters read notes: off by default, anchor-gated (recommended). Is phase 4 wanted at all?
5. **Preview libraries.** `marked` and `DOMPurify` as new dependencies (recommended), or a smaller hand-written Markdown subset?
6. **Revisions.** Keep 20 (recommended) or fewer?
7. **Order.** Phases 1 and 2 first (recommended); phase 3 and 4 afterwards.
