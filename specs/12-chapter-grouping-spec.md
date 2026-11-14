# Specification 12: Chapter Grouping (one chapter per ToC entry)

## Status: Approved for implementation
**Scope:** C# chapter model (`ImmersiveReading.Core/Epub`), persistence + lazy upgrade of existing books (`ImmersiveReading.Library`), merged chapter rendering (`ImmersiveReading.Enrichment`), reader navigation (client), tests, wiki.
**Plan:** [12-chapter-grouping-implementation-plan.md](12-chapter-grouping-implementation-plan.md) · **Feature file:** [features/10-chapter-grouping.feature](features/10-chapter-grouping.feature)
**Decisions (user, 2026-11-14):** (1) one chapter per ToC entry, shown as one continuous document; (2) existing books are rebuilt **lazily when opened**.

---

## 1. Problem

`ChapterListBuilder` creates one chapter record per **spine file**. A spine file with its own ToC entry becomes a chapter; a spine file without one becomes `"<previous title> (Part N)"` or `"<previous title> (Illustration)"`. Publishers (and Calibre) routinely split one logical chapter into several files and put illustrations on their own pages, so a book shows many bogus chapters, e.g. *chapter, (Illustration), (Part 2), (Illustration), (Part 3)*.

Measured on the user's library (2,223 books): **88,460 chapter records, 34,283 (39%) are generated `(Part N)` / `(Illustration)` records.** Worst case 超訳 自省録 has 480 records, 235 of them `(Part 2)`, because every ToC entry is a `_split_000.html` + `_split_001.html` pair.

## 2. Current behavior (verified in code)

| Fact | Where |
| :--- | :--- |
| One `BookChapterRecord` per spine item; ToC entry matched by `spineHref`, first entry per file wins | `ChapterListBuilder.BuildChapterList` |
| No-ToC spine items in the story region become continuation records | same, "Continuation or in-chapter illustration" branch |
| Records carry `EndSpineHref`/`EndFragmentId`, persisted but **never populated or used** | `BookModels.cs`, `book_chapters.end_spine_href` |
| The reader renders `ir-book://<book>/<spineHref>` = one enriched document per chapter record; token ids restart at 0 per document; `ChapterTokenMap` is keyed `(bookId, chapterHref)` | `BookSchemeHandler.GetEnrichedChapter`, `AngleSharpDomEnricher` |
| Progress, bookmarks, quiz sessions store `chapter_index` (+ `spine_href`, token ids, char offsets) | `reading_progress`, `bookmarks`, `comprehension_sessions` |
| Python study pack derives its own chapters; it does not read `book_chapters` | `enrichement-extractor` (verified by `--verify-reader` before/after) |

## 3. Requirements

### Chapter model
- **FR-1** A story chapter = a ToC entry plus all following spine files up to (not including) the next ToC entry. Text parts and illustration pages are **members** of that chapter, in spine order.
- **FR-2** Chapter title is the ToC title. No `(Part N)` / `(Illustration)` records are generated for the story region.
- **FR-3** Chapter ids are `chapter-NN` (no `-pK` suffix). Numbering is by ToC chapter.
- **FR-4** Front/back matter keeps its exclusion rules. A spine file without a ToC entry is merged into the preceding **front/back record only if that record came from a ToC entry**; otherwise it stays its own excluded record.
- **FR-5** Book with **no usable ToC**: each *text* spine file in the story region is a chapter (`Chapter N`); illustration-only files are merged into the preceding chapter.
- **FR-6** `BookChapterRecord` gains `MemberSpineHrefs` (primary first). `SpineHref` stays the first member, `EndSpineHref` = last member when there is more than one. `CharCount` = sum over members; `CumulativeChars` stays consistent.

### Rendering
- **FR-7** Opening a chapter renders **one continuous document** containing every member in order, illustrations inline. Relative asset URLs of each member resolve against that member's own path.
- **FR-8** Token ids, paragraph ids and the `ChapterTokenMap` are produced by **one** enrichment pass over the merged document and stored under the chapter's primary `SpineHref`. Everything that keys on `(bookId, primary spineHref)` (AI context, insights, vocabulary, POS cache) keeps working unchanged.
- **FR-9** A request for a non-primary member href resolves to its chapter's merged document. In-book links to a member file open the owning chapter.

