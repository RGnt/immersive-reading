# Implementation Plan 14: Illustration Page Layout

Spec: [14-image-page-layout-spec.md](14-image-page-layout-spec.md).

| # | Task | Files | Done when | Status |
| :-- | :--- | :--- | :--- | :--- |
| T1 | Composer adds `ir-image-part` to illustration parts | `Enrichment/Dom/ChapterDocumentComposer.cs` | unit tests pass | ✅ done |
| T2 | Composer tests (image-only, text, text+image, ruby, single-member unchanged) | `tests/Enrichment.Tests/ChapterGroupingRenderTests.cs` | pass | ✅ done |
| T3 | CSS layout for `section.ir-image-part` in both writing modes | `App/wwwroot/assets/ir-reader-injected.css` | screenshots/measurements of the real chapter show full-size pages | ✅ done |
| T4 | Guard test that the CSS keeps the `ir-image-part` rules (css is shipped as a plain asset) | `tests/App.Tests` (existing asset guards) | pass | ✅ done |
| T5 | Wiki: lessons entry, architecture section 8 note, spec status | `wiki/*`, `LLM-WIKI.md` | consistent | ✅ done |
| T6 | Full verification: build, all tests, `--verify-reader` | — | green | ✅ done |

## Risks
- A text-with-illustration part is not matched (more than 50 chars) and stays in flow: intended.
- `100vw` includes the scrollbar in horizontal mode: `overflow-x: hidden` on the html element in that mode.

## Outcome (2026-11-14)
Measured on the real "口絵 (Illustrations)" chapter of Re:ゼロ vol. 1 (6 files) through the real pipeline, 1456x768 viewport, reader override CSS applied: **before** vertical = pages 483 px wide (spread 966 px), side by side in a 3,382 px row; horizontal = 800 px wide. **After** vertical = every page 1376x728 (window width minus margins, full text-area height); horizontal = every page 1456x708 (window width). 278 .NET tests pass (4 new). Not visually verified in the running app (it holds the build output; restart it to see the change).
