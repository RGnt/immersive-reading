# Implementation Plan 11: Plugin UI Loading

Spec: [11-plugin-ui-loading-spec.md](11-plugin-ui-loading-spec.md). Work in order; each task ends with a check.

| # | Task | Files | Done when | Status |
| :-- | :--- | :--- | :--- | :--- |
| T1 | `extensions.getAsset` RPC + path/extension/size guards | `Rpc/Modules/ExtensionsRpcModule.cs` (+ helper in `PluginHostManager` if needed) | C# tests: ok / `..` / rooted / `.dll` / unknown plugin / missing | ✅ done |
| T2 | Pure module `pluginImportRewriter.ts` (`rewriteBareImports`, supported-specifier list, error type) | `client/src/services/pluginImportRewriter.ts` | self-check cases below pass in `--verify-ui` | ✅ done |
| T3 | `pluginModuleLoader.ts`: shim generation from `__IR_RUNTIME__`, blob load, style load | `client/src/services/pluginModuleLoader.ts` | `tsc -b` | ✅ done |
| T4 | Store: use loader + `uiEntry`/`styleEntry`, `uiErrors`, style cleanup on unload | `stores/usePluginStore.ts`, `sdk/types.ts` | `tsc -b` | ✅ done |
| T5 | Settings plugin card shows `uiErrors` | `components/settings/PluginSettingsTab.tsx` | visible in code path; checked by self-test error text | ✅ done |
| T6 | Remove import map, `ir-app://sdk` route, `GetSdkShim`, obsolete shim tests; keep `ir-app://plugins` route | `client/index.html`, `AppSchemeHandler.cs`, `ProgramSchemeTests.cs` | build + tests green | ✅ done |
| T7 | Self-test: rewriter cases, load POS + AiCompanion UIs (assert registrations), make spec-10 menu step fatal; restore state | `services/selfTestRunner.ts` | `--verify-ui` passes with real UI scenario | ✅ done |
| T8 | Docs: `wiki/plugin-ui-authoring.md` (new), update `dotnet-architecture.md` §7, lessons (mark fixed), spec 10 notes, LLM-WIKI, index | wiki/specs | all link-consistent | ✅ done |
| T9 | Final verification: build 0 warnings, all tests, `tsc -b`, `vite build`, lint no new findings, `--verify-reader`, `--verify-ui` | — | all green; results recorded below | ✅ done |

## T2 self-check cases (`rewriteBareImports`)
1. `import React, { useState } from 'react';` → specifier replaced, names untouched
2. `import {a as b} from "lucide-react"` (double quotes)
3. `export { X } from '@ir/sdk'`
4. `import 'react'` (side-effect)
5. `const m = await import('react-dom')`
6. a string/comment containing `from 'react'` text inside a template literal is *not* guaranteed untouched (documented limitation); relative `./x.js` → throws `UnsupportedImportError`
7. unsupported bare `left-pad` → throws with plugin id + specifier

## Risks
- **Regex rewriting** can miss exotic import syntax → mitigated by clear error for anything left bare, and by `--verify-ui` covering both first-party bundles.
- **Blob modules under a future CSP** → fatal verify step (FR-7).
- **AiCompanion `ui.js` is a large prebuilt bundle**: loading it for the first time may expose latent bugs unrelated to loading; record and fix or file them, do not hide them.
- **`--verify-ui` enables/disables plugins**: it must restore the user's prior state (already done for POS; extend to AiCompanion).

## Outcome (2026-11-14)
All tasks done. Verification: `dotnet build` 0 warnings; 223 tests pass (Dictionaries 15, Enrichment 36, Core 87, App 86 incl. 11 new/changed for spec 10/11); `tsc -b` and `vite build` pass; lint shows only the pre-existing `sdk/index.tsx` error; `--verify-reader` OK; `--verify-ui` exit 0 with `pluginUi=ok(...)` and `posMenu=ok(...)` in the real window.
Added beyond the plan: JSX build script for plugin UIs (AiCompanion's `ui.js` was uncompiled JSX), JSX/imports guard tests.
