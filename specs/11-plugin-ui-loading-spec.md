# Specification 11: Plugin UI Loading (blob-module loader)

## Status: Approved for implementation
**Fixes:** plugin UI bundles never load in the desktop app (found while verifying [spec 10](10-pos-highlight-category-menu-spec.md)).
**Scope:** client plugin loader, `extensions.getAsset` RPC, removal of the `ir-app://sdk` / import-map mechanism, plugin authoring guide, verification.
**Plan:** [11-plugin-ui-loading-implementation-plan.md](11-plugin-ui-loading-implementation-plan.md) · **Feature file:** [features/09-plugin-ui-loading.feature](features/09-plugin-ui-loading.feature)

---

## 1. Problem

`MainWindowHost` calls `window.Load(wwwroot/dist/index.html)`, so the SPA runs at origin `file://`. The plugin system loads UI with `import('ir-app://plugins/<id>/ui.js')` and resolves `react`, `lucide-react` and `@ir/sdk` through an import map to `ir-app://sdk/*.js` shims. From a `file://` page, **every `fetch()`/`import()` of an `ir-app://` URL fails** ("Failed to fetch"), including the shims. Result: no plugin UI (POS Highlights menu, AiCompanion drawer/page/settings panel) can ever appear. Server-side serving is correct and unit-tested; the failure is in the WebView's cross-origin rules for custom schemes, which the scheme handler cannot relax (no CORS headers).

## 2. Evidence (experiments run 2026-11-14 in the real window via `--verify-ui`)

| Experiment | Result |
| :--- | :--- |
| `fetch`/`import` of `ir-app://plugins/…`, `ir-app://sdk/…` from the `file://` page | **Fail** (all) |
| Load the SPA via `window.Load(new Uri("ir-app://app/index.html"))` so everything is same-scheme | **Blank window**: the SPA never made an RPC call (module scripts from a custom-scheme origin do not load) |
| `import()` of a `blob:` URL created from a JS string, from the `file://` page | **Works** |
| JSON-RPC over Photino web messages | **Works** (used by everything else) |

Conclusion: do not rely on the custom scheme for code. Fetch plugin code over RPC and run it as blob modules.

## 3. Requirements

