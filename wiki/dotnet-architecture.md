# .NET Architecture (`immersive-reader/`)

> **Read this first for any C# task.** For Python (`enrichement-extractor/`) see [architecture-and-dataflow.md](architecture-and-dataflow.md). Cross-language flows: [DEVELOPER_GUIDE.md](../DEVELOPER_GUIDE.md).

## 1. Solution layout & dependency direction

`net10.0`, `Nullable` + `ImplicitUsings` on, `TreatWarningsAsErrors=false` ([`Directory.Build.props`](../immersive-reader/Directory.Build.props)). Baseline: **0 warnings, 0 errors**. Keep it that way.

```
App ──► Library ──► Core
 │  ├─► Knowledge ──► Core   (knowledge hub + the user's memory, spec 24)
 │  ├─► Enrichment ─► Core
 │  ├─► Dictionaries ► Core
 │  └─► Plugins.{Japanese,AiCompanion,PosHighlighter,JitenMoe,DeckBuilder,CharacterKnowledge} ─► Core
 │         (DeckBuilder ─► Dictionaries, PosHighlighter ─► Enrichment: the only two exceptions, see below)
Tests: Core.Tests, Enrichment.Tests, Dictionaries.Tests, App.Tests
```

- **Core** has no sibling dependencies: contracts, models, EPUB metadata parsing, `MigrationRunner`, plugin interfaces, the prompt template engine and catalog (`Core.Prompts`), the knowledge contracts (`Core.Knowledge`), and helpers that App *and* plugins both need (for example `Core.Llm.LlmEndpointTester`).
- **Library / Enrichment / Dictionaries / Knowledge** reference only Core (never each other, never App).
- **App** is the only composition root. Concrete plugins are named *only* in [`Composition/`](../immersive-reader/src/ImmersiveReading.App/Composition/PluginCatalog.cs); an RPC module that needs something a plugin also uses gets it from Core.
- **Plugins reach each other's knowledge only through `context.Knowledge` (`IKnowledgeHub`, contract in `Core.Knowledge`)**: a plugin registers a source with `RegisterKnowledgeSource` and never references another plugin. A source that hides unread story must declare `KnowledgeGating.StoryPosition`.
- **A plugin** references Core and nothing else, except where it implements a contract another project owns: `DeckBuilder` implements `IDeckDictionary` on the dictionary store, `PosHighlighter` is a stage of the enrichment pipeline. A plugin reads library data through `IHostLibrary` / `IBookCatalog`, never from the library tables, and keeps its own data in its own tables or files.
- **SQL belongs in Library / Dictionaries services**, not in RPC modules. RPC modules parse parameters, call a service, shape the result.
- If two non-Core projects need the same type, move the contract to Core. Don't add a sibling reference.

**These rules are enforced by [`ArchitectureTests`](../immersive-reader/tests/ImmersiveReading.App.Tests/ArchitectureTests.cs)** (project references, `using` namespaces, SQL placement) and, for Python, by `knowledge-graph/tests/test_architecture.py` (module layers, only `store` touches SQLite, only `llm` touches the network, no import of the study pipeline) and `enrichement-extractor/tests/test_architecture.py` (no network library, no import of the LLM project). Debts the tests still allow are listed in the test file with a ceiling that may only go down: raw SQL in `ReaderRpcModule` (8 statements), `BootstrapRpcModule` (2), `StudyPackRpcModule` (2), `DictionaryRpcModule` (1); and the AI companion's tables created by `Library/Schema/LibraryMigrations.cs` and deleted by name in `ClearLibrary`. When you pay one off, lower the number or remove the entry.

## 2. Startup & composition

[`Program.cs`](../immersive-reader/src/ImmersiveReading.App/Program.cs) → `AppPaths` (discovery) → `AppServices.Create` (DI via `Microsoft.Extensions.DependencyInjection`, plugin registration, `PluginHostManager.InitializePluginsAsync`) → `MainWindowHost` (Photino window).
`AppServices.CreateRpcModules` builds the `IRpcModule` list. Sync-over-async (`.GetAwaiter().GetResult()`) appears only at startup/shutdown in `AppServices` and `Program`. Don't copy it into request paths.

