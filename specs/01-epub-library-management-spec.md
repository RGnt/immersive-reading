# 01 — EPUB Library Management Specification

> **Module:** `ImmersiveReading.Library` & `ImmersiveReading.Core.Library`  
> **Storage:** SQLite (`library.db`) + Managed/Linked Book Storage + `enrichement-extractor` Study Pack Bridge

---

## 1. Overview & Responsibilities

The **EPUB Library Management** subsystem manages the user's collection of EPUB books, supplementary vocabulary decks (Jiten CSVs), per-book configurations (`study.config.json`), reading progress, bookmarks, user vocabulary state (`New`, `Learning`, `Known`, `Ignored`), and links to generated study packs.

### Key Design Goals
1. **100% Identity Compatibility with `enrichement-extractor`**:
   - Every EPUB's `BookId` is computed identically to [`enrichement-extractor/immersive/build.py#L96`](../enrichement-extractor/immersive/build.py#L96) and [`epub.py#L11-L12`](../enrichement-extractor/immersive/epub.py#L11-L12):
     ```csharp
     public static (string BookId, string FullSha256Hex) ComputeFromBytes(ReadOnlySpan<byte> epubBytes)
     {
         Span<byte> fileHash = stackalloc byte[32];
         SHA256.HashData(epubBytes, fileHash);
         string fullHex = Convert.ToHexStringLower(fileHash);
         return (ComputeUidFromHex("book", fullHex), fullHex); // "book-" + SHA256(utf8(fullHex))[..16]
     }
     ```
2. **Non-Destructive Book Storage**:
   - Users can either import EPUBs into the workspace's `books/<book-slug>/` directory (managed mode, immediately compatible with `enrichement-extractor`) or link EPUBs in place from any disk path.
   - Original `.epub` files are **never modified**.
3. **Fast Offline Catalog**:
   - All metadata, chapter TOC trees, reading positions, bookmarks, and cross-book vocabulary knowledge states live in a single local SQLite database (`library.db`) using WAL (`journal_mode=WAL`) for concurrent read/write performance.

---

## 2. Book Folder Convention & Ingestion Flow

When importing an EPUB (and optional Jiten `.csv` deck) in **Managed Mode**:

```text
books/<sanitized-book-folder>/
├── <original-name>.epub             # Unmodified source EPUB
├── <optional-deck>.csv              # Optional Jiten vocabulary CSV
├── study.config.json                # Optional chapter filters, aliases, context_glosses
└── .enrichment/
    └── deck-provenance.json         # Managed by enrichement-extractor when updating CSV sort keys
```

### Step-by-Step Ingestion Pipeline (`LibraryManager.ImportBookAsync`)
1. **Hash & Deduplicate**:
   - Stream the `.epub` file to compute its full `Sha256` (64 hex chars) and `BookId` (`book-<16 hex>`).
   - If `BookId` already exists in `books` table, update file path / attach new CSV decks and return the existing `BookRecord`.
