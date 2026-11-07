# Immersive Reading Workspace

This workspace houses the **Immersive Reading** ecosystem for local, source-linked Japanese reading, dictionary lookups, study-pack generation, and AI-assisted reading comprehension.

---

## Quick Start

### 1. Launch the Desktop Reader & Library Application (.NET 10 + Photino.NET)
```powershell
cd k:\immersive-reading\immersive-reader
dotnet run --project src\ImmersiveReading.App
```

### 2. Run Test Suites & Automated Verifications
```powershell
# Run all .NET 10 xUnit tests (Phases 1–5 Gherkin specifications)
cd k:\immersive-reading\immersive-reader
dotnet test ImmersiveReading.sln

# Run desktop UI & reader verification self-tests
dotnet run --project src\ImmersiveReading.App -- --smoke-test
dotnet run --project src\ImmersiveReading.App -- --verify-reader
dotnet run --project src\ImmersiveReading.App -- --verify-ui

# Run all Python study pipeline tests (134 regression & unit tests)
cd k:\immersive-reading\enrichement-extractor
.venv\Scripts\python.exe -m unittest discover -s tests -v
```

---

## Architecture at a Glance

The ecosystem is divided into two cooperative projects linked by deterministic Book IDs (`book-<16 hex>` derived from `sha256(epub_bytes)[0..16]`):

| Component | Technology | Primary Role |
| :--- | :--- | :--- |
| **[`immersive-reader/`](immersive-reader)** | .NET 10, Photino.NET, SQLite (WAL+FTS5), AngleSharp, MeCab.DotNet | **Interactive Desktop Reader & High-Scale Library (24k+ books)**.<br>• Full-width 2-column layout with Book Info modal, 1-click jiten.moe metadata & difficulty ratings.<br>• Nested virtual folders, tags, inheritable custom fields, multi-select drag-and-drop, and empty folder pruning.<br>• Distraction-free reader with Vim motion navigation (`j`/`k`/`h`/`l`/`w`/`b`/`e`/`m`/`f`/`v`/`gg`/`G`), vertical/horizontal layout, and dynamic furigana modes.<br>• On-the-fly, pre-render `<ruby>` furigana and token span enrichment via custom `ir-book://` scheme.<br>• Sub-5ms indexed SQLite dictionary popover (Yomitan format-3, KANJIDIC2, KanjiVG, rule deinflector).<br>• 1-click Anki sentence mining preserving untranslated story sentences.<br>• AI Companion plugin: zero-spoiler comprehension quizzes and $n+1$ vocabulary-constrained persona chat.<br>• Centralized Global Configuration (`Ctrl+,`) with live CSS theming. |
| **[`enrichement-extractor/`](enrichement-extractor)** | Python >= 3.12, SudachiPy, ReportLab, PyMuPDF | **Offline Batch Study-Pack & Printable PDF Generator**.<br>• 100% offline, deterministic linguistic analysis.<br>• 20-word ordered learning sets (`vocabulary-sets.json`) and kanji context cards.<br>• Printable A4 master and chapter PDFs (`kanji-master.pdf`, `vocabulary-master.pdf`, `grammar-master.pdf`).<br>• Atomic staged releases under `.publication/` with OS-level byte-range leases for zero-race concurrent reader access. |

---

## Documentation & Guides

- **[USERGUIDE.md](USERGUIDE.md)** — **End-User Guide:** Full guide to using the desktop application, library management, Vim motions, 1-click jiten.moe integration, instant dictionary lookup, sentence mining, and settings.
- **[DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)** — **Developer Onboarding:** "Where do I start to work on X?", 5 end-to-end runtime flows, step-by-step recipes for adding features, and how .NET and Python connect.
- **[LLM-WIKI.md](LLM-WIKI.md)** — Master single-file architectural and operational reference, system invariants, contracts, and module catalog.
- **[AGENTS.md](AGENTS.md)** — Rules and hard invariants for AI coding agents working in this workspace.

