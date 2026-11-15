# Spec 23 — Library management: bulk metadata editing

Status: **implemented** (C# `BulkNumbering`, `BookMetadataService.PreviewBulkUpdate/BulkUpdateBooks`, `library.previewBulkUpdate|listBookIds|listSeriesNames`; UI `BulkEditModal.tsx`; 15 + 3 C# tests; `--verify-ui` opens the dialog on a selection and checks the preview without applying anything). Part 2 of library management after [spec 21](21-library-duplicate-detection-spec.md) (duplicates). Feature file: [features/19-library-bulk-edit.feature](features/19-library-bulk-edit.feature).

## 1. Problem

With thousands of books, fixing metadata one book at a time is not workable: putting a 12-volume series together meant typing the series name and every volume number 12 times. The backend already had `library.bulkUpdateBooks`, but the UI could only move books to a folder or add one tag.

## 2. Behaviour

Select books (checkboxes; **select all N** in the selection bar reaches every book the current filters match on **all pages**), press **Edit…**, tick what should change and press **Apply to N**.

| Field | Rule |
| :--- | :--- |
| Series | Ticked + text → set on all; ticked + empty → **clears** the series (the dialog says so); not ticked → untouched. Existing series names are offered as suggestions with their book counts. Names are trimmed |
| Volume numbers | *Leave as they are* (default) · *1, 2, 3 … in title order* (natural: Vol 2 before Vol 10, full-width digits too) · *in file name order* · *in selection order* · *the number written in each title* (第3巻, 3, III, 上/中/下 …, same extraction as the duplicate finder; books whose title has none keep their number) · *remove the volume numbers*. Start and step are editable for the three ordered modes. Ticking *Series* switches the default to title order |
| Author, publisher, imprint, language | Each ticked field is set on all (empty = cleared) |
| Tags | Per tag: add → remove → leave. Adding a tag a book already has is harmless |

* **Preview.** While series or volume numbering is involved, the dialog shows how many series names and volume numbers will change and a table of the first 12 books (now → after). It is computed by the server with **the same code that applies the edit** (`BulkNumbering.Compute`), so preview and result cannot differ. Nothing is written by a preview.
* **One transaction** for the whole selection; the search index is updated per book; selected ids that no longer exist are ignored, a repeated id counts once, an unknown numbering mode is an error and changes nothing.
* The importer **guesses** a series and volume from title patterns ("Saga 10" → series "Saga", volume 10). A bulk edit overwrites those guesses; the preview shows them as the "now" column.

## 3. Contracts

* `BulkBookUpdatePatch` + `NumberingMode` (`selection|title|filename|fromTitle`), `AutoNumberStep`, `ClearSeriesIndex` (the old `AutoNumberVolumes` still means *selection*).
* `library.previewBulkUpdate {bookIds, …patch}` → `{bookCount, seriesChanges, indexChanges, withoutNumber, rows[≤300]: {bookId, title, fileName, currentSeries, currentIndex, newSeries, newIndex}}`.
* `library.listBookIds {folderId?, tagIds?, search?, …}` → `{bookIds, total}` (paging ignored, cap 100,000).
* `library.listSeriesNames` → `[{name, count}]`.

## 4. Fixed on the way

The sidebar tag filter sent `tags`, the server reads `tagIds`: **the tag filter had no effect at all**. The client now sends `tagIds` (test `TheTagFilterTheSidebarSends_…`). "Select all N matching" depends on it.

## 5. Not done

* Editing the title, ISBN or description in bulk (single-book editing exists), find-and-replace in titles, regular-expression renaming.
* Custom fields in the dialog (the patch supports them).
* Undo. The preview is the safety net; for a bad edit, edit again.
* Inferring series for a whole library automatically (a "suggest series from titles" report is the natural next step; the extraction exists).
