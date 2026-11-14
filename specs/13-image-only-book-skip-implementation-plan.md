# Implementation Plan 13: Skip Image-Only Books at Import

Spec: [13-image-only-book-skip-spec.md](13-image-only-book-skip-spec.md). Work in order; tests are written with the code they cover.

| # | Task | Files | Done when | Status |
| :-- | :--- | :--- | :--- | :--- |
| T1 | `BookContentProfile` + `ImageOnlyPolicy` (thresholds, reason text) + `UnsupportedBookException` | `Core/Epub/BookContentProfile.cs`, `Core/Epub/UnsupportedBookException.cs` | compiles | ✅ done |
| T2 | `BookContentAnalyzer` (regex profiling, fixed-layout detection, failure-tolerant) wired into `ExtractFromStream`; optional `Content` field on `ExtractedEpubPackage` | `Core/Epub/BookContentAnalyzer.cs`, `EpubMetadataExtractor.cs` | analyzer tests pass; existing tests green | ✅ done |
| T3 | Analyzer tests incl. threshold boundaries and edge cases | `tests/Core.Tests/Epub/BookContentProfileTests.cs` | pass; fail against a stub that never flags | ✅ done |
| T4 | `ImportBookAsync` throws before any write; `ScanDirectoryAsync` skips, counts, warns; `ScanBatchResult`/`ScanBatchProgress` optional fields | `Library/Books/BookImportService.cs`, `Core/Library/ScanModels.cs` | import/scan tests pass | ✅ done |
| T5 | Import/scan tests (skip, no writes, existing book untouched, text books unchanged, progress) | `tests/Core.Tests/Books/ImageOnlyImportTests.cs` | pass | ✅ done |
| T6 | Scan dialog warning panel + skipped in progress; types | `client/src/components/library/ScanFolderModal.tsx` | `tsc -b`, `vite build` | ✅ done |
| T7 | `--verify-chapters` reports existing books the rule would flag | `App/Cli/ChapterVerification.cs` | real library prints ≈55, titles listed | ✅ done |
| T8 | Docs: spec statuses, wiki (architecture section + lessons incl. the "ratio alone is misleading" finding), LLM-WIKI, index, AGENTS | docs | consistent | ✅ done |
| T9 | Final verification: build 0 warnings, all tests, `tsc -b`/`vite build`, `--verify-chapters`, `--verify-reader`, `--verify-ui`; record results below | — | all green | ✅ done |

## Risks
- **False positives hide a real book** → rule requires both conditions; thresholds sit mid-gap; dry run lists what the real library would flag so it can be reviewed before trusting it.
- **Extraction cost** on 24k-book scans → one extra regex pass per spine document; measured acceptable in the analysis; re-check scan time on the real library.
- **Existing tests import tiny EPUBs** → they contain no images, so ratio is 0; run the whole suite to confirm (NFR-2).
- **Contract drift** → result/progress additions are optional trailing record parameters.

## Outcome (2026-11-14)
All tasks done. Verification: `dotnet build` 0 warnings; **274 tests pass** (Dictionaries 15, Enrichment 45, Core 128, App 86; 22 new for spec 13, no existing test changed); each new test class was mutation-checked (stubbing detection / skipping makes them fail). `tsc -b` and `vite build` pass; lint shows only pre-existing findings. `--verify-reader` OK, `--verify-ui` exit 0, `--verify-chapters` OK and reports **55** existing image-only books (matches the independent Python analysis).
No library data was changed by this work.