## 3. RPC bridge (frontend ↔ C#)

- JSON-RPC 2.0 over Photino IPC. [`LibraryRpcDispatcher`](../immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs) routes `"module.method"` strings to handlers that each [`IRpcModule.Handlers`](../immersive-reader/src/ImmersiveReading.App/Rpc/IRpcModule.cs) yields as `(Method, RpcHandler)` tuples, then falls through to `_pluginHostManager.TryGetRpcHandler(...)` and `_featurePluginHost.TryGetRpcHandler(...)` for `ext.<pluginId>.<action>` plugin endpoints.
- The 10 modules in `App/Rpc/Modules/`: `Appearance`, `Bootstrap`, `Config`, `Dictionary`, `Extensions`, `Library`, `Memory`, `Prompts`, `Reader`, and `StudyPack`. (jiten.moe is no longer a core RPC module: it is the `jitenMoe` plugin, whose actions are `ext.jitenMoe.*`.)
- Param access: `req.RequiredString / OptionalString / OptionalInt`. Throw `ArgumentException` / `KeyNotFoundException` for bad input; the dispatcher turns exceptions into JSON-RPC errors.
- Server→UI push: `IRpcEventSink.Emit("event.name", payload)`.
- **The contract is pinned by** [`RpcContractTests`](../immersive-reader/tests/ImmersiveReading.App.Tests/RpcContractTests.cs). Result-key names are public API because the React client reads them by name. Renaming an anonymous-object property is a breaking change.

### Adding an RPC method
1. Add the handler to the right module and yield it in `Handlers`.
2. Mirror the call in the client store (`client/src/stores/*`).
3. Add or adjust a case in `RpcContractTests`.
4. Update this page only if you add a module.

## 4. Data & persistence

- Library DB: SQLite WAL + FTS5 at `.library-data/library.db`. **The schema comes from `CREATE TABLE IF NOT EXISTS` in `LibraryDatabase.SchemaSql`; `LibraryMigrations.All` is NOT applied anywhere** (the `MigrationRunner` is only used by the dictionaries DB and the AiCompanion plugin DB). To add a column to an existing library DB: add it to `SchemaSql` (fresh DBs) **and** call `EnsureColumn(...)` in `LibraryDatabase.InitializeSchema` (existing DBs, idempotent).
- Dictionaries DB: `.library-data/dictionaries.db` (Yomitan terms/meta, KANJIDIC). Importers implement `IDictionaryImporter` and register in `DictionaryImporterRegistry`.
- Plugins get a sandboxed DB at `.library-data/plugins/{pluginId}/plugin.db`.
- Always use parameterized SQL (`$name` + `AddWithValue`). Dynamic `IN (...)` lists build *parameter names*, never values (see `ReaderRpcModule.EnrichWordsWithDictDb`).

## 5. Plugins

- Unified contract [`IImmersivePlugin`](../immersive-reader/src/ImmersiveReading.Core/Plugins/IImmersivePlugin.cs) (`PluginReloadBehavior.Hot | RestartRequired` in `PluginLifecycle.cs`) managed by [`PluginHostManager`](../immersive-reader/src/ImmersiveReading.App/Plugins/PluginHostManager.cs). Every plugin project exposes an [`IPluginModule`](../immersive-reader/src/ImmersiveReading.Core/Extensions/IPluginModule.cs) registered in [`PluginCatalog`](../immersive-reader/src/ImmersiveReading.App/Composition/PluginCatalog.cs) (`JapaneseModule`, `AiCompanionModule`, `PosHighlighterModule`, `JitenMoeModule`, `DeckBuilderModule`, `CharacterKnowledgeModule`), and `AppServices` discovers plugins polymorphically via `CreateImmersivePlugins(sp)`.
- Note: because `IImmersivePlugin` inherits `IAsyncDisposable`, any plugin registered via `services.AddSingleton<MyPlugin>()` must **also** implement `IDisposable` (synchronous), or `ServiceProvider.Dispose()` will throw at shutdown.
- The legacy `FeaturePluginHost` path still supports older `IFeaturePlugin` modules. DOM enrichment stages (`IDomEnrichmentStage`) are pulled lazily through `BookSchemeHandler.DomStagesProvider`. See [LLM-WIKI.md](../LLM-WIKI.md), [creating-a-plugin-tutorial.md](creating-a-plugin-tutorial.md), and `specs/08`, `specs/09`.

