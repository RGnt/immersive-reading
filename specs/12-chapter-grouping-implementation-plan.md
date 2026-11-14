# Implementation Plan 12: Chapter Grouping

Spec: [12-chapter-grouping-spec.md](12-chapter-grouping-spec.md). Work in order; tests are written with the code they cover.

| # | Task | Files | Done when | Status |
| :-- | :--- | :--- | :--- | :--- |
| T1 | Model: `MemberSpineHrefs` on `BookChapterRecord` (default = primary only) | `Core/Library/BookModels.cs` | compiles, existing tests green | ✅ done |
| T2 | Builder rewrite per spec §4.1 (grouping, no-ToC rule, front/back rule, ids, sums, `EndSpineHref`) | `Core/Epub/ChapterListBuilder.cs` | builder tests pass | ✅ done |
| T3 | Builder tests with synthetic EPUBs (all cases in spec §6) | `tests/Core.Tests/Epub/ChapterGroupingTests.cs` (new) | pass; old extractor tests still pass or are updated with reason | ✅ done |
| T4 | Schema columns (`EnsureColumn`; migrations are not applied) + import writes `member_spine_hrefs` / `chapter_model_version=2`; query reads them | `Library/Schema/LibraryMigrations.cs`, `Books/BookImportService.cs`, `Books/BookQueryService.cs` | migration test; round-trip test | ✅ done |
| T5 | `ChapterModelUpgrader` (rebuild, transactional, remap progress/bookmarks/sessions, per-book lock, failure-safe) + hook in `LibraryManager.GetBookDetail` | `Library/Books/ChapterModelUpgrader.cs` (new), `LibraryManager.cs` | upgrader tests pass | ✅ done |
| T6 | `ChapterDocumentComposer` + `BookSchemeHandler` resolves owning chapter, merged render, member-href fallback | `Enrichment/Dom/ChapterDocumentComposer.cs` (new), `BookSchemeHandler.cs` | composer + handler tests pass; single-member output identical | ✅ done |
| T7 | Client: `memberSpineHrefs` in types, `loadChapter` matches member hrefs | `client/src/types/api.ts`, `stores/useReaderStore.ts` | `tsc -b` | ✅ done |
| T8 | `--verify-chapters` dry-run CLI (no writes) and extend `--verify-reader` to assert no generated `(Part`/`(Illustration)` chapters in the verification book | `App/Cli/*`, `Program.cs` | prints before/after totals for the real library | ✅ done |
| T9 | Docs: wiki (new "epub-chapters" section in dotnet-architecture), lessons, LLM-WIKI, index, AGENTS table; update specs 12 status | docs | consistent | ✅ done |
| T10 | Final verification: build 0 warnings, all tests, `tsc -b`/`vite build`, `--verify-reader`, `--verify-chapters`, `--verify-ui`; record results below | — | all green | ✅ done |

## Risks
- **Builder behavior change touches every new import** → tests first (T3), dry run over 2,223 real EPUBs (T8) before trusting it.
- **Merged document correctness** (head merge, body classes, ids) → single-member identical-output test; real-library spot check via `--verify-reader`.
- **User data remap** (progress, bookmarks) → transactional, version-flagged, failure leaves v1; tests cover primary/member positions.
- **Existing tests** assert the old `(Part N)` behavior in places (extractor tests, specs tests) → update deliberately and list them in the outcome.
- **Do not touch the original `.library-data` DB in tests or dry run** (dry run opens read-only).

## Outcome (2026-11-14)
All tasks done. Verification: `dotnet build` 0 warnings; **252 tests pass** (Dictionaries 15, Enrichment 45, Core 106, App 86; 28 new for spec 12; no existing test had to change). `tsc -b`, `vite build` pass; lint shows only the pre-existing `sdk/index.tsx` error. `--verify-reader` OK on the real library (the verification book was upgraded live: 12 chapters, 8 multi-file). `--verify-ui` exit 0. **`--verify-chapters` over the real 2,223-book library: chapters 88,460 -> 39,564; generated (Part/Illustration) 34,278 -> 0; 0 coverage mismatches; 0 unreadable; 32 s.**
Data safety: a backup of the library DB was taken at `temp/backup/library.db.pre-chapter-grouping` before the first live upgrade; only one book (クークの森の学校) has been upgraded so far, the other 2,222 upgrade when opened.
