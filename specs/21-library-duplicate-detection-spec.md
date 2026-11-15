# Spec 21 — Library duplicate detection (first library-management feature)

Status: **implemented** (detector, service, RPC, dialog; tuned against a real 2,188-book library, see section 10). Feature file: [features/17-library-duplicates.feature](features/17-library-duplicates.feature).

## 1. Context and goal

The library is expected to hold **10,000+ books**, so tidy management is a primary product concern. Byte-identical files are already collapsed at import (the book id is derived from the file's SHA-256). What remains are **different files of the same book**: a re-downloaded edition, a "digital/colour/new edition", a repack, the same volume stored under two folders or a differently spelled title.

Goal: find such **likely duplicates** across the whole library quickly, show them side by side, and let the **user decide** which one to keep. Nothing is ever removed automatically.

## 2. Principles

1. **The user decides.** The tool proposes groups and a recommended keeper; removal needs an explicit click per group.
2. **A wrong "duplicate" costs more than a missed one.** Different volumes of one series must never be grouped (volume number is a hard separator), and so must different languages.
3. **Reversible by default.** "Remove" deletes the library entry only. The EPUB file on disk is **not touched** (a later spec adds an optional "move file to the Recycle Bin"). The removed file is remembered so a re-scan does not import it again, and the user can allow it again.
4. **Scale.** Detection must not compare every pair: candidate pairs come from blocking keys. Target: 10,000 books in well under a second, 50,000 in a few seconds (test-enforced with a generous bound).
5. **No loss of user work.** When a duplicate is removed, its tags, custom fields (e.g. the vocabulary deck CSV) and missing metadata move to the kept book.

## 3. Matching

Per book the detector derives (never stored): normalized **base title**, **volume** (number or null), normalized **series**, **authors** (set of normalized names), normalized **ISBN-13**, **identifier**, **language**, length (characters) and chapter count.

### 3.1 Normalization

* **Titles**: Unicode NFKC (full-width digits and letters become ASCII), case-folded, katakana folded to hiragana, spaces and punctuation removed, in this order:
  1. **Edition noise** removed (電子書籍版, 電子版, デジタル版, カラー版, 新装版, 改訂版, 特装版, 限定版, 文庫版, eBook, Kindle Edition ...).
  2. **Publisher imprint** in brackets removed: `(電撃文庫)`, `(MF文庫J)`, `(GA文庫)`, `(Kadokawa Books)`, `(ドラゴンノベルス)` (any bracket containing 文庫, ノベルズ, ブックス, ノベル, コミックス, books, bunko ...).
  3. **Edition remarks at the end** removed: `【電子特典付き】`, `【電子書籍限定書き下ろしSS付き】` (they hide the volume number in front of them).
  4. **Volume** extracted and removed (first match wins): `14.5` (decimal), `その2`, `第3巻`, `3巻`, `Vol.3`, `v3`, `#3`, `(3)`, a number at the very end with or without a space (`Title 3`, `Title3`, `Title003`), a roman numeral at the end (`Title VI`), a kanji numeral closing a subtitle or standing alone (`～十一`, `～弐`, 壱弐参 included). `上/中/下` (`<上>`, `(下)`, ` 上巻`, ` 中`) is a *part*: alone it means 1/2/3, combined with a number it is `number + part/10` (`II <上>` = 2.1).
  5. The remaining **number tokens** (digit runs, roman numerals, `#EX`) are kept as a signature: titles whose signatures differ are different books, whatever their text similarity.
* **Authors**: NFKC, split on `、 , ， / ／ ; ； & ＆ · |` and ` と ` / ` and ` between spaces, parentheticals and role suffixes removed (`著`, `作`, `原作`, `イラスト`, `画`, `訳`, `編`, `監修`), spaces and punctuation removed. Two author sets are **compatible** when they share a name, or one name contains the other (surname only, at least 2 characters), or either set is empty (unknown). Order does not matter.
* **ISBN**: digits only, ISBN-10 converted to ISBN-13.
* **Volume evidence** has two strengths. **Strong** is the volume written in the title. **Weak** is the volume from the file name (parsed like a title) or the series index (only when whole; real libraries store `14.5` under index 14).

### 3.2 Rules (pair score 0-100)

**Hard separators** (the pair is never a duplicate): different non-empty languages; volume disagreement:

| Situation | Result |
|---|---|
| both titles state a volume | must be equal |
| one title states a volume | the other must agree through its weak volume, or a number inside its title equal to it, or the stated volume is 1 ("Title" and "Title 1"); otherwise different books |
| neither states a volume | weak volumes must be equal when both exist; one side without any volume evidence stays compatible |

| Rule | Score | Needs |
|---|---|---|
| Same ISBN | 100 | |
| Same publication identifier (OPF `dc:identifier`) | 95 | identifier used by at most 5 books (some tools stamp every volume alike), equal number signature and title similarity (Dice) of at least 0.85 |
| Same base title, same volume (or both without), authors share a name | 90 | |
| Same base title, volume known on one side only, authors share a name | 80 | |
| Same series and same series index, similar base title (Dice 0.6 or more), equal number signature, authors not in conflict | 80 | |
| Very similar base title (Dice 0.88 or more), equal number signature, same volume state, authors share a name | 70 | |
| Same base title, same volume, an author unknown on one side | 60 | |

Authors in conflict (both known, nothing shared) block the title rules. Length adjusts the score when both lengths are computed: within 3 % gives **+10** ("same length"); more than 25 % apart gives **-15** ("very different length: abridged or another edition?"). The reasons are kept and shown. Note that quick scans do not compute lengths, so the length signal is often absent.

Confidence levels: **certain** 90 and above, **likely** 70 to 89, **possible** 50 to 69. The default view shows likely and above; the user can lower it.

### 3.3 Groups

Pairs with a score above the threshold are joined strongest first (union-find). A join is refused if the merged group would contain **two different known volumes**, which stops chains like `A~B~C` where A and C are different volumes. Each group gets a stable id (hash of its sorted book ids) and a recommended keeper.

### 3.4 Blocking (scale)

Candidate pairs only come from shared buckets: ISBN, identifier, (base title, volume), (series, index), and (author name, volume) for fuzzy titles. Oversized buckets (> 200 books) are skipped for the fuzzy rule only. Complexity is roughly linear in the number of books plus the sum of squared small bucket sizes.

## 4. Recommended keeper

Highest total of: reading progress (+40, the user's work), study pack (+15), vocabulary deck CSV (+15), metadata completeness (series, authors, ISBN, description, cover: +3 each), computed length larger (+5), bigger file (+2), earlier added date (tie-break). The reasons are shown ("has 42 % reading progress", "has study pack"). The user can pick any member.

## 5. Resolving a group

`resolve(keepBookId, removeBookIds, mergeData = true)` in one transaction:

1. If `mergeData`: copy tags (union), copy custom field values the kept book does not have, fill **empty** `series_name`, `series_index`, `authors`, `isbn`, `publisher`, `description` from removed books (best candidate first). Reading progress is **not** transferred (chapter structure differs between editions), which is why progress is weighted in the recommendation and shown in the comparison.
2. Record each removed book in `removed_books` (`book_id`, `epub_sha256`, title, original path, kept book id, time).
3. Delete the library rows (existing delete path: FTS entry, custom fields, cascade of chapters, progress, tags).

Safety checks: the kept id must exist, must not be in the removed list, and removed ids must exist; otherwise nothing changes.

**Mass clear:** `resolveMany(groups, mergeData)` applies `resolve` to many groups in one call, **each group in its own transaction**: a group that no longer fits (a book vanished since the search, a book reused by two groups) is reported in `failed` and the others still go through. The UI button "Mass clear keeping recommended" acts on the groups currently listed (so the confidence filter and "Skip" decide the scope), keeps each group's recommended book **except where the user picked another copy by hand** (that choice wins), and always asks first with a summary (groups by confidence, books to remove, hand-picked groups, a warning when "possible" matches are included). Measured on a real 2,125-book library: 412 groups, 419 books removed, 0 failures, 0.4 s.

**Not duplicates:** `dismiss(bookIds)` stores every pair of the group in `duplicate_dismissals`; those pairs are never proposed again. Deleting a book removes its dismissals.

**Re-scan:** a scan skips files whose book id is in `removed_books` and reports `SkippedRemovedDuplicates` and a warning per file. Importing a single file explicitly is the user's intent and clears its removal record. `forgetRemoved(bookId)` allows the next scan to import it again.

## 6. RPC (`library.*`)

| Method | Payload | Result |
|---|---|---|
| `findDuplicates` | `{minScore?}` | `{totalBooks, elapsedMs, groups:[{groupId, confidence, score, recommendedKeepId, members:[…], pairs:[{a,b,score,reasons}]}]}` |
| `resolveDuplicates` | `{keepBookId, removeBookIds, mergeData?}` | `{removed, merged:{tags, customFields, fields}}` |
| `resolveDuplicatesBatch` | `{groups:[{keepBookId, removeBookIds}], mergeData?}` | `{groupsResolved, removed, tagsMerged, customFieldsMerged, fieldsFilled, resolvedKeepBookIds, failed:[{keepBookId, error}]}` |
| `dismissDuplicates` | `{bookIds}` | `{pairs}` |
| `listRemovedBooks` | `{}` | removed entries |
| `forgetRemovedBook` | `{bookId}` | `{ok}` |

## 7. UI

Sidebar → **Find duplicates** opens a modal. Summary line (books scanned, groups, time), confidence filter, groups listed 25 at a time. A group is a comparison table (cover, title, volume/series, authors, length, chapters, file size, file name and folder, progress, flags) with a **Keep** radio defaulting to the recommendation, the reasons for the match, and the buttons **Keep selected, remove others**, **Not duplicates**, **Skip**. A toolbar button **Mass clear keeping recommended (N)** clears all listed groups at once after a confirmation. After an action the group disappears and the next one moves up. A "Removed duplicates" tab lists removed books with **Allow import again**.

## 8. Tests

Normalizer vectors (titles, volumes, authors, ISBN); detector scenarios from the feature file (editions, volumes never grouped, language, chain guard, dismissal); a 20,000-book synthetic library for time and no false groups among 10,000 distinct books; resolve (merge of tags/fields, rollback on invalid input, removal record, scan skip, explicit import clears); RPC contract; `--verify-ui`.

## 9. Library management roadmap (not part of this spec)

After duplicates: bulk metadata clean-up (series and volume inference from titles, author spelling merges), missing-metadata and missing-cover reports, orphaned file / moved file detection, saved views, optional move-to-Recycle-Bin of removed duplicates.

## 10. Findings from a real library (what shaped the rules)

Tuning against a copy of a real 2,188-book library (about 20 % of the books turned out to be duplicates; 443 groups at "likely and above") showed what synthetic tests do not:

* Volume numbers appear **inside** titles (`賢者２ ～サブタイトル～`), **attached** to the text (`教典2`, `件５`), as roman numerals (`… VI`, `ホライゾンII<上>`), as kanji numerals (`～十一`, `～弐`), as `上/中/下`, as decimals (`14.5`), and as `#EX`. Treating titles that differ only by such a number as "similar" merged whole series into one group; the number signature and the volume rules exist because of this.
* **Series index is unreliable** (`14.5` stored as 14, the index missing, or from a differently numbered edition), hence "weak" evidence.
* **Identifiers** are stamped identically on every volume by some publishers and tools, so an identifier alone is never proof.
* Publisher imprints in the title (`(富士見ファンタジア文庫)`) and reading-based file names are the commonest reason two copies differ.
* The same book often exists 2 to 4 times (a plain file, a `[author] title.epub` file, a calibre export, a reading-named file). Groups of 3 and 4 are real.
* With the final rules a scan of 2,188 books takes under 150 ms, and the only same-title/same-volume pair left out of groups was an author list in a different order (fixed).