### 1. .NET + Photino.NET EPUB Library & Reader Specifications (`specs/`)
- **[00 — Master Architecture & Roadmap (`specs/00-master-architecture-and-roadmap.md`)](specs/00-master-architecture-and-roadmap.md)** — .NET 10 + Photino.NET + AngleSharp + MeCab.DotNet system architecture, solution layout, and runnable vertical-slice implementation plan.
- **[01 — EPUB Library Management (`specs/01-epub-library-management-spec.md`)](specs/01-epub-library-management-spec.md)** — `book-<16 hex>` ingestion, hierarchical folders/collections (`Japanese -> Seinen -> Series -> Volumes`), manual tags, SQLite `library.db` schema, reading progress, and `enrichement-extractor` bridge.
- **[02 — Pre-Render AngleSharp Enrichment & Language Plugins (`specs/02-prerender-enrichment-and-plugin-spec.md`)](specs/02-prerender-enrichment-and-plugin-spec.md)** — `ILanguagePlugin` SDK, AngleSharp ruby-aware DOM walker, pre-render `<span class="ir-tok">` & `<ruby>` injection, and `MeCab.DotNet` Japanese plugin.
- **[03 — On-the-Fly Dictionary Lookup Engine (`specs/03-onthefly-dictionary-lookup-spec.md`)](specs/03-onthefly-dictionary-lookup-spec.md)** — Sub-5ms SQLite dictionary engine, Yomitan format-3 importer, longest-prefix + deinflection + EPUB ruby-priority ranking, and word mining.
- **[04 — Photino.NET Reader UI & IPC (`specs/04-photino-reader-ui-and-ipc-spec.md`)](specs/04-photino-reader-ui-and-ipc-spec.md)** — `ir-book://` custom scheme interceptor, JSON-RPC 2.0 IPC protocol, vertical (`vertical-rl`) & horizontal typography, and interactive Popover UI.
- **[05 — Extensibility, AI Comprehension & $n+1$ Persona Chat (`specs/05-extensibility-and-ai-companion-spec.md`)](specs/05-extensibility-and-ai-companion-spec.md)** — General `IFeaturePlugin` architecture, Reading Comprehension Quizzes (MCQ $\rightarrow$ Freeform from just-read text), spoiler-safe $n+1$ Vocabulary-Constrained Persona Chat (verified by `MeCab.DotNet`), and `ILlmProvider` SDK.
- **[06 — Junior Developer Implementation Plan & Task Backlog (`specs/06-junior-developer-implementation-plan.md`)](specs/06-junior-developer-implementation-plan.md)** — Step-by-step developer tickets (`TASK-2.1` through `TASK-5.4`) with exact file paths, algorithms, and verification tests for Phases 2–5.
- **[07 — Global Configuration & Settings System (`specs/07-global-configuration-and-settings-spec.md`)](specs/07-global-configuration-and-settings-spec.md)** — Centralized Settings Screen, `app_settings` SQLite storage, CSS custom properties engine, and live LLM/Directory/Reader updates.
- **Gherkin BDD Feature Suite (`specs/features/`)**:
  - [01-library-ingestion-and-24k-scanning.feature](specs/features/01-library-ingestion-and-24k-scanning.feature)
  - [02-library-hierarchy-tags-and-custom-fields.feature](specs/features/02-library-hierarchy-tags-and-custom-fields.feature)
  - [03-prerender-anglesharp-enrichment-and-plugins.feature](specs/features/03-prerender-anglesharp-enrichment-and-plugins.feature)
  - [04-onthefly-dictionary-lookup-and-vocab.feature](specs/features/04-onthefly-dictionary-lookup-and-vocab.feature)
  - [05-photino-reader-and-study-pack-bridge.feature](specs/features/05-photino-reader-and-study-pack-bridge.feature)
  - [06-ai-comprehension-and-n-plus-one-persona-chat.feature](specs/features/06-ai-comprehension-and-n-plus-one-persona-chat.feature)
  - [07-global-configuration-and-settings.feature](specs/features/07-global-configuration-and-settings.feature)