2. **Parse EPUB Package via `EpubArchiveReader` (ZipArchive + AngleSharp)**:
   - Validate `META-INF/container.xml` and locate the root `.opf` file.
   - Parse `<metadata>` using AngleSharp `XmlParser`:
     - `Title` (`dc:title`), `Creator` (`dc:creator`), `Language` (`dc:language`, normalized to BCP-47 primary tag e.g. `"ja"`), `Publisher` (`dc:publisher`), `PublishedDate` (`dc:date`), `Identifier` (`dc:identifier`).
   - Parse `<manifest>` (`id -> href, media-type, properties`) and linear `<spine>` (`itemref idref`).
   - Parse Navigation TOC:
     - Prefer EPUB 3 `<nav epub:type="toc">` (`properties="nav"`).
     - Fall back to EPUB 2 NCX (`application/x-dtbncx+xml` `<navPoint>`).
     - Apply front/back matter detection matching [`enrichement-extractor/immersive/epub.py#L51-L63`](../enrichement-extractor/immersive/epub.py#L51-L63) (`もくじ`, `目次`, `奥付`, `表紙`, `扉`, `Contents`, `Table of Contents`, `Cover`, `Copyright`, plus `study.config.json` `exclude_titles` / `include_titles` / `end_before_href`).
   - Extract Cover Image:
     - Locate manifest item with `properties="cover-image"`, or `<meta name="cover" content="..."/>`, or first spine image, and cache a thumbnail at `library-cache/covers/{bookId}.webp` (or `.png`/`.jpg`).
3. **Compute Quick Character & Chapter Stats**:
   - Count chapters and total normalized character count across spine chapters so the library shelf can show book length and percentage completion accurately.
4. **Discover Existing Study Pack Outputs**:
   - Check `enrichement-extractor/output/` (via `.publication/active.json` manifest or `output/data/{bookId}/reader.json`) and link available study artifacts (HTML companion, PDFs, `vocabulary-sets.json`) to the book record.

---

## 3. SQLite Database Schema (`library.db` — Engineered for 24,000+ Books)

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

-- 1. Hierarchical Virtual Folders / Categories / Series Collections
-- Supports arbitrary tree nesting, e.g.:
--   "Japanese" (collection)
--     ├── "Shounen" (category)
--     └── "Seinen" (category)
--           └── "Monster" (series) -> Volumes sorted by series_index / custom_sort_order
CREATE TABLE IF NOT EXISTS library_folders (
    folder_id           TEXT PRIMARY KEY,          -- UUID or slug
    parent_folder_id    TEXT REFERENCES library_folders(folder_id) ON DELETE CASCADE,
    name                TEXT NOT NULL,             -- e.g. 'Japanese', 'Seinen', '三日間の幸福'
    folder_kind         TEXT NOT NULL DEFAULT 'folder', -- 'collection' | 'category' | 'series' | 'folder'
    description         TEXT NOT NULL DEFAULT '',
    icon                TEXT NOT NULL DEFAULT 'folder',
    color_hex           TEXT,
    sort_order          INTEGER NOT NULL DEFAULT 0,-- Sibling order in tree sidebar
    default_book_sort   TEXT NOT NULL DEFAULT 'series_or_title', -- 'series_or_title' | 'custom' | 'last_read' | 'added'
    created_at_utc      TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_folders_parent ON library_folders(parent_folder_id, sort_order);

-- 2. Books Catalog (Full Editable Bibliographic Metadata + Original OPF Snapshot)
CREATE TABLE IF NOT EXISTS books (
    book_id             TEXT PRIMARY KEY,          -- 'book-[0-9a-f]{16}'
    epub_sha256         TEXT NOT NULL UNIQUE,      -- Full 64-char hex SHA-256
    folder_id           TEXT REFERENCES library_folders(folder_id) ON DELETE SET NULL, -- Primary virtual folder
    title               TEXT NOT NULL,             -- User-editable display title
    sort_title          TEXT NOT NULL,             -- User-editable kana/sort title
    series_name         TEXT NOT NULL DEFAULT '',  -- e.g. '進撃の巨人' or '本好きの下剋上'
    series_index        REAL,                      -- Volume number e.g. 1.0, 2.0, 2.5 (NULL if standalone)
    custom_sort_order   INTEGER NOT NULL DEFAULT 0,-- Manual sort position within folder
    authors             TEXT NOT NULL DEFAULT '',  -- User-editable author(s), ' · '-joined
    author_sort         TEXT NOT NULL DEFAULT '',  -- Kana/sort author name
    isbn                TEXT NOT NULL DEFAULT '',  -- Extracted & editable ISBN-13 / ISBN-10
    publisher           TEXT NOT NULL DEFAULT '',  -- e.g. '主婦と生活社', 'KADOKAWA'
    imprint             TEXT NOT NULL DEFAULT '',  -- e.g. 'PASH! ブックス', '電撃文庫'
    published_date      TEXT NOT NULL DEFAULT '',  -- ISO date or year-month
    language            TEXT NOT NULL DEFAULT 'ja',-- Primary language code ('ja', 'zh', 'ko', etc.)
    description         TEXT NOT NULL DEFAULT '',  -- Book synopsis / blurb (editable)
    opf_identifier      TEXT NOT NULL DEFAULT '',  -- Raw dc:identifier from OPF
    opf_original_json   TEXT NOT NULL DEFAULT '{}',-- Immutable snapshot of raw OPF metadata for diff/reset
    is_metadata_edited  INTEGER NOT NULL DEFAULT 0,-- 1 if user customized bibliographic fields
    folder_path         TEXT NOT NULL,             -- Absolute disk path to book directory
    epub_file_name      TEXT NOT NULL,             -- Filename inside folder_path
    file_size_bytes     INTEGER NOT NULL DEFAULT 0,
    cover_cache_path    TEXT,                      -- Cached cover image path
    chapter_count       INTEGER NOT NULL DEFAULT 0,
    total_char_count    INTEGER NOT NULL DEFAULT 0,
    stats_computed      INTEGER NOT NULL DEFAULT 0,-- 0 = fast OPF scan only, 1 = deep chapter char count done
    writing_mode        TEXT NOT NULL DEFAULT 'auto', -- 'auto', 'vertical-rl', 'horizontal-tb'
    added_at_utc        TEXT NOT NULL,
    updated_at_utc      TEXT NOT NULL,
    last_opened_at_utc  TEXT,
    has_study_pack      INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_books_folder_sort ON books(folder_id, series_name, series_index, custom_sort_order, sort_title);
CREATE INDEX IF NOT EXISTS idx_books_series ON books(series_name, series_index);
CREATE INDEX IF NOT EXISTS idx_books_author ON books(author_sort, authors);
CREATE INDEX IF NOT EXISTS idx_books_isbn ON books(isbn) WHERE isbn != '';
CREATE INDEX IF NOT EXISTS idx_books_added ON books(added_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_books_last_opened ON books(last_opened_at_utc DESC);

-- 3. Full-Text Search Index (FTS5) for Instant < 5ms Search Across 24,000+ Books
CREATE VIRTUAL TABLE IF NOT EXISTS books_fts USING fts5(
    book_id UNINDEXED,
    title,
    sort_title,
    series_name,
    authors,
    author_sort,
    publisher,
    imprint,
    isbn,
    description,
    custom_fields_text,
    tokenize = 'unicode61'
);

-- 4. Multi-Level Custom Field Definitions & Values (Folder / Series / Book Levels)
-- Allows attaching custom fields like "jiten.moe Link", "Media Deck URL", "Difficulty",
-- "BookWalker URL", etc. at ANY level (Collection folder, Category, Series folder, or Book).
CREATE TABLE IF NOT EXISTS custom_field_definitions (
    field_key           TEXT PRIMARY KEY,          -- e.g. 'jiten_moe_url', 'vocab_deck_csv', 'jiten_difficulty'
    display_name        TEXT NOT NULL,             -- e.g. 'jiten.moe Media Link'
    field_type          TEXT NOT NULL,             -- 'url' | 'text' | 'number' | 'rating' | 'boolean' | 'select' | 'file_path'
    target_scope        TEXT NOT NULL DEFAULT 'any', -- 'any' | 'folder' | 'book'
    inherit_to_children INTEGER NOT NULL DEFAULT 1,-- 1 = Folder/Series value cascades to child subfolders & books!
    options_json        TEXT NOT NULL DEFAULT '[]',-- Allowed choices when field_type = 'select'
    show_on_card        INTEGER NOT NULL DEFAULT 1,-- Show badge/link on book/series card & table column
    sort_order          INTEGER NOT NULL DEFAULT 0,
    created_at_utc      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS custom_field_values (
    entity_type         TEXT NOT NULL,             -- 'folder' | 'book'
    entity_id           TEXT NOT NULL,             -- folder_id or book_id
    field_key           TEXT NOT NULL REFERENCES custom_field_definitions(field_key) ON DELETE CASCADE,
    value_text          TEXT NOT NULL DEFAULT '',  -- URL, string, JSON, or file path
    value_number        REAL,                      -- Populated for numeric/rating fields for fast SQL sorting/filtering!
    updated_at_utc      TEXT NOT NULL,
    PRIMARY KEY (entity_type, entity_id, field_key)
);
CREATE INDEX IF NOT EXISTS idx_custom_values_lookup ON custom_field_values(field_key, value_text);
CREATE INDEX IF NOT EXISTS idx_custom_values_num ON custom_field_values(field_key, value_number);

-- 5. Manual User Tags & Assignments (for both Books and Folders/Series)
CREATE TABLE IF NOT EXISTS tags (
    tag_id              TEXT PRIMARY KEY,          -- UUID or normalized slug
    name                TEXT NOT NULL UNIQUE COLLATE NOCASE, -- e.g. 'Mystery', 'N2-Target', 'Favorite'
    color_hex           TEXT NOT NULL DEFAULT '#64748b',
    sort_order          INTEGER NOT NULL DEFAULT 0,
    created_at_utc      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS book_tags (
    book_id             TEXT NOT NULL REFERENCES books(book_id) ON DELETE CASCADE,
    tag_id              TEXT NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
    assigned_at_utc     TEXT NOT NULL,
    PRIMARY KEY (book_id, tag_id)
);
CREATE INDEX IF NOT EXISTS idx_book_tags_tag ON book_tags(tag_id, book_id);

CREATE TABLE IF NOT EXISTS folder_tags (
    folder_id           TEXT NOT NULL REFERENCES library_folders(folder_id) ON DELETE CASCADE,
    tag_id              TEXT NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
    assigned_at_utc     TEXT NOT NULL,
    PRIMARY KEY (folder_id, tag_id)
);

-- 6. Book Chapters (Reading Order Spine / Navigation Sections)
CREATE TABLE IF NOT EXISTS book_chapters (
    book_id             TEXT NOT NULL REFERENCES books(book_id) ON DELETE CASCADE,
    chapter_index       INTEGER NOT NULL,          -- 0-based index
    chapter_id          TEXT NOT NULL,             -- 'chapter-01', 'chapter-02', ...
    title               TEXT NOT NULL,
    spine_href          TEXT NOT NULL,             -- Relative POSIX path inside EPUB (e.g. 'OEBPS/Text/p01.xhtml')
    fragment_id         TEXT NOT NULL DEFAULT '',  -- Optional anchor fragment
    end_spine_href      TEXT,                      -- Exclusive boundary href for multi-anchor files
    end_fragment_id     TEXT,
    char_count          INTEGER NOT NULL DEFAULT 0,
    cumulative_chars    INTEGER NOT NULL DEFAULT 0,-- Sum of char_count prior to this chapter
    is_excluded         INTEGER NOT NULL DEFAULT 0,-- Excluded by front/back matter or study.config.json
    PRIMARY KEY (book_id, chapter_index)
);

-- 7. Reading Progress (One row per book)
CREATE TABLE IF NOT EXISTS reading_progress (
    book_id                 TEXT PRIMARY KEY REFERENCES books(book_id) ON DELETE CASCADE,
    current_chapter_index   INTEGER NOT NULL DEFAULT 0,
    current_spine_href      TEXT NOT NULL,
    locator_token_id        INTEGER,               -- Enriched token ID at top-left/top-right of viewport
    locator_char_offset     INTEGER NOT NULL DEFAULT 0, -- Chapter-relative normalized char offset
    scroll_or_page_ratio    REAL NOT NULL DEFAULT 0.0,  -- 0.0 to 1.0 fallback within chapter
    overall_completion      REAL NOT NULL DEFAULT 0.0,  -- 0.0 to 1.0 across entire book
    total_reading_seconds   INTEGER NOT NULL DEFAULT 0,
    updated_at_utc          TEXT NOT NULL
);

-- 8. Bookmarks & Highlights
CREATE TABLE IF NOT EXISTS bookmarks (
    bookmark_id         TEXT PRIMARY KEY,          -- UUID
    book_id             TEXT NOT NULL REFERENCES books(book_id) ON DELETE CASCADE,
    chapter_index       INTEGER NOT NULL,
    spine_href          TEXT NOT NULL,
    start_token_id      INTEGER,
    end_token_id        INTEGER,
    start_char_offset   INTEGER NOT NULL,
    end_char_offset     INTEGER NOT NULL,
    selected_text       TEXT NOT NULL,
    user_note           TEXT NOT NULL DEFAULT '',
    highlight_color     TEXT,                      -- NULL for simple bookmark, hex/class for highlight
    created_at_utc      TEXT NOT NULL
);

-- 9. Cross-Book User Vocabulary Knowledge State
CREATE TABLE IF NOT EXISTS vocabulary_state (
    language            TEXT NOT NULL,             -- 'ja', etc.
    lemma               TEXT NOT NULL,             -- Dictionary spelling / word (e.g. '食べる')
    reading             TEXT NOT NULL,             -- Hiragana-normalized reading (e.g. 'たべる')
    status              INTEGER NOT NULL,          -- 0=New/Unseen, 1=Learning, 2=Known, 3=Ignored
    custom_gloss        TEXT,                      -- Optional user-reviewed context gloss
    times_looked_up     INTEGER NOT NULL DEFAULT 0,
    first_seen_book_id  TEXT,
    updated_at_utc      TEXT NOT NULL,
    PRIMARY KEY (language, lemma, reading)
);
CREATE INDEX IF NOT EXISTS idx_vocab_state_lemma ON vocabulary_state(language, lemma);
```

---

## 3.1 Multi-Level Custom Fields (`jiten.moe` Links, Decks, Ratings) & Inheritance

Because different metadata belongs at different levels of the hierarchy (for example, a **`jiten.moe` parent media page link** belongs to a **Series folder**, while a **volume-specific `jiten.moe` sub-deck URL** or **ISBN** belongs to an individual **Book**), `custom_field_definitions` and `custom_field_values` support arbitrary levels:

1. **Out-of-the-Box Pre-Seeded Custom Fields**:
   - `jiten_moe_url` (`field_type = "url"`, `inherit_to_children = 1`): Direct clickable link to the book or series page on `https://jiten.moe/...` (for vocabulary decks & media stats).
   - `vocab_deck_csv` (`field_type = "file_path"`, `inherit_to_children = 1`): Path to an associated Jiten `.csv` vocabulary deck on disk.
   - `jiten_difficulty` (`field_type = "number"`, `inherit_to_children = 1`): Numeric difficulty rating for sorting/filtering.
   - Users can define unlimited additional custom fields (`text`, `url`, `number`, `rating`, `boolean`, `select`, `file_path`) directly from the UI.
2. **Hierarchical Custom Field Inheritance**:
   - When `inherit_to_children = 1` on a custom field definition, setting `jiten_moe_url` on a Series folder (e.g. `Japanese -> Seinen -> Monster`) automatically provides that value as an `InheritedValue` on all volumes inside `Monster`, while still allowing any individual volume to override it with its own specific volume deck URL.

---

## 3.2 Editable Bibliographic Metadata & 24,000-Book Scale Curation

Real-world Japanese EPUB collections (~24,000 books) frequently have inconsistent OPF metadata (missing `dc:creator`, author embedded in `dc:title` like `[Author] Title`, missing series tags, or corrupted publisher/ISBN strings):

1. **Smart Extraction + Filename Heuristic Fallback**:
   - Extracts `dc:title`, `opf:file-as` (`sort_title`), `dc:creator`, `dc:publisher`, `dc:date`, `dc:description`, EPUB3 `belongs-to-collection` / Calibre `calibre:series` + `calibre:series_index`, and **ISBN-10 / ISBN-13** (scanning all `dc:identifier` elements and normalizing hyphens).
   - When OPF fields are missing or contain bracketed filename patterns (e.g., `[かさい まり] こぐまのクーク物語` or `くま クマ 熊 ベアー (PASH! ブックス) - くまなの`), the parser automatically extracts suggested `Authors`, `Imprint`, `SeriesName`, and `SeriesIndex` (`第01巻`, `Vol.1`, etc.).
2. **Non-Destructive Single & Bulk Editing**:
   - Every bibliographic field (`Title`, `SortTitle`, `Authors`, `AuthorSort`, `Isbn`, `Publisher`, `Imprint`, `PublishedDate`, `SeriesName`, `SeriesIndex`, `Description`, `Language`) can be edited in the UI Inspector or Table View.
   - The raw OPF values are preserved in `opf_original_json` so you can always see what was originally inside the EPUB and revert if needed, without ever modifying the `.epub` binary on disk.
   - **Bulk Edit Mode**: Select 10, 50, or 500 books at once to batch-assign `Folder`, `Series Name`, `Authors`, `Publisher`, `Imprint`, `Tags`, or `Custom Fields` (like `jiten.moe` URL), or run **Auto-Number Series Volumes** across selected books.
3. **24k-Scale Fast Directory Scanner & Virtualized Queries**:
   - **Two-Stage Ingestion**: Scanning a directory of 24,000 EPUBs reads only the ZIP central directory, `container.xml`, and `.opf` file in parallel (`Parallel.ForEachAsync`) and writes in batched SQLite WAL transactions (500 books/tx), indexing thousands of books per minute without reading chapter XHTML bodies upfront.
   - **Server-Side Pagination & FTS5**: `library.listBooks` queries SQLite with `LIMIT @limit OFFSET @offset` and `books_fts` (`MATCH`) so searching, sorting, and scrolling across 24,000 books stays under `< 5 ms` with constant memory usage.

---

## 3.3 Hierarchical Virtual Folders, Series Volumes & Manual Tags (`FolderAndTagOrganizer`)

To support deep personal library organization without moving or renaming files on disk, `FolderAndTagOrganizer` provides:

1. **Nested Virtual Folder Tree (`library_folders`)**:
   - Users can create any depth of nested collections, categories, and series folders:
     ```text
     📁 Japanese (collection)
      ├── 📁 Shounen (category)
      │    └── 📚 Dragon Ball (series) ──► [Vol. 1, Vol. 2, Vol. 3 ...]
      └── 📁 Seinen (category)
           ├── 📚 Berserk (series)     ──► [Vol. 1, Vol. 2 ...]
           └── 📚 Monster (series)     ──► [Vol. 1, Vol. 2 ...]
     ```
   - Clicking a parent folder (e.g. `Japanese` or `Seinen`) in the sidebar can either show its direct child subfolders + books, or recursively show all books under that subtree (`includeDescendants: true`) grouped by Series.
   - Moving a folder or book is an instant SQLite update (`parent_folder_id`, `folder_id`, `sort_order`)—disk paths (`books/<book>/`) remain completely untouched so `enrichement-extractor` links never break.

2. **Series & Volume Sorting (`series_name`, `series_index`, `custom_sort_order`)**:
   - Each book has an optional `series_name` and numeric `series_index` (`REAL`, supporting `1`, `2`, `2.5`, `10`) plus a `custom_sort_order` integer.
   - When sorting by `Series / Volume` (the default inside Series and Category folders), volumes sort numerically (`1, 2, 2.5, 3 ... 10` instead of ASCII `1, 10, 2`).

3. **Manual User Tags (`tags` & `book_tags`)**:
   - Users can create manual tags with custom names and colors (e.g., `Slice of Life`, `Sci-Fi`, `Light Novel`, `Reading Next`, `Hard Kanji`) and assign/unassign them on any book (or batch of selected books).
   - The Library view supports filtering by one or more tags (`Match All (AND)` or `Match Any (OR)`) combined with the selected folder subtree, plus sorting by `Series & Volume`, `Custom Manual Order`, `Last Read`, `Date Added`, `Title`, or `% Completed`.

---

## 4. Per-Book Configuration (`study.config.json`) Management

The Library Manager reads and writes `books/<book>/study.config.json` while strictly respecting [`enrichement-extractor/immersive/config.schema.json`](../enrichement-extractor/immersive/config.schema.json):

```csharp
public sealed record BookStudyConfig
{
    [JsonPropertyName("end_before_href")]
    public string? EndBeforeHref { get; init; }

    [JsonPropertyName("include_titles")]
    public IReadOnlyList<string>? IncludeTitles { get; init; }

    [JsonPropertyName("exclude_titles")]
    public IReadOnlyList<string>? ExcludeTitles { get; init; }

    [JsonPropertyName("aliases")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Aliases { get; init; }

    [JsonPropertyName("context_glosses")]
    public IReadOnlyDictionary<string, string>? ContextGlosses { get; init; }

    [JsonPropertyName("print_context")]
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, PrintContextOverride>>? PrintContext { get; init; }
}
```
- When a user marks a reviewed context gloss or spelling alias inside the .NET Reader UI, they can choose to save it either to their global `vocabulary_state` table or directly into the book's `study.config.json` (`context_glosses` / `aliases`) so future PDF/HTML study-pack builds in `enrichement-extractor` automatically pick up the reviewed override!
- Any write to `study.config.json` uses temp-file + `File.Move(..., overwrite: true)` atomic replacement and validates against `config.schema.json` rules (`additionalProperties: false`).

---

## 5. Interoperability with `enrichement-extractor` Study Packs

`PythonStudyPackBridge` in `ImmersiveReading.Library` connects the live .NET reader with the offline Python study-pack generator:

1. **Reading Published Snapshots**:
   - Inspects `enrichement-extractor/output/.publication/active.json` (verifying `manifest.json` SHA-256 just like [`publication.py#L135-L168`](../enrichement-extractor/immersive/publication.py#L135-L168)).
   - Acquires a compatible reader lease file in `.publication/leases/<generation>/<uuid>.lock` while reading snapshot files so `immersive.retention` never prunes an active snapshot.
   - Surfaces per-book links in the Library UI:
     - **Master PDFs**: `kanji-master.pdf`, `vocabulary-master.pdf`, `grammar-master.pdf`
     - **Chapter PDFs**: `chapter-XX-kanji.pdf`, `chapter-XX-vocabulary.pdf`
     - **20-Word Learning Sets**: Reads `vocabulary-sets.json` so the user can view/study the exact 20-word sets for the chapter they are currently reading.
2. **Triggering Background Study Pack Builds**:
   - From the Book Detail view in the Library UI, the user can click:
     - **"Build Study Pack (HTML + PDFs + Flashcards)"**: Runs `.venv\Scripts\python.exe -m immersive.cli build --book "<folder>" --cards` asynchronously with a `CancellationToken`.
     - **"Update CSV Sort Keys (`first_appearance`)"**: Runs `.venv\Scripts\python.exe -m immersive.cli decks --book "<folder>"`.

---

## 6. Library UI Features (Photino Frontend)

1. **Bookshelf View**:
   - Filter by language (`ja`, all), reading status (`Reading`, `Unread`, `Completed`), or search by title/author.
   - Sort by `Last Read`, `Date Added`, `Title`, or `% Completed`.
   - Visual badges on each book card: Language tag (`JA`), Progress bar (`42%`), Known Vocabulary Coverage % (computed by comparing book tokens against `vocabulary_state`), and `Study Pack Ready` badge.
2. **Import & Deck Attachment Modal**:
   - Drag-and-drop `.epub` and optional `.csv` (Jiten deck) files.
   - Preview detected chapters and toggle `exclude_titles` / `end_before_href` before saving `study.config.json`.
3. **Book Detail & Vocabulary Coverage Drawer**:
   - Chapter list with character counts, per-chapter reading status, and direct links to Chapter Kanji/Vocabulary PDFs if built.
   - Book-level vocabulary statistics (Total unique words, % Known, % Learning, % Unknown).