- **FR-1** Plugin UI bundles (`manifest.uiEntry`) load and `activate(ctx)` runs, in the real app, for built-in and external plugins.
- **FR-2** Plugins keep the ergonomic authoring contract: `import React from 'react'`, `'react-dom'`, `'react/jsx-runtime'`, `'lucide-react'`, `'@ir/sdk'`.
- **FR-3** Named exports of those modules are **derived from the host runtime object at load time**, with no hand-maintained export lists (removes the failure class from the spec-10 lesson).
- **FR-4** Plugin stylesheets (`manifest.styleEntry`) are loaded and injected as `<style data-ir-plugin="id">`, removed on unload.
- **FR-5** A failed load is visible: the error is stored per plugin and shown on the plugin's card in Settings → Installed Plugins (not only `console.error`).
- **FR-6** Hot enable/disable/reload still works: re-enable re-loads fresh code (no stale module), disable removes registrations, styles and live reader styles.
- **FR-7** `--verify-ui` fails if a first-party plugin UI does not load (spec 10's `verifyPosCategoryMenu` becomes fatal).
- **NFR-1** Offline, no new dependencies, no change to the `IImmersivePlugin` C# contract.
- **NFR-2** Asset reads are confined to the plugin's own assets: no `..`, allow-listed extensions (`.js .mjs .css .json`), size cap 5 MB.
- **NFR-3** One documented way to write a plugin UI: `wiki/plugin-ui-authoring.md`.

## 4. Design

### 4.1 Server: `extensions.getAsset`
`{ pluginId, path }` → `{ path, contentType, text }`.
Uses `PluginHostManager.OpenAssetStream` (external folder first, then embedded resource). Rejects with an error for: unknown plugin, path containing `..` or rooted, extension not allow-listed, size over cap, asset not found. Disabled plugins are still readable (the host decides when to load).

`extensions.list` is unchanged (`uiBundleUrl` remains for compatibility, but the client no longer fetches it).

### 4.2 Client: `services/pluginModuleLoader.ts`
```
loadPluginModule(pluginId, entry): Promise<module>
  1. text   = rpc extensions.getAsset(pluginId, entry)
  2. text'  = rewriteBareImports(text, specifier -> shimBlobUrl(specifier))
  3. blob   = URL.createObjectURL(new Blob([text'], {type:'text/javascript'}))
  4. module = await import(blob); URL.revokeObjectURL(blob)
```
- **Shims** are generated once per session from `window.__IR_RUNTIME__` (already exposed by `exposeHostRuntime`): `react`→`React`, `react-dom`→`ReactDOM`, `react/jsx-runtime`→`JsxRuntime`, `lucide-react`→`LucideReact`, `@ir/sdk`→`HostSdk`. Shim source: `const M = window.__IR_RUNTIME__.<Key>; export default M; export const { a, b, … } = M;` with `a, b, …` = the runtime object's own keys that are valid identifiers and not `default`. Each shim is a blob URL cached for the session.
- **Specifier rewriting** handles string-literal specifiers in `import … from 'x'`, `export … from 'x'`, bare `import 'x'` and `import('x')`. Unsupported bare specifiers raise a clear error naming the plugin and specifier (not a browser link error).
- **Limitation (documented):** a plugin UI is a **single self-contained file**; relative imports (`./x.js`) are not resolved. Bundle multi-file UIs first.

### 4.3 Store integration (`usePluginStore.loadPluginUiBundle`)
- Uses `manifest.uiEntry` (not the URL), calls the loader, records `uiErrors[pluginId]` on failure and clears it on success.
- `styleEntry` → `extensions.getAsset` → `<style data-ir-plugin>` in `document.head`; removed in `unloadPluginUiRegistrations` together with the other registrations.
- Settings plugin card renders `uiErrors[pluginId]` next to the existing `lastError`.

### 4.4 Removals
Delete the import map in `client/index.html`, the `ir-app://sdk/*` route and `AppSchemeHandler.GetSdkShim`, and tests that only covered them. The `ir-app://plugins/…` asset route stays (image/asset URLs) but is no longer used for code.

### 4.5 Why not the alternatives
| Option | Verdict |
| :--- | :--- |
| Load SPA through `ir-app://app/…` (same-origin) | Rejected: blank window in the real WebView |
| Serve the app from a local HTTP server | Rejected: opens a localhost port, extra attack surface, against the "no network" ethos |
| Bundle plugin UIs into the host SPA | Rejected: kills external/drop-in plugins and hot reload |
| Pass `{React, icons, sdk}` into `activate()` instead of imports | Rejected: breaks normal bundlers/authoring; blob + rewrite keeps `import` syntax |

## 5. Edge cases
- Plugin imports an unsupported bare specifier → load fails with `Plugin 'x' imports 'foo', which is not available (supported: …)`.
- `export default function activate` vs named `activate` → both accepted (existing store code checks `module.activate`; unchanged).
- Re-loading the same plugin creates a new blob each time, so changes in an external plugin folder are picked up.
- Asset exceeds cap or has a disallowed extension → clear error in the plugin card.
- Strict CSP/WebView updates later: if blob modules are ever blocked, `verify-ui` fails fatally (FR-7), so it cannot regress silently.

## 5b. As built (deviations and additions)
- **AiCompanion `ui.js` was raw JSX** and could never have run. Added `client/scripts/build-plugin-uis.mjs` (`npm run build:plugins`, rolldown, host modules external, automatic JSX runtime). AiCompanion source is now `Assets/ui.src.jsx`; the generated `ui.js` is committed; `*.src.*` is excluded from embedding.
- `loadPluginUiBundle(pluginId)` now reads `manifest.uiEntry`/`styleEntry` instead of taking a URL.
- Guard tests: first-party `ui.js` only imports supported specifiers (list read from `pluginImportRewriter.ts`, no second copy) and contains no raw JSX.
- Not covered: relative imports inside a plugin (documented limitation); the pre-existing lint error in `sdk/index.tsx` (`useEvent` calls a hook inside a callback) is untouched.

## 6. Test plan
**C#**: `extensions.getAsset` returns POS `ui.js`; rejects `..`, rooted paths, `.dll`, unknown plugin, missing asset; contract test for result keys.
**Client**: `tsc -b`, `vite build`, lint without new findings. The rewrite function is a pure function placed in its own module; it gets a small self-check executed by `--verify-ui` (cases listed in the plan) because the client has no unit-test runner.
**End to end (real window)**: `--verify-ui` loads POS and AiCompanion UI bundles (assert registrations exist), then runs spec 10's menu scenario, fatal on failure.
