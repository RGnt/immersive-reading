# Immersive Reading

An offline-first **EPUB library manager, Japanese reader, and study-pack ecosystem** combining a native **.NET 10 + Photino.NET** desktop application, an **offline deterministic Python study-pack & PDF generator**, and an optional **spoiler-gated character knowledge graph** builder.

**Jump to what you need:**
1. [Using the App (End Users)](#1-using-the-app-end-users) — Download, launch, import books/dictionaries, and start reading.
2. [Building from Source (Self-Hosting & Packaging)](#2-building-from-source-self-hosting--packaging) — Prerequisites, compiling the app & frontend, and building portable releases.
3. [Developer Information](#3-developer-information) — Architecture, repository layout, test suites, self-tests, and technical docs.

---

## 1. Using the App (End Users)

If you just want to download and use **Immersive Reading**, grab a pre-compiled release for your platform from **GitHub Releases**. Every release is self-contained with all required runtime files included — **you do not need the `.NET` SDK, `Node.js`, or any build tools**.

### Available Release Packages
* **Windows (64-bit):** `immersive-reader-*-win-x64.zip`
  * *Requires:* Windows 10 (1809+) or Windows 11 with Microsoft Edge WebView2 Runtime (already installed on modern Windows).
* **macOS (Apple Silicon):** `immersive-reader-*-osx-arm64.tar.gz`
* **Linux (64-bit):** `immersive-reader-*-linux-x64.tar.gz`
  * *Requires:* WebKitGTK (standard on most desktop distributions).

### How to Run
1. Download the archive for your platform from **Releases** and extract it to any folder (for example, `Documents/ImmersiveReading`).
2. Launch the application:
   * **Windows:** Double-click **`ImmersiveReading.App.exe`** (or **`START-READER.bat`** if using a local portable build).
   * **macOS:** Double-click **`START-READER.command`** (or run `./ImmersiveReading.App` from Terminal).
   * **Linux:** Run `./ImmersiveReading.App` inside the extracted `immersive-reader/` folder.

### First-Time Setup & Core Features
1. **Import Books:** Drag and drop `.epub` files directly onto the library window, or use **Scan Folder** to index large collections (tested for 24,000+ books).
2. **Import Dictionaries (`Ctrl+,`):** Open **Settings** -> **Dictionaries** to import local **Yomitan** (format-3 `.zip`), **KANJIDIC2**, and **KanjiVG** stroke-order resources for sub-5ms in-reader popovers.
3. **Read & Navigate:**
   * Toggle **Vertical (`vertical-rl`)** / **Horizontal** text with `v`.
   * Cycle **Furigana** modes (`All` / `Uncommon` / `Author Only` / `Hover` / `Off`) with `f`.
   * Use **Vim-style motions** (`j`/`k`/`h`/`l`/`w`/`b`/`e`, `gg`/`G`), search (`Ctrl+F`), and bookmarks (`Ctrl+B`). Press **`?`** anytime in the reader for the full shortcut cheat sheet.
   * Mark word learning states (`1`–`4`) and mine sentences to **Anki** (`m`, requires AnkiConnect running locally).
4. **Optional Integrations (Configurable in `Ctrl+,` Settings):**
   * **AI Companion & Character Knowledge:** Connect a local OpenAI-compatible LLM server (such as Ollama or FreeToken) for spoiler-free comprehension quizzes, $n+1$ persona chat, and character/story knowledge graphs.
   * **Offline Study Packs & Printable A4 PDFs:** If you have Python 3.12+ configured, generate chapter/master vocabulary, kanji, and grammar study packs and printable A4 PDFs directly from the app.
5. **Your Data:** All library metadata, reading progress, vocabulary states, and covers are stored locally in `.library-data/` alongside the app (or your configured data directory).

> For a full walkthrough of every screen, shortcut, and feature, see the **[User Guide (`USERGUIDE.md`)](USERGUIDE.md)**.

---

## 2. Building from Source (Self-Hosting & Packaging)

Follow these steps if you want to clone the repository, compile the application yourself, or package a portable release.

### Prerequisites
* **[.NET 10 SDK](https://dotnet.microsoft.com/)** (`10.0.302` or compatible, see [`immersive-reader/global.json`](immersive-reader/global.json))
* **[Node.js](https://nodejs.org/) (LTS 22+) & npm** — required to compile the React 19 + TypeScript + Vite frontend and plugin UI bundles
* **Optional — Python >= 3.12** (with [`uv`](https://docs.astral.sh/uv/) or `pip`) — only needed if you want to run the offline study-pack/PDF generator ([`enrichement-extractor/`](enrichement-extractor)) or the character knowledge graph builder ([`knowledge-graph/`](knowledge-graph))

### Clone & Build the Desktop Application
```powershell
git clone <repository-url>
cd immersive-reading

# 1. Install frontend dependencies and build plugin UIs + React client
cd immersive-reader\src\ImmersiveReading.App\client
npm ci
npm run build:plugins
npm run build

# 2. Build and run the .NET 10 desktop application
cd ..\..\..
dotnet run --project src\ImmersiveReading.App
```

### Build a Portable Self-Contained Release
* **Windows x64 (automated packager script):**
  ```powershell
  .\scripts\package_windows.ps1
  ```
  Produces a self-contained portable folder in `dist/ImmersiveReading-win-x64/` and a ready-to-share archive at `dist/ImmersiveReading-win-x64.zip` (and verifies the binary with `--verify-reader`). Pass `-SkipVerify` to skip the post-publish smoke check.

* **Manual `dotnet publish` (Windows / Linux / macOS):**
  Ensure the React client is built first (`npm run build`), then publish for your target RID (`win-x64`, `linux-x64`, or `osx-arm64`):
  ```powershell
  dotnet publish immersive-reader/src/ImmersiveReading.App/ImmersiveReading.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:BuildReactClient=false -o ./dist/immersive-reader
  ```

### Optional: Set Up Python Subprojects
* **Offline Study-Pack & PDF Pipeline (`enrichement-extractor/`):**
  ```powershell
  cd enrichement-extractor
  uv venv --python 3.12 .venv
  # Base (reader JSON + HTML companion):
  uv pip install --python .venv\Scripts\python.exe -e .
  # With printable A4 PDF support (reportlab, pymupdf, fonttools, pillow):
  uv pip install --python .venv\Scripts\python.exe -e ".[print]"
  ```
* **Character Knowledge Graph (`knowledge-graph/`):**
  ```powershell
  cd knowledge-graph
  uv venv --python 3.12 .venv
  uv pip install --python .venv\Scripts\python.exe -e .
  ```

---

## 3. Developer Information

### Architecture at a Glance
The workspace is divided into three cooperative components linked by deterministic Book IDs (`book-<16 hex>` derived from `sha256(epub_bytes)[0..16]`):

| Component | Stack | Role |
| :--- | :--- | :--- |
| **[`immersive-reader/`](immersive-reader)** | .NET 10, Photino.NET, React 19, TS, Tailwind, SQLite (WAL + FTS5), AngleSharp, MeCab.DotNet | Interactive desktop reader, high-scale library manager, custom `ir-book://` pre-render DOM enrichment, sub-5ms dictionary lookup, AnkiConnect mining, and modular plugin host (`IImmersivePlugin` + `@ir/sdk` UI slots). |
| **[`enrichement-extractor/`](enrichement-extractor)** | Python >= 3.12, SudachiPy, ReportLab, PyMuPDF | **100% offline, zero-LLM** deterministic EPUB analysis, 20-word learning sets, kanji context, HTML companion, and A4 PDF generation with atomic `.publication/` staged releases. |
| **[`knowledge-graph/`](knowledge-graph)** | Python >= 3.12, NetworkX | **Separate LLM-assisted project** that builds spoiler-gated character, relationship, event, and item graphs per series/chapter via an OpenAI-compatible endpoint. Never imported by `enrichement-extractor/`. |

### Repository Structure
```text
immersive-reading/
├── README.md                          # Workspace overview (this file)
├── USERGUIDE.md                       # End-user manual
├── DEVELOPER_GUIDE.md                 # Developer onboarding, runtime flows, and quick-jump index
├── LLM-WIKI.md                        # Master architectural reference & invariants
├── AGENTS.md                          # Rules and hard invariants for AI coding agents
├── scripts/
│   └── package_windows.ps1            # Self-contained Windows x64 release packager
├── specs/                             # Architecture specs, plugin specs, and Gherkin BDD features
├── wiki/                              # Modular topic pages for enrichement-extractor & .NET architecture
├── immersive-reader/                  # .NET 10 + Photino.NET Desktop Application
│   ├── ImmersiveReading.sln
│   ├── global.json
│   ├── src/
│   │   ├── ImmersiveReading.Core/         # Abstractions, domain models, EPUB utilities, plugin contracts
│   │   ├── ImmersiveReading.Library/      # LibraryManager, SQLite, metadata/bulk edits, flashcards, Python bridge
│   │   ├── ImmersiveReading.Enrichment/   # AngleSharpDomEnricher, BookSchemeHandler, chapter token cache
│   │   ├── ImmersiveReading.Dictionaries/ # DictionaryLookupEngine, deinflection, Yomitan/Kanji importers
│   │   ├── ImmersiveReading.Knowledge/    # KnowledgeHub & user memory source
│   │   ├── ImmersiveReading.App/          # Photino host, DI composition, JSON-RPC dispatcher, React client (client/)
│   │   └── Plugins/
│   │       ├── ImmersiveReading.Plugins.Japanese/           # MeCab tokenizer, ruby aligner, Python stages
│   │       ├── ImmersiveReading.Plugins.AiCompanion/        # Spoiler-free quizzes & n+1 persona chat
│   │       ├── ImmersiveReading.Plugins.CharacterKnowledge/ # In-app character/story knowledge browser & builder
│   │       ├── ImmersiveReading.Plugins.PosHighlighter/     # Part-of-speech DOM highlighter & color palettes
│   │       ├── ImmersiveReading.Plugins.DeckBuilder/        # Deck building plugin
│   │       └── ImmersiveReading.Plugins.JitenMoe/           # 1-click jiten.moe metadata & difficulty ratings
│   └── tests/                         # xUnit test projects (Core, Dictionaries, Enrichment, App)
├── enrichement-extractor/             # Offline deterministic Python study-pack & PDF pipeline
│   ├── pyproject.toml
│   ├── immersive/                     # Core package modules, schemas, and pipeline runner
│   └── tests/                         # unittest regression & integration suite
└── knowledge-graph/                   # Spoiler-gated character & story knowledge graph builder
    ├── pyproject.toml
    ├── immersive_knowledge/
    └── tests/
```

### Running Tests & Automated Verifications
```powershell
# 1. .NET 10 xUnit test suite
cd immersive-reader
dotnet test ImmersiveReading.sln

# 2. Desktop headless / Photino self-tests
dotnet run --project src\ImmersiveReading.App -- --smoke-test
dotnet run --project src\ImmersiveReading.App -- --verify-reader
dotnet run --project src\ImmersiveReading.App -- --verify-ui

# 3. Frontend typecheck, build & lint
cd src\ImmersiveReading.App\client
npm run lint
npm run build

# 4. Python study pipeline tests
cd ..\..\..\..\enrichement-extractor
.venv\Scripts\python.exe -m unittest discover -s tests -v

# 5. Python knowledge-graph tests
cd ..\knowledge-graph
.venv\Scripts\python.exe -m unittest discover -s tests -v
```

### Developer Documentation
* **[`DEVELOPER_GUIDE.md`](DEVELOPER_GUIDE.md)** — Onboarding, runtime flows, and "Where do I start to work on X?"
* **[`wiki/creating-a-plugin-tutorial.md`](wiki/creating-a-plugin-tutorial.md)** — Beginner-friendly step-by-step tutorial on creating plugins (C#, Python, and UI) based on `PosHighlighter`.
* **[`LLM-WIKI.md`](LLM-WIKI.md)** — Master architectural reference, invariants, and module catalog.
* **[`AGENTS.md`](AGENTS.md)** — Hard architectural invariants and workspace rules.
* **[`specs/`](specs/)** — Feature specifications (`00`–`26+`) and Gherkin BDD `.feature` files.
* **[`wiki/index.md`](wiki/index.md)** — Modular wiki chapters covering data contracts, linguistics, caching, .NET architecture, plugin UI authoring, and troubleshooting.
* **[`knowledge-graph/README.md`](knowledge-graph/README.md)** — CLI usage, config schema, and exit codes for the character knowledge graph builder.
