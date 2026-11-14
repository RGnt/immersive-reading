# Writing a Plugin UI (authoring guide)

> Read this before adding or changing any plugin user interface. Design record: [spec 11](../specs/11-plugin-ui-loading-spec.md). Reference plugins: `PosHighlighter` (plain JS, toolbar popover) and `AiCompanion` (JSX, view + drawer). Architecture overview: [dotnet-architecture.md](dotnet-architecture.md) §7.

## 1. How a plugin UI reaches the screen (and why)

1. The plugin's `Manifest.UiEntry` (e.g. `ui.js`) is an **embedded resource** (`Assets\**\*` in the plugin csproj).
2. On enable, the host calls RPC `extensions.getAsset { pluginId, path }` and gets the file **text**.
3. `pluginImportRewriter` rewrites the bare imports (`react`, `lucide-react`, `@ir/sdk`, …) to blob URLs of shim modules, built at runtime from `window.__IR_RUNTIME__`.
4. The result is imported as a `blob:` module and `activate(ctx)` is called.

**Why not `ir-app://` URLs?** The SPA runs from `file://`; the WebView blocks `fetch`/`import` of custom schemes from there, and loading the SPA through the scheme gives a blank window (evidence in spec 11 §2). Do not "simplify" this back to `import('ir-app://…')`.

## 2. Rules for `ui.js`

| Rule | Why |
| :--- | :--- |
| **One self-contained ES module.** No relative imports (`./x.js`) | Blob modules cannot resolve them; bundle first |
| Only these bare imports: `react`, `react-dom`, `react/jsx-runtime`, `lucide-react`, `@ir/sdk` | The host provides exactly these (`SUPPORTED_SPECIFIERS` in `services/pluginImportRewriter.ts`). Anything else fails with a readable error naming your plugin |
| **No raw JSX in `ui.js`.** Write `ui.src.jsx` and run `npm run build:plugins` (in `client/`), then commit the generated `ui.js` | Browsers cannot run JSX. A guard test fails if `ui.js` contains JSX |
| Export `activate(ctx)` (and optionally `deactivate`) | Called once per enable |
| Import specifiers must be string literals | The rewriter is regex-based |
| New icons and SDK exports need **no** registration | Shim export lists are derived from the runtime object's keys |

Plain JS plugins (like PosHighlighter) can use `React.createElement` directly and skip the build step.

## 3. What `ctx` offers (`PluginUiContext`, `sdk/types.ts`)

- `registerView`, `registerReaderDrawer`, `registerBookInfoTab`, `registerCustomSettingsPanel`
- `registerReaderToolbarAction({ id, label, icon, onClick? , popover? })`: `popover` renders an anchored panel (Escape / outside click close it). `onClick(ctx)` gets `rpc(action, payload)` scoped to `ext.<pluginId>.<action>`.
- From `@ir/sdk`: `usePluginRpc`, `usePluginConfig`, `useActiveBook`, `EnrichedText`, `PluginDrawerShell`, `PluginPageLayout`, `Button`, and `setReaderStyle(pluginId, styleId, css)` for live CSS in the open chapter (use the id the server injected to *replace* it).
- Hidden settings: mark a schema property `"x-hidden": true` to keep it out of the generic Settings form.
- Stylesheets: set `Manifest.StyleEntry` to a `.css` asset; it is injected as `<style data-ir-plugin="id">` and removed on disable.

## 4. Server side of a plugin
`IImmersivePlugin.OnEnableAsync(context)` registers RPC actions with `context.RegisterRpcAction("name", handler)`; they are reachable as `ext.<pluginId>.name`. Settings come from `context.Config` (JSON schema in `settings.schema.json`, defaults in `settings.defaults.json`, both embedded).

## 5. New plugin UI checklist
1. Add `Assets/ui.js` (or `ui.src.jsx` + `npm run build:plugins`); csproj embeds `Assets\**\*` (and removes `*.src.*`, see AiCompanion).
2. Set `UiEntry` (and `StyleEntry`) in the manifest.
3. Keep imports within section 2.
4. Add a step to `services/selfTestRunner.ts` using `withPluginUi('<id>', …)` that asserts your slot registered and exercises the main interaction. It enables the plugin, fails loudly with the real load error, and restores the user's state.
5. `dotnet test` (guard tests), `npm run build`, then `dotnet run --project src/ImmersiveReading.App -- --verify-ui`.

## 6. Debugging
- **Settings → Installed Plugins → your plugin** shows "Plugin problem: …" when the UI failed to load (import error, syntax error, asset missing).
- Typical messages: `imports 'x', which is not provided by the host` (section 2); `Unexpected token '<'` (raw JSX in `ui.js`); `Asset 'x' not found in plugin` (not embedded, or wrong `UiEntry`).
- `--verify-ui` output ends with `pluginUi=ok(...)` and `posMenu=ok(...)`. A missing field means the run failed earlier; read the `FAIL:` line.
- Stale code after editing an *embedded* plugin: rebuild the plugin project (the file is compiled into the DLL). External plugin folders are read from disk on each enable.
