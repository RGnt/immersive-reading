# .NET Architecture (`immersive-reader/`)

> **Read this first for any C# task.** For Python (`enrichement-extractor/`) see [architecture-and-dataflow.md](architecture-and-dataflow.md). Cross-language flows: [DEVELOPER_GUIDE.md](../DEVELOPER_GUIDE.md).

## 1. Solution layout & dependency direction

`net10.0`, `Nullable` + `ImplicitUsings` on, `TreatWarningsAsErrors=false` ([`Directory.Build.props`](../immersive-reader/Directory.Build.props)). Baseline: **0 warnings, 0 errors**. Keep it that way.

```
App ──► Library ──► Core
 │  ├─► Enrichment ─► Core
 │  ├─► Dictionaries ► Core
 │  └─► Plugins.{Japanese,AiCompanion,PosHighlighter}
Tests: Core.Tests, Enrichment.Tests, Dictionaries.Tests, App.Tests
```

- **Core** has no sibling dependencies: contracts, models, EPUB metadata parsing, `MigrationRunner`, plugin interfaces.
- **Library / Enrichment / Dictionaries** reference only Core (never each other, never App).
- **App** is the only composition root. Concrete plugins are referenced *only* from [`Composition/PluginCatalog.cs`](../immersive-reader/src/ImmersiveReading.App/Composition/PluginCatalog.cs).
- If two non-Core projects need the same type, move the contract to Core. Don't add a sibling reference.

## 2. Startup & composition

[`Program.cs`](../immersive-reader/src/ImmersiveReading.App/Program.cs) → `AppPaths` (discovery) → `AppServices.Create` (DI via `Microsoft.Extensions.DependencyInjection`, plugin registration, `PluginHostManager.InitializePluginsAsync`) → `MainWindowHost` (Photino window).
`AppServices.CreateRpcModules` builds the `IRpcModule` list. Sync-over-async (`.GetAwaiter().GetResult()`) appears only at startup/shutdown in `AppServices` and `Program`. Don't copy it into request paths.

## 3. RPC bridge (frontend ↔ C#)

- JSON-RPC 2.0 over Photino IPC. [`LibraryRpcDispatcher`](../immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs) routes `"module.method"` strings to handlers that each [`IRpcModule.Handlers`](../immersive-reader/src/ImmersiveReading.App/Rpc/IRpcModule.cs) yields as `(Method, RpcHandler)` tuples.
- Modules live in `App/Rpc/Modules/`: Bootstrap, Library, Jiten, Reader, Dictionary, StudyPack, Config, Extensions.
- Param access: `req.RequiredString / OptionalString / OptionalInt`. Throw `ArgumentException` / `KeyNotFoundException` for bad input; the dispatcher turns exceptions into JSON-RPC errors.
- Server→UI push: `IRpcEventSink.Emit("event.name", payload)`.
- **The contract is pinned by** [`RpcContractTests`](../immersive-reader/tests/ImmersiveReading.App.Tests/RpcContractTests.cs). Result-key names are public API because the React client reads them by name. Renaming an anonymous-object property is a breaking change.

### Adding an RPC method
1. Add the handler to the right module and yield it in `Handlers`.
2. Mirror the call in the client store (`client/src/stores/*`).
3. Add or adjust a case in `RpcContractTests`.
4. Update this page only if you add a module.

## 4. Data & persistence

- Library DB: SQLite WAL + FTS5 at `.library-data/library.db`. Schema comes from [`LibraryMigrations`](../immersive-reader/src/ImmersiveReading.Library/Schema/LibraryMigrations.cs) plus Core `MigrationRunner` (versioned `IDbMigration`). **Never edit an applied migration; add a new one.**
- Dictionaries DB: `.library-data/dictionaries.db` (Yomitan terms/meta, KANJIDIC). Importers implement `IDictionaryImporter` and register in `DictionaryImporterRegistry`.
- Plugins get a sandboxed DB at `.library-data/plugins/{pluginId}/plugin.db`.
- Always use parameterized SQL (`$name` + `AddWithValue`). Dynamic `IN (...)` lists build *parameter names*, never values (see `ReaderRpcModule.EnrichWordsWithDictDb`).

## 5. Plugins

Unified contract `IImmersivePlugin` (+ `PluginLifecycle.Hot|RestartRequired`) managed by `PluginHostManager`. The legacy `FeaturePluginHost` / `IPluginModule` path still registers feature plugins. DOM enrichment stages (`IDomEnrichmentStage`) are pulled lazily through `BookSchemeHandler.DomStagesProvider`. See [LLM-WIKI.md](../LLM-WIKI.md) and `specs/08`, `specs/09`.

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

Review log and open items: [dotnet-code-review.md](dotnet-code-review.md). Past failures: [troubleshooting-and-lessons.md](troubleshooting-and-lessons.md).