### Existing data
- **FR-10** `books.chapter_model_version` (new, default 1). New imports are written as version 2. A version-1 book is **rebuilt from its EPUB the first time it is read** (`GetBookDetail`), transactionally: chapters replaced, then progress / bookmarks / quiz sessions remapped, then version set to 2. Failure leaves the book untouched at version 1 and is logged (it is retried on next open; the reader still works with the old list).
- **FR-11** Remapping: `reading_progress` and `bookmarks` map by `spine_href` to the chapter that owns it. Position inside the *primary* file is preserved exactly (token ids and offsets are unchanged because the primary member is rendered first). A position inside a *non-primary* member degrades to the start of the chapter (token/offset reset) and the bookmark keeps its text and note. `comprehension_sessions` (index only) maps old index → new chapter by the old record's href.

### Client
- **FR-12** Chapter dropdown, prev/next and progress use the grouped chapters unchanged. `loadChapter(spineHref)` finds the chapter by primary **or member** href.

### Non-functional
- **NFR-1** Offline, deterministic, no new dependencies.
- **NFR-2** Merged rendering must not change the output for single-member chapters (byte-identical enriched HTML) so existing behavior and tests hold.
- **NFR-3** A dry-run verification (`--verify-chapters`) rebuilds chapter lists for every book **without writing** and reports before/after totals, so the effect on the real library is measurable.

## 4. Design

### 4.1 Builder (`ChapterListBuilder`)
Single pass over the spine, tracking an "open" record per region:
```
for each spine item i:
  region = front | story | back            (unchanged classification)
  if item has ToC entry                      -> start new record (as today), members=[item]
  else if region == story and a story record is open   -> append item to its members   (FR-1)
  else if region != story and open record came from ToC -> append                       (FR-4)
  else if book has no ToC (FR-5)             -> text item: new record 'Chapter N'; illustration-only item: append to previous
  else                                       -> own excluded record (front/back, as today)
after loop: set EndSpineHref, sum CharCount, assign chapter ids/indexes/cumulative chars
```
Illustration detection stays `FrontMatterClassifier.IsIllustrationSpineItem`.

### 4.2 Persistence
Migration v4 (`LibraryMigrations`):
```sql
ALTER TABLE book_chapters ADD COLUMN member_spine_hrefs TEXT NOT NULL DEFAULT '[]';
ALTER TABLE books ADD COLUMN chapter_model_version INTEGER NOT NULL DEFAULT 1;
```
`[]` means "just the primary" for version-1 rows, so old data still reads correctly before it is upgraded. `BookImportService` writes `member_spine_hrefs` and `chapter_model_version = 2`. `BookQueryService` reads the new column.

### 4.3 Lazy upgrade (`ChapterModelUpgrader`, Library)
`LibraryManager.GetBookDetail(bookId)` calls `EnsureCurrent(bookId)` first (cheap check on `chapter_model_version`). The upgrader: reads the EPUB via the existing metadata extractor path → new chapter list → single SQLite transaction: delete/insert `book_chapters`, update `books.chapter_count`, remap `reading_progress` / `bookmarks` / `comprehension_sessions` per FR-11, set version 2. Concurrency: guarded per book id so two simultaneous opens upgrade once.

