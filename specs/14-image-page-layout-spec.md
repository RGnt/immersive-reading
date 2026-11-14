# Specification 14: Illustration Pages in Merged Chapters Fill the Reader

## Status: Implemented (2026-11-14)
**Scope:** `ChapterDocumentComposer` (marks illustration parts), `ir-reader-injected.css` (layout of those parts), tests, wiki.
**Plan:** [14-image-page-layout-implementation-plan.md](14-image-page-layout-implementation-plan.md) · **Feature file:** [features/12-image-page-layout.feature](features/12-image-page-layout.feature)

---

## 1. Problem (reported 2026-11-14, reproduced)
Chapter grouping ([spec 12](12-chapter-grouping-spec.md)) puts all spine files of a ToC entry into one document. In a vertical (`vertical-rl`) book the parts are laid out **side by side, right to left**, so a chapter made of six full-page illustrations (Re:ゼロ vol. 1, "口絵 (Illustrations)", 6 files) shows six pages in one row: each image is about 1/6 of the window (15-20 %) and a horizontal scrollbar appears at the bottom. In horizontal mode the pages are stacked but limited to the 800 px text column, not the window width. Before spec 12 each illustration page was its own chapter and filled the reader.

Measured (real chapter via the real pipeline, 1456x768 viewport): vertical = pages 483 px wide (spread 966 px) side by side, total body width 3382 px; horizontal = 800 px wide.

## 2. Requirements
- **FR-1** The composer marks every part that is an *illustration page* with the extra class `ir-image-part`. A part is an illustration page when its body contains `img`/`svg`/`image` and at most `ImageOnlyPolicy.ImagePageMaxChars` visible characters (same definition as spec 13, ruby excluded).
- **FR-2** In vertical mode an illustration part is exactly one reader page: as wide as the visible window and as tall as the text area, its image fitted inside (aspect ratio kept, centered). The reader scrolls page by page (horizontal scrollbar is expected in vertical mode, but each page is full size).
- **FR-3** In horizontal mode an illustration part uses the full window width (not the 800 px text column) and the full window height, image fitted and centered.
- **FR-4** Text parts and single-file chapters are unchanged (single-member chapters keep the old render path byte for byte).
- **FR-5** Works for SVG pages (`<svg width="100%" height="100%" viewBox>` + `<image>`, the usual fixed-layout pattern) and plain `<img>` pages.

## 3. Design
Composer: `CreatePart` adds `ir-image-part` after a cheap check (`QuerySelector("img, svg, image")` first, then `BookContentAnalyzer.CountVisibleChars`). CSS: two custom properties per mode (`--ir-page-w`, `--ir-page-h`) sized from viewport units (not percentages, because publisher wrappers such as `<div class="main">` have no definite size); the part is a centering flex box with those dimensions; `svg`/`img` inside get the same box with `object-fit: contain` / SVG `preserveAspectRatio` (default `meet`). Horizontal mode breaks out of the 800 px column with `margin-inline: calc(50% - 50vw)`.

## 4. Out of scope
Spread detection/splitting, zoom, page-turn animation, changing single-file illustration chapters (cover, title page) which already render correctly, chapter size caps (tracked separately in the wiki: 95-file chapters).

## 5. Test plan
Composer: marks image-only parts, not text parts, not text+image parts, ruby does not count, single-member untouched. Visual: headless Edge screenshots of the real chapter in both modes (image width/height measured by script).
