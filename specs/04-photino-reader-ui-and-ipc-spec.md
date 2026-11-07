# 04 — Photino.NET Desktop App, Custom Scheme & IPC Specification

> **Module:** `ImmersiveReading.App`  
> **UI Host:** `Photino.NET` (`PhotinoWindow`)  
> **Custom URI Schemes:** `ir-app://` (Application UI) and `ir-book://` (On-the-Fly Enriched EPUB Content & Assets)

---

## 1. Photino.NET Host Architecture

`ImmersiveReading.App` uses **Photino.NET** to host the application UI inside the OS native WebView (Edge WebView2 on Windows) without bundling a 150 MB Chromium binary.

### Why Custom URI Schemes (`ir-book://`) Are Essential
Instead of extracting EPUB files to temporary folders on disk or sending megabytes of base64 HTML over WebMessage IPC:
1. We register a custom scheme `ir-book` via `PhotinoWindow.RegisterCustomSchemeHandler("ir-book", HandleBookResourceRequest)`.
2. When the reader view navigates an `<iframe>` (or shadow/container viewport) to:
   ```text
   ir-book://book-334a48c88bcc5342/OEBPS/Text/chapter01.xhtml?furigana=unknown&theme=paper
   ```
   Photino intercepts the request in C#:
   - **If the path is an XHTML/HTML spine document**:
     - Streams the entry from the cached `ZipArchive` (or `ChapterEnrichmentCache`), runs `AngleSharpDomEnricher` + `ILanguagePlugin` (`MeCab.DotNet`), injects the reader interaction stylesheet (`ir-reader-injected.css`) and lightweight event bridge (`ir-reader-bridge.js`), and returns the UTF-8 stream with `contentType = "application/xhtml+xml; charset=utf-8"`.
   - **If the path is an EPUB relative asset (`.css`, `.jpg`, `.png`, `.webp`, `.svg`, `.woff2`, `.ttf`)**:
     - Because the chapter was loaded from `ir-book://book-334a48c88bcc5342/OEBPS/Text/chapter01.xhtml`, the browser automatically resolves relative URLs like `../Images/cover.jpg` or `../Styles/style.css` to:
       ```text
       ir-book://book-334a48c88bcc5342/OEBPS/Images/cover.jpg
       ```
     - `HandleBookResourceRequest` validates the normalized relative path inside the EPUB (rejecting `..` escapes, matching [`enrichement-extractor/immersive/yomitan.py#L48-L50`](../enrichement-extractor/immersive/yomitan.py#L48-L50)) and streams the raw asset bytes directly from the EPUB archive with the appropriate MIME type!

```csharp
var window = new PhotinoWindow()
    .SetTitle("Immersive Reading")
    .SetUseOsDefaultSize(false)
    .SetSize(1400, 920)
    .Center()
    .RegisterCustomSchemeHandler("ir-app", appSchemeHandler.Handle)
    .RegisterCustomSchemeHandler("ir-book", bookSchemeHandler.Handle)
    .RegisterWebMessageReceivedHandler(rpcDispatcher.OnMessageReceived);
```

---

## 2. Bidirectional JSON-RPC IPC Protocol

Communication between the Photino frontend (`window.external.sendMessage` / `window.external.receiveMessage`) and .NET backend uses a typed JSON-RPC 2.0 envelope:

### Request Envelope (JS -> .NET)
```json
{
  "id": "req-104",
  "method": "lookup.query",
  "params": {
    "bookId": "book-334a48c88bcc5342",
    "chapterHref": "OEBPS/Text/chapter01.xhtml",
    "tokenId": 142,
    "charOffsetInToken": 0
  }
}
```

### Response Envelope (.NET -> JS)
```json
{
  "id": "req-104",
  "result": { ... },
  "error": null
}
```

### Push Event Envelope (.NET -> JS)
```json
{
  "event": "studyPack.progress",
  "data": {
    "bookId": "book-334a48c88bcc5342",
    "stage": "documents",
    "message": "Rendering chapter PDFs..."
  }
}
```

### Core RPC Methods Catalog

| Method | Params | Returns | Description |
| :--- | :--- | :--- | :--- |
| `library.listBooks` | `{ folderId?, includeDescendants?, tagIds?, tagMatchMode?, search?, customFieldFilters?, sort?, offset, limit }` | `PagedBookListResult` | Server-side paginated (< 5ms) query across 24,000+ books using SQLite covering indexes & `books_fts`. |
| `library.scanDirectory` | `{ rootDirectory, targetFolderId?, recursive }` | `ScanBatchResult` | Multi-threaded OPF & cover batch scanner for large (24k+) ebook directories with live progress events. |
| `library.getFolderTree` | `{}` | `LibraryFolderNode[]` | Returns the full nested virtual folder hierarchy (e.g. `Japanese -> Seinen -> Series`) with book counts & custom fields. |
| `library.saveFolder` | `{ folderId?, parentFolderId?, name, folderKind, description?, sortOrder, customFields? }` | `LibraryFolderNode` | Creates, renames, or moves a virtual folder/collection/category/series node and its custom fields. |
| `library.deleteFolder` | `{ folderId, reassignParent? }` | `{ ok: true }` | Deletes a virtual folder (leaving books safely in catalog). |
| `library.updateBookMetadata` | `{ bookId, title, sortTitle, authors, authorSort, isbn, publisher, imprint, publishedDate, seriesName, seriesIndex, description, language, folderId?, customSortOrder?, tagIds?, customFields? }` | `BookDetail` | Edits a book's bibliographic metadata, folder/series placement, tags, and custom fields without mutating the `.epub`. |
| `library.bulkUpdateBooks` | `{ bookIds[], patch }` | `{ updatedCount }` | Batch-updates folder, series name, auto-numbered volume index, author, publisher, tags, or custom fields across many books. |
| `library.resetBookMetadataToOpf`| `{ bookId }` | `BookDetail` | Restores a book's bibliographic fields from its immutable `opf_original_json` snapshot. |
| `library.listCustomFieldDefs` | `{}` | `CustomFieldDefinition[]` | Lists user-defined and built-in custom fields (e.g. `jiten_moe_url`, `vocab_deck_csv`, `jiten_difficulty`). |
| `library.saveCustomFieldDef` | `{ fieldKey, displayName, fieldType, targetScope, inheritToChildren, showOnCard, sortOrder }` | `CustomFieldDefinition` | Creates or updates a custom field definition attachable to folders/series and/or books. |
| `library.setCustomFieldValue` | `{ entityType, entityId, fieldKey, valueText, valueNumber? }` | `{ ok: true }` | Sets a custom field value on a folder/series (`entityType="folder"`) or book (`entityType="book"`). |
| `library.listTags` | `{}` | `TagRecord[]` | Lists all user-defined tags and book counts. |
| `library.saveTag` | `{ tagId?, name, colorHex, sortOrder }` | `TagRecord` | Creates or updates a manual user tag. |
| `library.importBook` | `{ epubPath, csvPaths[], folderId?, seriesName?, seriesIndex?, tagIds?, customFields?, copyToWorkspace }` | `BookDetail` | Hashes EPUB, extracts metadata/ISBN/TOC/cover, imports into `library.db`. |
| `library.getBookDetail` | `{ bookId }` | `BookDetail` | Returns editable metadata, original OPF snapshot, effective + inherited custom fields, chapters, tags, and study pack links. |
| `library.updateStudyConfig` | `{ bookId, config }` | `BookStudyConfig` | Validates against `config.schema.json` and saves `study.config.json`. |
| `library.buildStudyPack` | `{ bookId, html, pdf, cards, updateDecks }` | `JobResult` | Invokes `enrichement-extractor` (`immersive.cli`) asynchronously. |
| `reader.openBook` | `{ bookId }` | `ReaderSessionInfo` | Returns TOC, saved `ReadingProgress`, and active `EnrichmentOptions`. |
| `reader.saveProgress` | `{ bookId, chapterIndex, spineHref, tokenId, charOffset, ratio }` | `{ ok: true }` | Persists reading position and updates reading time in `library.db`. |
| `reader.setEnrichmentOptions`| `{ furiganaMode, highlightMode, writingMode }` | `{ ok: true }` | Updates pre-render enrichment settings and refreshes chapter view. |
| `lookup.query` | `{ bookId, chapterHref, tokenId, charOffsetInToken }` | `LookupResult` | Sub-5ms on-the-fly dictionary & kanji lookup at the tapped token. |
| `lookup.searchText` | `{ query, language, maxResults }` | `LookupResult` | Free-text search by spelling, kana reading, or English gloss. |
| `vocab.setStatus` | `{ language, lemma, reading, status, customGloss? }` | `VocabularyItem` | Updates word status (`New`/`Learning`/`Known`/`Ignored`) & live DOM. |
| `vocab.exportCard` | `{ bookId, chapterHref, tokenId, term, senseIndex }` | `{ cardId }` | Adds flashcard to `output/data/flashcards/library.json` or user deck. |
| `dictionaries.list` | `{}` | `DictionarySource[]` | Lists installed Yomitan, KANJIDIC2, and Kanji alive dictionaries. |
| `dictionaries.importYomitan`| `{ zipPath }` | `DictionarySource` | Streams and indexes a format-3 Yomitan `.zip` into `dictionaries.db`. |
| `plugins.list` | `{}` | `PluginInfo[]` | Lists active `ILanguagePlugin` and `IFeaturePlugin` implementations. |
| `ext.<pluginId>.<action>` | `{ ... }` | `object` | Dynamically routed RPC namespace for `IFeaturePlugin` extensions (e.g., `ext.aiCompanion.generateQuiz`, `ext.aiCompanion.sendChatMessage`). |

---

## 3. Injected Pre-Render Reader Markup & CSS/JS Contract

When `AngleSharpDomEnricher` processes an EPUB chapter before serving it via `ir-book://`, it injects two assets into `<head>`:
1. `<link rel="stylesheet" href="ir-app://assets/ir-reader-injected.css" />`
2. `<script src="ir-app://assets/ir-reader-bridge.js" defer="defer"></script>`

### 3.1 Injected DOM Structure Example
Original EPUB XHTML paragraph:
```xml
<p><ruby>人気<rt>ひとけ</rt></ruby>のない森で、木の実を食べた。</p>
```

Serialized XHTML after `AngleSharpDomEnricher` + `JapaneseLanguagePlugin` (`MeCab.DotNet`):
```xml
<p data-ir-pid="0">
  <span class="ir-tok ir-st-learning ir-in-deck" data-tid="0"><ruby>人気<rt>ひとけ</rt></ruby></span><span class="ir-tok" data-tid="1">の</span><span class="ir-tok" data-tid="2">ない</span><span class="ir-tok ir-st-known" data-tid="3"><ruby class="ir-ruby-gen">森<rt>もり</rt></ruby></span><span class="ir-tok" data-tid="4">で</span><span class="ir-tok" data-tid="5">、</span><span class="ir-tok ir-st-new ir-in-deck" data-tid="6"><ruby class="ir-ruby-gen">木<rt>き</rt></ruby>の<ruby class="ir-ruby-gen">実<rt>み</rt></ruby></span><span class="ir-tok" data-tid="7">を</span><span class="ir-tok ir-st-known" data-tid="8"><ruby class="ir-ruby-gen">食<rt>た</rt></ruby>べた</span><span class="ir-tok" data-tid="9">。</span>
</p>
```
*(Note: No whitespace is inserted between `<span class="ir-tok">` elements in CJK text so inline layout and line-breaking remain 100% identical to the un-enriched text!)*

### 3.2 CSS Classes for Instant Visual التغيير (Zero Re-Parse)
Because AngleSharp tags generated `<ruby class="ir-ruby-gen">` separately from author `<ruby>`, and tags tokens with `data-lemma` or status classes (`ir-st-new`, `ir-st-learning`, `ir-st-known`), toggling reader display modes only requires toggling a class on `document.documentElement`:

```css
/* Hide all furigana (both EPUB author and plugin-generated) */
html.ir-furigana-off ruby > rt {
  visibility: hidden;
}

/* Show furigana ONLY on hover when in practice mode */
html.ir-furigana-hover ruby > rt {
  opacity: 0;
  transition: opacity 0.12s ease;
}
html.ir-furigana-hover .ir-tok:hover ruby > rt {
  opacity: 1;
}

/* Author-only mode: hide generated furigana, keep original EPUB ruby */
html.ir-furigana-author ruby.ir-ruby-gen > rt {
  display: none;
}

/* Unknown-only furigana mode: hide generated furigana on Known words */
html.ir-furigana-unknown .ir-st-known ruby.ir-ruby-gen > rt {
  display: none;
}

/* Subtle underline for words in the book's study deck or Learning state */
html.ir-highlight-vocab .ir-tok.ir-st-learning {
  text-decoration: underline dotted #c86432 1.5px;
  text-underline-offset: 0.22em;
}
```

### 3.3 Japanese Vertical & Horizontal Typography Support
- **Vertical Mode (`writing-mode: vertical-rl`)**:
  - Uses CSS multi-column layout (`column-width: calc(100vh - 80px); column-gap: 48px;`) or viewport-paged columns so each page turn scrolls horizontally right-to-left (`RTL` page progression) by exact column widths.
  - Preserves `text-combine-upright: all` (`tatechuyoko`) for 2-digit numbers and `ruby-position: over` (right side of vertical text).
- **Horizontal Mode (`writing-mode: horizontal-tb`)**:
  - Supports both 2-column book spread / 1-column paginated mode and continuous vertical scroll mode.

---

## 4. Dictionary Popover UI Specification

When `lookup.query` returns a `LookupResult` (< 5 ms):
1. **Active Span Highlighting**:
   - All `<span class="ir-tok">` elements from `HighlightStartTokenId` to `HighlightEndTokenId` receive `.ir-tok-active`, highlighting the exact matched compound or inflected verb in the reader text.
2. **Smart Popover Positioning**:
   - Anchors above/below the word in `horizontal-tb` mode, or to the left/right of the vertical column in `vertical-rl` mode, staying within viewport bounds.
3. **Popover Tabs & Controls**:
   - **Words Tab**:
     - Headword (`Expression`), Kana Reading (`Reading`), Pitch Accent SVG diagram (`[0]`, `[1]`, etc.), Frequency badges (`JPDB`, `VN`, `Jiten #412`), and `Matches Book Ruby` / `Set #04` badges.
     - Inflection chain chip (e.g., `食べた ← 食べる (past)`).
     - Status quick-toggle pill (`New` | `Learning` | `Known` | `Ignored`) — keyboard shortcuts `1`, `2`, `3`, `4`.
     - Reviewed Context Gloss banner (if set) + expandable Dictionary Sense blocks grouped by dictionary source.
   - **Kanji Tab**:
     - One card per kanji in the matched word showing `Kanji alive` primary meaning, `ON`/`KUN` readings, interactive **KanjiVG stroke-order diagram** (with step-by-step red current stroke matching the print layout!), and related words in the current book.
   - **Recursive Lookup inside Popover**:
     - Clicking any Japanese word inside a monolingual dictionary definition or example sentence inside the popover opens a nested lookup view.