First-party plugins (`src/Plugins/`): `Japanese` (language), `AiCompanion`, `PosHighlighter`, **`JitenMoe`** and **`DeckBuilder`** ([spec 15](../specs/15-jiten-plugin-and-deck-builder-spec.md)), and **`CharacterKnowledge`** ([spec 20](../specs/20-character-knowledge-plugin-spec.md)): launches the separate Python `knowledge-graph` builder as a child process and reads its SQLite output read-only. Since spec 22 it also builds and serves **events and item holdings** (second builder pass; RPC `listEvents` / `listItems`, stored cards looked up by chapter like the profiles, schema version 2 migrated in place by the builder, missing story tables read as "no story data").

**Library access for plugins** (`IPluginHostContext.Library`, `Core/Abstractions/IHostLibrary.cs`): a deliberately small metadata surface (`ListBooks`, `GetBookDetail`, `UpdateBookMetadata`, `ListTags`, `SaveTag`, `SetCustomFieldValue`) implemented by `LibraryManager`. Plugins never see the library database. `PluginHostManager` takes it as an optional last constructor argument (tests that do not need it omit it; touching `Library` then throws a clear error).

**Plugin DI gotcha:** a plugin registered as a singleton in `IPluginModule.ConfigureServices` must implement `IDisposable` as well as `IAsyncDisposable`, otherwise `AppServices.Dispose()` throws "type only implements IAsyncDisposable" (see [lessons](troubleshooting-and-lessons.md)).

## 6. Conventions we follow (and why)

| Convention | Why |
| :--- | :--- |
| One shared or injected `HttpClient`, never `new HttpClient()` per call | Per-call clients exhaust sockets and lose connection reuse |
| Catch **specific** exceptions (`SqliteException`, `IOException`, `JsonException`) and `Trace.TraceWarning` when degrading gracefully. No bare `catch { }` | Bare catches hid real failures (corrupt cache, locked DB) |
| Pass `CancellationToken` through async RPC handlers and long I/O | Window close or user cancel must stop work |
| Records for DTOs, primary constructors for module DI | Immutable and terse; matches existing modules |
| Extract duplicated selection/parsing logic into a named private helper | Example: `ReaderRpcModule.FindFirstStoryChapter` |
| Use patterns only where they remove real duplication or coupling | We use DI, registry, strategy (`IDictionaryImporter`, `ILanguagePlugin`) and pipeline stages. Don't add factories or interfaces with one implementation and no test seam |

## 7. Plugin UI host APIs (reader menu bar)

- **Toolbar actions** (`registerReaderToolbarAction`): `onClick(ctx)` for plain buttons, or `popover: Component` for an anchored panel (closes on Escape / outside click). `ctx.rpc(action, payload)` calls `ext.<pluginId>.<action>`. Implemented in `components/plugins/PluginSlots.tsx` (`ToolbarActionButton`).
- **Live reader styles**: `setReaderStyle(pluginId, styleId, css)` (SDK export) stores CSS in `usePluginStore.readerStyles`; `ChapterIframe` writes each entry into `<style id=styleId>` in the iframe on change and on every iframe load; unloading a plugin drops its styles. Use the id the server injected to *replace* it.
- **Settings schema**: properties with `"x-hidden": true` are skipped by the generic Settings form (used for values managed by a custom UI).
- **Plugin code loading** (spec 11): UI code is fetched via RPC `extensions.getAsset` and run as blob modules; bare imports (`react`, `react-dom`, `react/jsx-runtime`, `lucide-react`, `@ir/sdk`) are rewritten to shims derived from `window.__IR_RUNTIME__`. The SPA runs from `file://`, where `ir-app://` fetch/import is blocked, so never load plugin code through the scheme. Full guide: [plugin-ui-authoring.md](plugin-ui-authoring.md).
- Reference implementation: PosHighlighter ([spec 10](../specs/10-pos-highlight-category-menu-spec.md)), with `PosCategoryCatalog` as the single source for category ids, labels and CSS selectors.

