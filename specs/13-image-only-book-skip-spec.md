# Specification 13: Skip Image-Only Books at Import (with a warning)

## Status: Approved for implementation
**Scope:** content profiling during EPUB extraction (`ImmersiveReading.Core/Epub`), skip + warning in single import and directory scan (`ImmersiveReading.Library`), scan result/progress contract, scan dialog warning (client), dry-run reporting, tests, wiki.
**Plan:** [13-image-only-book-skip-implementation-plan.md](13-image-only-book-skip-implementation-plan.md) · **Feature file:** [features/11-image-only-book-skip.feature](features/11-image-only-book-skip.feature)
**Decision (user, 2026-11-14):** image-only books (manga, scanned/fixed-layout books) are **not supported** by this reader and belong in a comic format (`.cbz` or similar) which is also not supported yet. They are ignored at import with a warning. The question of supporting them is deferred until it is relevant again.

---

## 1. Problem

Image-only EPUBs (every page is a picture) give the reader nothing to work with: no text to tokenize, no dictionary lookups, no vocabulary, no study pack. They were still imported, producing hundreds of "chapters", huge image documents, and (found in the chapter work, [spec 12](12-chapter-grouping-spec.md)) a worst case of 266 files in one chapter.

## 2. Evidence (analysis of the real library, 2,223 EPUBs, 2026-11-14)

| Finding | Number |
| :--- | :--- |
| Books declaring fixed layout in the OPF (`rendition:layout=pre-paginated`, `fixed-layout`, `original-resolution`, `book-type=comic`) | 42 |
| Books with **≥ 90%** image-only pages (page = contains `img`/`image`/`svg` and ≤ 50 visible characters) | 56 |
| Books in the **50–90%** band | 547, ordinary books: picture books, diagram-heavy non-fiction. **The image-page ratio alone is not a safe signal.** |
| Average visible characters per spine document: books below 60 | 55 |
| ... books between 60 and 100 | **0** (clean gap) |
| ... books between 100 and 200 | 3 |
| ... books above 800 | 2,123 |

All 42 fixed-layout books and 13 more (text-in-image scans without the flag) fall under the rule below. The one book with ≥ 90% image pages but 172 characters per document (a light-novel "illustrated guide", 218 files) correctly stays importable.

## 3. Detection rule

A book is **image-only** when both hold:
1. at least **90%** of its spine documents are image pages, and
2. it has fewer than **80 visible characters per spine document on average** (ruby text excluded).

Thresholds live in one place (`ImageOnlyPolicy`) and sit in the middle of the measured gap (60–100), not on its edge. Declared fixed layout is recorded and reported in the reason text but is **not** required nor sufficient on its own (a fixed-layout book with real text is still readable).

## 4. Requirements

- **FR-1** Every extraction (`EpubMetadataExtractor`) computes a `BookContentProfile`: documents, image documents, total visible characters, declares-fixed-layout, and `IsImageOnly` with a human-readable reason.
- **FR-2** `ImportBookAsync` of an image-only book throws `UnsupportedBookException` whose message names the file, the numbers and the reason ("...image-only books such as comics are not supported yet; use a comic format later"). Nothing is written: no row, no cover cache, no FTS entry.
- **FR-3** `ScanDirectoryAsync` skips image-only files, does **not** count them as failures or imports, and reports them: `SkippedImageOnly` count and one warning per file in `Warnings`. Progress carries a running `SkippedFiles`.
- **FR-4** Skipping never modifies the library: an image-only EPUB that is already in the library (imported earlier) is left exactly as it is and is **not** deduplicated or updated either.
- **FR-5** The scan dialog shows a warning panel when `SkippedImageOnly > 0`: count, explanation, and an expandable list of file names with their reason.
- **FR-6** `--verify-chapters` (dry run) additionally reports how many books **already in the library** the rule would flag, with titles, so the user can decide what to do with them later. No data is changed by this spec.
- **FR-7** Detection never makes a normal book unimportable: any failure while profiling (unreadable spine file, odd encoding) counts that document as text-bearing / not image-only, never as an error.

### Non-functional
- **NFR-1** Offline, deterministic, no new dependencies. Cost: one extra read of each spine document during extraction (measured equivalent in Python: 2,223 books in 18 s).
- **NFR-2** Existing import behavior for text books is unchanged (existing tests untouched).

## 5. Design

### 5.1 Profiling (`BookContentAnalyzer`, Core)
For each spine document: read the entry as text, decide `hasImage` (`<img`, `<image`, `<svg`) and `visibleChars` (remove `script`/`style`/`rt`/`rp` blocks, strip tags, remove whitespace, count). Regex based on purpose (the same approach that produced the evidence numbers, fast, no DOM). Fixed layout is read from the OPF text. Result is the `BookContentProfile` record attached to `ExtractedEpubPackage` (new optional trailing parameter, so existing constructors keep working).

### 5.2 Import behavior
```
ImportBookAsync:  extract -> if profile.IsImageOnly -> throw UnsupportedBookException   (before any write)
ScanDirectoryAsync (parallel extract stage): if profile.IsImageOnly -> record skip + warning, do not cache cover, do not enqueue for upsert
```
`ScanBatchResult` gains optional `SkippedImageOnly` and `Warnings`; `ScanBatchProgress` gains optional `SkippedFiles`. All additions are trailing optional parameters with defaults, so the JSON contract stays backward compatible (result keys are read by name by the client).

### 5.3 Client
`ScanFolderModal` reads `skippedImageOnly` / `warnings` from the result and renders an amber panel under the existing failed-files line. The progress line shows skipped files while scanning.

### 5.4 Out of scope (deliberately)
- Importing or converting comics (`.cbz`, `.cbr`, PDF); a configuration switch to force import; removing the 55 image-only books already in the library (reported by the dry run only); per-page image viewing. Revisit when comic support is on the roadmap; the profile record and its thresholds are the extension point.

## 5b. As built
- `UnsupportedBookException.BuildMessage` is shared by the single-import exception and the scan warning, so both read identically.
- Percentages in messages use the invariant culture (a locale put a space in "99 %").
- Verified on the real library: the C# detector flags exactly **55** books, identical to the independent Python analysis (all 42 fixed-layout-declared books plus 13 text-in-image scans). The dry run with the extra profiling pass takes 9 s for 2,223 books.
- Not done (as specified): comic formats, a force-import switch, removal of the 55 existing image-only books.

## 6. Edge cases
- EPUB with 1–3 documents, all images: image-only (nothing to read) and skipped.
- Spine document missing from the archive: not counted as a document.
- Mixed book (long text chapters plus many full-page illustrations): ratio stays well below 90% or text is large; imported.
- Declared fixed layout but 5,000 characters per page: imported.
- The same image-only file scanned repeatedly: skipped each time, with a warning each time.

## 7. Test plan
**Core**: analyzer on synthetic EPUBs: manga-like (all images), light-novel (text + illustrations), picture book (mixed), fixed-layout flagged with text, tiny single image page, ruby-only text, missing spine entry, undecodable document; threshold boundary cases (89% / 90%, 79 / 80 chars).
**Library**: `ImportBookAsync` throws `UnsupportedBookException` and writes nothing; scan skips, counts, warns, leaves an already-imported image-only book untouched; text books unchanged; progress reports skipped.
**Dry run**: `--verify-chapters` prints the would-be-flagged existing books (expected ≈55 on the real library, matching the Python analysis).
**Client**: `tsc -b`, `vite build`; `--verify-ui` self-test unchanged and passing.
