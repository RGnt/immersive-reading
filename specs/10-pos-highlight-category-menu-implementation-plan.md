# Implementation Plan 10: POS Category Menu

Spec: [10-pos-highlight-category-menu-spec.md](10-pos-highlight-category-menu-spec.md). Work in order; each task ends with a verifiable check. Tests are written with (or before) the code they cover.

| # | Task | Files | Done when | Status |
| :-- | :--- | :--- | :--- | :--- |
| T1 | Category catalog `PosCategoryCatalog` (id, labels, example, color accessor, selectors moved verbatim) | `Plugins/…PosHighlighter/PosCategoryCatalog.cs` (new) | compiles; catalog has 13 unique ids | ✅ done |
| T2 | Capture current `GenerateCss` output as golden test **before** refactor | `tests/…App.Tests/PosHighlighterTests.cs` | test passes on unchanged code | ✅ done |
| T3 | `EnabledCategories` setting + `IsEnabled`; defaults/schema (`x-hidden`) | `PosHighlighterSettings.cs`, `settings.defaults.json`, `settings.schema.json` | JSON round trip test; null ⇒ all | ✅ done |
| T4 | Rewrite `GenerateCss` over catalog, filtering by enabled | `PosDomEnrichmentStage.cs` | T2 golden still passes; new filter tests pass | ✅ done |
| T5 | RPC `getCategories`, `setCategories`, `getCss`; validation of ids; persistence | `PosHighlighterPlugin.cs` | RPC tests pass | ✅ done |
| T6 | SDK: `popover` on toolbar action, scoped `rpc` in `ToolbarActionContext` (fixes inert button) | `sdk/types.ts`, `components/plugins/PluginSlots.tsx` | `tsc -b` passes | ✅ done |
| T7 | SDK: `setReaderStyle`; `injectedStyles` in reader store; apply in `ChapterIframe` on change + load; cleanup on plugin unload | `sdk/index.tsx`, `stores/useReaderStore.ts`, `stores/usePluginStore.ts`, `ChapterIframe.tsx` | `tsc -b` passes | ✅ done |
| T8 | Settings form skips `x-hidden` properties | `components/settings/PluginSettingsTab.tsx` (+ `JsonSchemaProperty` type) | no array field rendered | ✅ done |
| T9 | Plugin UI: `PosMenu` popover, presets, master switch, serialized requests, rollback | `Plugins/…PosHighlighter/Assets/ui.js` | manual scenarios | ✅ done |
| T10 | Full verification: `dotnet build`, `dotnet test`, `npm run build`, `npm run lint`, `--verify-reader` | — | build 0 warnings, 213 tests pass, `tsc -b` + `vite build` pass, no new lint findings. `--verify-reader` / manual UI scenarios **not run** (app was running; WebView not drivable here) | ✅ done (after spec 11) |
| T11 | Docs: update spec 09 §4.3 pointer, `LLM-WIKI.md`, `wiki/dotnet-architecture.md` (plugin toolbar popover + `setReaderStyle`), `wiki/troubleshooting-and-lessons.md` (inert toolbar button), mark this table done | docs | wiki lists new APIs and lesson | ✅ done |

## Risks
- **Refactoring selectors** could silently change colors → T2 golden test first.
- **Live style vs. server style duplicates** → same `<style id>`, client replaces.
- **ui.js is unbundled** → no JSX; keep to `React.createElement`; the file is embedded in the plugin assembly, so a rebuild of the plugin project is needed to see changes.
- **Photino WebView cannot be driven by this agent**: client behavior is verified by type check, lint and build; the manual scenarios in `features/08` still need a human pass in the running app.

## Update: verified end to end after spec 11
The blocker below was fixed by [plan 11](11-plugin-ui-loading-implementation-plan.md). `--verify-ui` now drives the real button and popover (`posMenu=ok(13 categories, live particles-only, persisted, esc closes)`), so T10 is complete.

## Verification result before spec 11 (2026-11-14, app closed)
- `dotnet build` 0 warnings, 213 tests pass, `--verify-reader` OK, `--verify-ui` OK (exit 0).
- **The Highlights menu could not be exercised in the real window.** A new step in `selfTestRunner.ts` drives the real button and popover, but it cannot start: plugin UI bundles fail to load because the app page runs from `file://` and all `ir-app://` fetches fail (pre-existing; see troubleshooting page). The step reports `posMenu=NOT_VERIFIED(...)` and is non-fatal until that is fixed.
- Therefore FR-1…FR-7 are verified only at the backend/RPC/CSS level and by type check/build, not end-to-end.

## Outcome
T1–T9 and T11 done. T10 partial: see its row. Extra work discovered during T9: the two SDK import shims in `AppSchemeHandler.cs` (guard test added) and the broken `ToolbarActionContext.rpc`.