### 2. `enrichement-extractor` Codebase Wiki (`wiki/`)
1. **[Wiki Home & Glossary (`wiki/index.md`)](wiki/index.md)** — Workspace layout, domain glossary, and cross-reference index.
2. **[Architecture & Dataflow (`wiki/architecture-and-dataflow.md`)](wiki/architecture-and-dataflow.md)** — End-to-end execution stages, `reader` / `study` / `decks` modes, and dependency isolation (`base` vs `print`).
3. **[Module & Symbol Reference (`wiki/module-reference.md`)](wiki/module-reference.md)** — Complete guide to every module, class, function, script, and test suite in `enrichement-extractor`.
4. **[Data Contracts & Schemas (`wiki/data-contracts-and-schemas.md`)](wiki/data-contracts-and-schemas.md)** — JSON contracts (`reader.json`, `lookup.json`, `quality.json`, `study.config.json`, `library.json`), deterministic IDs, and coordinate systems.
5. **[Matching & Linguistics (`wiki/matching-and-linguistics.md`)](wiki/matching-and-linguistics.md)** — EPUB parsing, `<ruby>` extraction, Sudachi split modes A/C, `MatchPolicy`, Yomitan format-3 ingestion, furigana alignment, and ambiguity tracking.
6. **[Publication, Caching & Resources (`wiki/publication-caching-and-resources.md`)](wiki/publication-caching-and-resources.md)** — Atomic `.publication/` releases, OS locks, `ResourceBundle` / `ResourceStore` / `WorkerContext` thread rules, `ContentCache`, CSV rewrite provenance, and GC leases.
7. **[Study Exports & Validation (`wiki/study-exports-and-validation.md`)](wiki/study-exports-and-validation.md)** — `StudyData` preparation, kanji context selection, HTML & PDF rendering, 3-layer validation, and Windows x64 portable packaging.
8. **[Playbooks & Invariants (`wiki/playbooks-and-invariants.md`)](wiki/playbooks-and-invariants.md)** — Hard architectural invariants, step-by-step guides for common tasks, and verification commands.
9. **[Testing & Synthetic Fixtures (`wiki/testing-and-fixtures.md`)](wiki/testing-and-fixtures.md)** — How the 19 self-contained test suites and `fixture()` helper work.
10. **[Roadmap & Open Workstreams (`wiki/roadmap-and-open-work.md`)](wiki/roadmap-and-open-work.md)** — Completed refactoring phases, DOM source mapping, lookup API, and known linguistic edge cases.

---

## Repository Structure

```text
k:/immersive-reading/
├── README.md                        # Workspace entry point
├── DEVELOPER_GUIDE.md               # Quick-jump guide & 5 core runtime flows for developers
├── LLM-WIKI.md                      # Master single-file LLM Wiki reference
├── AGENTS.md                        # AI coding agent rules and invariants
├── specs/                           # .NET 10 reader specifications & Gherkin features
│   ├── 00-master-architecture-and-roadmap.md
│   ├── 01-epub-library-management-spec.md
│   ├── 02-prerender-enrichment-and-plugin-spec.md
│   ├── 03-onthefly-dictionary-lookup-spec.md
│   ├── 04-photino-reader-ui-and-ipc-spec.md
│   ├── 05-extensibility-and-ai-companion-spec.md
│   ├── 06-junior-developer-implementation-plan.md
│   └── features/                    # 6 Gherkin BDD feature specifications
├── wiki/                            # Modular LLM Wiki topic pages (10 chapters)
├── immersive-reader/                # .NET 10 + Photino.NET + AngleSharp + MeCab.DotNet Desktop EPUB Library & Reader
│   ├── ImmersiveReading.sln
│   ├── Directory.Build.props
│   ├── global.json
│   ├── .library-data/               # Runtime SQLite databases (library.db, dictionaries.db) & covers
│   ├── src/
│   │   ├── ImmersiveReading.Core/            # Contracts, domain models, plugin interfaces
│   │   ├── ImmersiveReading.Library/         # LibraryManager, SQLite, EPUB reader, flashcards, Python bridge
│   │   ├── ImmersiveReading.Enrichment/      # AngleSharpDomEnricher, BookSchemeHandler, cache, context slicing
│   │   ├── ImmersiveReading.Dictionaries/    # DictionaryLookupEngine, deinflector, Yomitan/Kanji importers
│   │   ├── Plugins/
│   │   │   ├── ImmersiveReading.Plugins.Japanese/     # MeCab.DotNet morphological analyzer & furigana aligner
│   │   │   └── ImmersiveReading.Plugins.AiCompanion/  # Progressive quiz, n+1 persona chat, LLM provider
│   │   └── ImmersiveReading.App/             # Photino host, LibraryRpcDispatcher, local asset server, wwwroot/
│   └── tests/
│       ├── ImmersiveReading.Core.Tests/
│       ├── ImmersiveReading.Enrichment.Tests/
│       └── ImmersiveReading.Dictionaries.Tests/
└── enrichement-extractor/           # Offline Japanese EPUB enrichment & study-pack pipeline
    ├── pyproject.toml               # Package metadata (immersive-reading v0.3.0)
    ├── uv.lock                      # Frozen reproducible dependency lockfile
    ├── immersive/                   # Core Python package (37 modules + schemas/assets)
    ├── scripts/                     # CLI wrappers and portable distribution builder
    ├── tests/                       # 19 unittest regression & integration test modules
    ├── docs/                        # Feature design & verification notes
    ├── data/                        # Local dictionaries, KanjiVG, Kanji alive, fonts, charts
    ├── books/                       # Input book folders (<book>/*.epub, *.csv, study.config.json)
    └── output/                      # Published releases (.publication/) and compatibility views
```