## 8. EPUB chapters (spec 12)

- **Model:** a chapter = one ToC entry + the following spine files up to the next ToC entry (its `Members`; `SpineHref` is the first member = *primary*). Split text parts and illustration pages are members, not extra chapters. `chapter_model_version` on `books` is 1 (legacy: one record per spine file) or 2 (grouped).
- **Builder:** `Core/Epub/ChapterListBuilder.cs`. Rules: ToC entry starts a chapter; no-ToC story files join the open chapter; front matter joins a ToC-titled section (text pages) or collapses runs of generated "口絵 (Illustrations)" pages; back matter files join the previous back record; no story ToC at all means each text file is a chapter and illustration-only files join the previous one.
- **Persistence:** `book_chapters.member_spine_hrefs` (JSON) written by `ChapterStore.ReplaceChapters` (shared by import and upgrade).
- **Lazy upgrade:** `LibraryManager.GetBookDetail` calls `ChapterModelUpgrader.EnsureCurrent`; a v1 book is rebuilt from its EPUB in one transaction and its progress, bookmarks and quiz sessions are remapped by spine href (primary-file positions kept exactly; continuation-file positions degrade to the chapter start). A missing/corrupt EPUB leaves the book at v1. Because of this, **read paths that must not write (dry runs) must not call `GetBookDetail`**.
- **Rendering:** `BookSchemeHandler.GetEnrichedChapter` resolves any member href to its chapter; several members are merged by `Enrichment/Dom/ChapterDocumentComposer` (each member's URLs resolved against its own path, bodies wrapped in `<section class="ir-spine-part" data-ir-spine-href>`, images `loading=lazy`) and enriched in one pass; the token map is keyed by the primary href. Single-file chapters take the unchanged old path.
- **Verify:** `dotnet run --project src/ImmersiveReading.App -- --verify-chapters` rebuilds every book's chapter list in memory (no writes) and checks that the covered spine files are identical before/after. `--verify-reader` additionally asserts no generated `(Part N)`/`(Illustration)` chapters and that member hrefs resolve.
- **Illustration parts (spec 14):** parts that are image pages get `ir-image-part`; the reader CSS makes each exactly one window page (vertical: pages scroll right-to-left; horizontal: stacked, full window width). Single-file chapters keep the old render.
- **Known limit:** merged chapters have no size cap. After the rescan, 10 of 2,188 books have a chapter of more than 40 files (max 95, about 13 MB merged HTML): text books whose ToC has very few entries. Image-only books are skipped at import (section 9) and none remain in the library. Options if it hurts: cap chapter size or render lazily.

## 9. Image-only books are skipped at import (spec 13)

- **Rule:** a book is image-only when **>= 90% of its spine documents are image pages** (contain `img`/`image`/`svg` and <= 50 visible characters) **and** it averages **< 80 visible characters per document**. Constants in `Core/Epub/BookContentProfile.cs` (`ImageOnlyPolicy`). Both conditions are needed: the image-page ratio alone is misleading (547 ordinary books sit at 50-90%); characters per document has a clean gap (0 books between 60 and 100) and the threshold sits in its middle. Declared fixed layout is reported but neither required nor sufficient.
- **Profiling:** `BookContentAnalyzer` (regex, one extra read of each spine document, ruby text excluded) runs in `EpubMetadataExtractor.ExtractFromStream` and fills `ExtractedEpubPackage.Content`. Failures while profiling never make a book unimportable.
- **Behavior:** `ImportBookAsync` throws `UnsupportedBookException` before any write; `ScanDirectoryAsync` skips, counts `SkippedImageOnly`, and adds one entry per file to `Warnings` (not `Errors`; progress has `SkippedFiles`). The scan dialog shows an amber warning with an expandable file list. An image-only book already in the library is left untouched (not even deduplicated).
- **Deferred on purpose:** comic formats (`.cbz`), a force-import switch, removing the 55 image-only books already in the library (`--verify-chapters` lists them). Revisit when comic support is relevant; `ImageOnlyPolicy` and `BookContentProfile` are the extension point.

Review log and open items: [dotnet-code-review.md](dotnet-code-review.md). Past failures: [troubleshooting-and-lessons.md](troubleshooting-and-lessons.md).

## 10. Library UI slots for plugins (spec 15)

The library views have no plugin-specific code; plugins attach through three slots (all cleaned up on disable by `unloadPluginUiRegistrations`):

| Slot | Registered with | Rendered by | Notes |
| :--- | :--- | :--- | :--- |
| Book Info tab | `ctx.registerBookInfoTab({ id, title, icon, component })` | `BookInfoModal.tsx` (after the built-in tabs) | `component` gets `{ bookId, book }` (`book` = the open `BookDetail`). The modal keeps the current tab when the same book is refreshed |
| Book badge | `ctx.registerBookBadge({ id, component })` | `BookBadgeSlot` in `BookCard`, the table "Rating" column and series cards (`BookBadgeSlot` in `PluginSlots.tsx`) | `component` gets `{ books, context: 'card' \| 'row' \| 'series' }` |
| Series action | `ctx.registerSeriesAction({ id, label, icon, onClick(ctx) })` | `SeriesActionsSlot` in `LibraryGrid` (drill-down header, series cards, shelf headers) | `ctx` has `seriesName`, `openModal`, `rpc`, `getPluginConfig` |

After a plugin changes book metadata through its own RPC it must call `refreshLibrary(bookId?)` from `@ir/sdk`; that reloads the grid and the open Book Info dialog.

## 10a. Library management: bulk editing (spec 23)

- **Where:** `BookMetadataService.BulkUpdateBooks` / `PreviewBulkUpdate` (one transaction, FTS updated per book), `Books/BulkNumbering.cs` (modes `selection|title|filename|fromTitle`, natural order, volume taken from the title through `DuplicateNormalizer`), `BookQueryService.ListBookIds` / `ListSeriesNames`. RPC: `library.bulkUpdateBooks`, `previewBulkUpdate`, `listBookIds`, `listSeriesNames`.
- **Null leaves a field alone, an empty string clears it** (`COALESCE($x, column)`); strings are trimmed. The preview and the update share `BulkNumbering.Compute`.
- **UI:** `BulkActionBar` (Edit…, select all N) opens `BulkEditModal`; `useLibraryStore.selectAllMatching` asks `listBookIds` with the same filters as the list (`tagIds`, not `tags`).

## 10. Library management: duplicates (spec 21)

- **Where:** `ImmersiveReading.Library/Duplicates/`. `DuplicateDetector.Find(facts, dismissedPairs, options)` is pure (no database); `DuplicateService` loads `BookFacts` in one query and applies decisions. `LibraryManager` exposes `FindDuplicates/ResolveDuplicates/DismissDuplicates/ListRemovedBooks/ForgetRemovedBook`.
- **Never compare all pairs:** candidate pairs come from buckets (ISBN, identifier, base title by volume, series+index, author+volume). 20,000 books run in well under a second.
- **Hard separators:** different language, different volume. A volume written in the title is strong evidence, one from the file name or series index is weak (see spec 21 sections 3.2 and 10).
- **Remove = library entry only.** `Resolve` runs in one transaction: merge tags/custom fields/empty metadata into the kept book, write `removed_books`, delete via `BookMetadataService.DeleteBookInTransaction` (the single delete path). EPUB files are never touched. `BookImportService.ScanDirectoryAsync` skips ids in `removed_books` (`SkippedRemovedDuplicates` + one warning per file); `ImportBookAsync` of a single file clears the record.
- **Decisions persist:** `duplicate_dismissals` (pair keys, cascade-deleted with the books) for "not duplicates".
- **Tune with real data:** copy `.library-data/library.db*` to a scratch folder and dump `DuplicateService.FindDuplicates` groups plus the "same base+volume but not grouped" misses (that is how most rules in spec 21 section 10 were found).
