# Troubleshooting & Lessons Learned (check here BEFORE debugging)

**Rule:** when you fix a non-trivial bug or learn a gotcha, add an entry here using the template. Newest first. Search with `grep -in <keyword> wiki/troubleshooting-and-lessons.md`.

```
### <short symptom>
- Why it mattered: <user-visible impact / why a fix was needed>
- Cause: <root cause, verified>
- Fix / how: <what changed, files, commit>
- Prevent: <test, rule, or wiki page>
```

---

### Plugin UI bundles could not load (fixed: spec 11)
- Why it mattered: no plugin UI (POS "Highlights" menu, AiCompanion drawer/page) could ever appear in the reader, and nothing said why.
- Cause: (1) the SPA runs from `file://` (`MainWindowHost` calls `window.Load(dist/index.html)`), where the WebView blocks every `fetch()`/`import()` of an `ir-app://` URL, including the SDK shims. Loading the SPA through `ir-app://app/index.html` instead gives a **blank window** (module scripts from a custom-scheme origin don't load; `window.Load(string)` also treats the argument as a file path, use `Load(Uri)`). (2) AiCompanion's embedded `ui.js` was **raw JSX**, which no browser can run, so it could never have loaded. (3) Load errors were swallowed into `console.error`.
- Fix / how: plugin code is fetched over JSON-RPC (`extensions.getAsset`) and run as `blob:` modules (blob import works from `file://`); bare imports are rewritten to blob shims generated from `window.__IR_RUNTIME__`; JSX plugins are compiled by `npm run build:plugins`; errors are stored per plugin and shown on the plugin's Settings card; `--verify-ui` now fails if a first-party plugin UI doesn't load.
- Prevent: [plugin-ui-authoring.md](plugin-ui-authoring.md). Guard tests: `FirstPartyPluginUi_OnlyImportsSpecifiersTheHostProvides`, `FirstPartyPluginUi_IsPlainJavaScript_NotRawJsx`, and the `pluginUi=` / `posMenu=` fields of `--verify-ui`.
- Method note: a hypothesis about WebView behavior is cheap to test inside `--verify-ui` (probe `fetch`/`import` in the self-test and read the summary). Do that before designing around an assumption.

### (Superseded by spec 11) Hand-maintained SDK shims / inert toolbar button
- Why it mattered: the POS "POS Colors" toolbar button did nothing, and a plugin `ui.js` can fail to load with no visible error.
- Cause: (1) plugin `ui.js` imports resolve through the import map to hard-coded shims in `AppSchemeHandler.GetSdkShim` (`lucide-react.js`, `host-sdk.js`). Importing a name the shim does not list (e.g. `Palette`, `setReaderStyle`) is an ES-module link error that kills the whole bundle. (2) `ToolbarActionContext` never contained `rpc`/`toast`, yet the old `ui.js` destructured them, so even a loaded button threw inside a try/catch.
- Fix / how (at the time): added names to the shims and a scoped `rpc` to `ToolbarActionContext`.
- Prevent: obsolete. The `ir-app://sdk` shims and `GetSdkShim` were removed; shim exports are now derived from the runtime object's keys. The scoped `rpc` in `ToolbarActionContext` stays.

### `dotnet build` fails: file locked by `ImmersiveReading.App`
- Why it mattered: build/test could not copy plugin DLLs while the app was running.
- Cause: a running `ImmersiveReading.App` process holds `bin/Debug/net10.0/*.dll`.
- Fix / how: either close the app, or build/test into the git-ignored `temp/` folder inside the repo: `dotnet test ImmersiveReading.sln -p:OutDir='K:\immersive-reading\temp\out\' -p:BuildReactClient=false`.
- Prevent: the OutDir **must stay inside the repo**. `PythonEnvironmentResolver.ResolveExtractorDir` walks up from `AppContext.BaseDirectory` to find `enrichement-extractor`, so an OutDir elsewhere (e.g. the OS temp dir) makes 3 Python-dependent tests fail with a null path.

### POS highlighting is CSS-only (what changes when)
- Facts that avoid re-investigating: the stage always writes token classes and `data-ir-pos`; only the injected `<style id="ir-pos-highlighter-style">` decides what is visible. `ChapterEnrichmentCache` stores only the token map, so every chapter load re-enriches with the *current* settings. The reader iframe is re-created per chapter (`key=bookId-spineHref`). Category selection (spec 10) therefore needs no cache invalidation; live updates just replace that `<style>` in the open iframe via `setReaderStyle`.

### Python version probe hangs the Settings UI
- Why it mattered: the "detect Python" action could block forever on a stuck interpreter.
- Cause: `ConfigRpcModule.CheckPythonVersion` called `StandardOutput.ReadToEnd()` before `WaitForExit(3000)`. `ReadToEnd` blocks until the process exits, so the timeout never fired.
- Fix / how: `ReadToEndAsync` on both streams, then `WaitForExit`, then `Kill(entireProcessTree: true)` on timeout.
- Prevent: with `RedirectStandardOutput`, never read synchronously before a timed wait. See [dotnet-code-review.md](dotnet-code-review.md).

### Silent failures from bare `catch { }`
- Why it mattered: dictionary enrichment, kanji lookup and study-pack flag updates failed with no trace.
- Cause: blanket catches in `ReaderRpcModule` and `StudyPackRpcModule`.
- Fix / how: catch `SqliteException` / `IOException` / `JsonException` and `Trace.TraceWarning`.
- Prevent: the grep command in [dotnet-code-review.md](dotnet-code-review.md).

### Dictionary preset downloads 404 (KANJIDIC, JMnedict, KireiCake)
- Cause / fix: URLs in `DictionaryRpcModule.Presets` went stale; fixed in commit `41a064c` (`git show 41a064c` for the URLs).
- Prevent: when a preset 404s, check the upstream release URL first. The `kirei*` preset id match is prefix-based by design.

### Reader text rendering / furigana toggle desync across Japanese EPUBs
- Commits: `b5d6d3a`, `1ee83e7` (`git show <sha>` for details). Touches `BookSchemeHandler`, `AngleSharpDomEnricher`, `ir-reader-bridge.js` and the injected CSS served via `ir-app://assets/`.
- Start debugging with `dotnet run --project immersive-reader/src/ImmersiveReading.App -- --verify-ui` (`ReaderVerification.cs`), which checks assets, enrichment and lookup end to end.

### Environment gotchas
- Python tests run from `enrichement-extractor` with `.venv\Scripts\python.exe`. .NET tests run from `immersive-reader` (`dotnet test ImmersiveReading.sln`).
- `ImmersiveReading.App.csproj` runs `npm run build` before build **only if `wwwroot/dist/index.html` is missing**. Pass `-p:BuildReactClient=false` to skip the Vite build.
- Some App paths are relative to `AppContext.BaseDirectory` (`.library-data/`, e.g. dictionary downloads), so running from `bin/` can use a different data dir than the repo's `immersive-reader/.library-data`.
- Bash tool on Windows: a long multi-heredoc command (or one mixing `'''` Python strings with regex backslashes) can fail to parse and write nothing, or silently mangle `\b` into a backspace. Check `git status`, and write files with the Write tool, then run a small script file.