### 4.4 Rendering (`ChapterDocumentComposer`, Enrichment)
`BookSchemeHandler.GetEnrichedChapter(bookId, chapterHref)`:
1. Resolve the owning chapter (by primary or member href) from `GetBookDetail`.
2. Read the raw XHTML of each member.
3. **Single member → exactly today's path** (NFR-2).
4. Several members → `ChapterDocumentComposer.Compose(bookId, members)`: parse each member, run `EpubUrlRewriter.RewriteRelativeAssetUrls(doc, bookId, memberHref)` per member (so each resolves its own relative paths), then append each member's `<body>` children into the first document's body wrapped in `<div class="ir-spine-part" data-ir-spine-href="…">`, copying the body's `class`/`style` attributes onto the wrapper; union `<head>` `link`/`style` elements (de-duplicated by href/text). Serialize and pass to `EnrichChapter` with `chapterHref = primary`. Because URL rewriting already happened, the enricher's own rewrite pass is idempotent on `ir-book://` URLs.
5. Token map is stored under the primary href (existing `_cache.Set`).

Known limitation: duplicate `id` attributes across members are possible but harmless for reading; in-chapter fragment anchors work when ids are unique.

### 4.5 Client
`loadChapter` resolves `chapters.findIndex(c => c.spineHref === h || c.memberSpineHrefs?.includes(h))`. `ChapterSummary` type gains `memberSpineHrefs`. No other client change.

### 4.6 Why not the alternatives
| Option | Verdict |
| :--- | :--- |
| Keep per-file pages, hide continuations in the list | Rejected by the user: a chapter would still be several documents; progress/insights stay per-file |
| Split by ToC *fragment* inside a file (multiple chapters in one XHTML) | Out of scope here (different problem; `EndFragmentId` columns exist for a follow-up) |
| Rebuild every book at startup | Rejected by the user: long first run, higher risk |

## 4b. As built (deviations and additions)
- **No migration v4.** `LibraryMigrations.All` is not applied anywhere (verified), so the new columns are added by an idempotent `EnsureColumn` in `LibraryDatabase.InitializeSchema` plus `SchemaSql` for fresh databases.
- **"No usable ToC" means no story-titled entry** (a ToC listing only a cover and a colophon counts as none), which keeps the existing extractor test and its intent.
- **Front matter** also collapses runs of generated `口絵 (Illustrations)` pages (seen in real data: nine front records, seven identical), not only ToC-owned continuation.
- **Back matter**: any following file without a ToC entry joins the previous back record.
- `ChapterStore` (shared insert) was extracted so import and upgrade write chapter rows identically.
- Merged chapters mark `<img>` as `loading="lazy"` because image-only books produce chapters of 100+ files (22 of 2,223 books above 40 files; max 266).
- Re-importing an existing book does not set version 2 (chapters are rewritten, version left as is); the lazy upgrade then runs once more, remapping by href, which is harmless.
- Not done (out of scope): splitting one XHTML file into several chapters by ToC fragment; a cap/split for very large image-only chapters; bookmarks in continuation files keep text/note but lose their exact position.

## 5. Edge cases
- ToC entry pointing at a file that is not in the spine: ignored, as today.
- Two ToC entries pointing at the same file: first wins, as today.
- Chapter whose first member is an illustration page and the text follows: stays one chapter (illustration is a member).
- A book whose every file has a ToC entry: unchanged (single-member chapters).
- All chapters excluded safety guard: unchanged.
- Upgrade of a book whose EPUB file is missing or corrupt: skipped, logged, stays version 1.
- Very large merged chapter: same enrichment cost as the sum of its members; acceptable (a few hundred KB of text).

## 6. Test plan
**Builder** (synthetic EPUB zips, like `EpubMetadataExtractorTests`): split-pair collapses; illustration pages between text parts collapse; front matter with ToC-owned continuation; no-ToC book; all-single-file book unchanged; ids/titles/cumulative chars; `MemberSpineHrefs`/`EndSpineHref`.
**Composer**: member order; per-member relative URL resolution; head de-duplication; single-member output byte-identical to the old path; token ids contiguous and map stored under primary.
**Upgrader**: v1 → v2 row replacement; progress remap (primary exact, member degrades); bookmark remap; quiz-session remap; idempotent; failure leaves v1.
**Handler**: member href resolves to chapter document.
**Library dry run**: `--verify-chapters` reports totals over the real 2,223-book library (expected: generated records drop from 34,283 to ~0).
**Regression**: `dotnet test`, `--verify-reader`, `--verify-ui`.
