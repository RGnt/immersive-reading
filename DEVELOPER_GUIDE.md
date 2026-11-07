# Developer Onboarding & Architecture Guide (`immersive-reading`)

Welcome to the **Immersive Reading** repository! If you just opened this workspace and want to understand **how the pieces fit together** or **jump straight into modifying a specific subsystem**, this guide is for you.

---

## 1. Quick-Find: "I Want to Work On... Where Do I Start?"

Use this table to jump directly to the files responsible for the area you want to change:

| What You Want to Work On | Primary Entry Files | Key Classes / Functions | Tests to Run |
| :--- | :--- | :--- | :--- |
| **Desktop UI (Library Grid, Reader Controls, Popover, Drawers)** | [`index.html`](immersive-reader/src/ImmersiveReading.App/wwwroot/index.html)<br>[`app.js`](immersive-reader/src/ImmersiveReading.App/wwwroot/app.js)<br>[`styles.css`](immersive-reader/src/ImmersiveReading.App/wwwroot/styles.css) | `rpcCall()`, `openBookInReader()`, `showDictPopover()`, `openStudyPackDrawer()`, `openAiDrawer()` | `dotnet run --project immersive-reader/src/ImmersiveReading.App -- --verify-ui` |
| **JSON-RPC Bridge (Adding/Changing Frontend $\leftrightarrow$ C# APIs)** | [`LibraryRpcDispatcher.cs`](immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs)<br>[`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs) | [`LibraryRpcDispatcher.HandleMethodAsync`](immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs#L93-L550) | [`Phase1LibrarySpecsTests.cs`](immersive-reader/tests/ImmersiveReading.Core.Tests/Phase1LibrarySpecsTests.cs) |
| **EPUB Library, 24k+ Scanner, Folders, Tags & Custom Fields** | [`LibraryManager.cs`](immersive-reader/src/ImmersiveReading.Library/LibraryManager.cs)<br>[`LibraryDatabase.cs`](immersive-reader/src/ImmersiveReading.Library/LibraryDatabase.cs)<br>[`EpubMetadataExtractor.cs`](immersive-reader/src/ImmersiveReading.Core/Epub/EpubMetadataExtractor.cs) | [`LibraryManager.ScanDirectoryAsync`](immersive-reader/src/ImmersiveReading.Library/LibraryManager.cs), [`LibraryManager.ListBooks`](immersive-reader/src/ImmersiveReading.Library/LibraryManager.cs), [`LibraryDatabase.InitializeSchema`](immersive-reader/src/ImmersiveReading.Library/LibraryDatabase.cs) | [`Phase1LibrarySpecsTests.cs`](immersive-reader/tests/ImmersiveReading.Core.Tests/Phase1LibrarySpecsTests.cs) |
| **EPUB Chapter Rendering, `<ruby>` Furigana & Token Spans** | [`BookSchemeHandler.cs`](immersive-reader/src/ImmersiveReading.Enrichment/BookSchemeHandler.cs)<br>[`AngleSharpDomEnricher.cs`](immersive-reader/src/ImmersiveReading.Enrichment/AngleSharpDomEnricher.cs)<br>[`ChapterEnrichmentCache.cs`](immersive-reader/src/ImmersiveReading.Enrichment/ChapterEnrichmentCache.cs) | [`BookSchemeHandler.ResolveAsync`](immersive-reader/src/ImmersiveReading.Enrichment/BookSchemeHandler.cs), [`AngleSharpDomEnricher.EnrichChapterAsync`](immersive-reader/src/ImmersiveReading.Enrichment/AngleSharpDomEnricher.cs) | [`Phase2EnrichmentSpecsTests.cs`](immersive-reader/tests/ImmersiveReading.Enrichment.Tests/Phase2EnrichmentSpecsTests.cs) |
| **Japanese Morphological Analysis (`MeCab.DotNet`) or Adding a Language** | [`LanguagePluginContracts.cs`](immersive-reader/src/ImmersiveReading.Core/Plugins/LanguagePluginContracts.cs)<br>[`JapaneseLanguagePlugin.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseLanguagePlugin.cs) | [`ILanguagePlugin`](immersive-reader/src/ImmersiveReading.Core/Plugins/LanguagePluginContracts.cs), [`JapaneseLanguagePlugin.AnalyzeParagraphsAsync`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseLanguagePlugin.cs) | [`Phase2EnrichmentSpecsTests.cs`](immersive-reader/tests/ImmersiveReading.Enrichment.Tests/Phase2EnrichmentSpecsTests.cs) |
| **Instant Dictionary Lookup, Deinflection & Word Status (`Known`/`Learning`)** | [`DictionaryLookupEngine.cs`](immersive-reader/src/ImmersiveReading.Dictionaries/DictionaryLookupEngine.cs)<br>[`JapaneseDeinflector.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseDeinflector.cs)<br>[`YomitanSqliteImporter.cs`](immersive-reader/src/ImmersiveReading.Dictionaries/YomitanSqliteImporter.cs)<br>[`VocabularyTracker.cs`](immersive-reader/src/ImmersiveReading.Library/VocabularyTracker.cs) | [`DictionaryLookupEngine.LookupAsync`](immersive-reader/src/ImmersiveReading.Dictionaries/DictionaryLookupEngine.cs), [`JapaneseDeinflector.Deinflect`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseDeinflector.cs) | [`Phase3DictionarySpecsTests.cs`](immersive-reader/tests/ImmersiveReading.Dictionaries.Tests/Phase3DictionarySpecsTests.cs) |
| **1-Click Sentence Flashcards & Python Study Pack Bridge** | [`FlashcardMiningService.cs`](immersive-reader/src/ImmersiveReading.Library/FlashcardMiningService.cs)<br>[`PythonStudyPackBridge.cs`](immersive-reader/src/ImmersiveReading.Library/PythonStudyPackBridge.cs) | [`FlashcardMiningService.MineCard`](immersive-reader/src/ImmersiveReading.Library/FlashcardMiningService.cs), [`PythonStudyPackBridge.AcquireSnapshotLease`](immersive-reader/src/ImmersiveReading.Library/PythonStudyPackBridge.cs) | [`Phase4StudyPackSpecsTests.cs`](immersive-reader/tests/ImmersiveReading.Core.Tests/Phase4StudyPackSpecsTests.cs) |
| **AI Comprehension Quizzes, $n+1$ Persona Chat & Feature Plugins** | [`FeaturePluginContracts.cs`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs)<br>[`ReaderContextService.cs`](immersive-reader/src/ImmersiveReading.Enrichment/ReaderContextService.cs)<br>[`SnippetEnricher.cs`](immersive-reader/src/ImmersiveReading.Enrichment/SnippetEnricher.cs)<br>[`AiCompanionPlugin.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/AiCompanionPlugin.cs)<br>[`ComprehensionQuizService.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/ComprehensionQuizService.cs)<br>[`PersonaChatService.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/PersonaChatService.cs) | [`IFeaturePlugin`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs), [`ReaderContextService.GetJustReadPassageAsync`](immersive-reader/src/ImmersiveReading.Enrichment/ReaderContextService.cs), [`PersonaChatService.SendMessageAsync`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/PersonaChatService.cs) | [`Phase5AiCompanionSpecsTests.cs`](immersive-reader/tests/ImmersiveReading.Enrichment.Tests/Phase5AiCompanionSpecsTests.cs) |
| **Offline Python Study Pack & Printable A4 PDF Pipeline** | [`enrichment.py`](enrichement-extractor/immersive/enrichment.py)<br>[`build.py`](enrichement-extractor/immersive/build.py)<br>[`study_data.py`](enrichement-extractor/immersive/study_data.py)<br>[`kanji_pdf.py`](enrichement-extractor/immersive/kanji_pdf.py)<br>[`publication.py`](enrichement-extractor/immersive/publication.py) | [`enrich_book()`](enrichement-extractor/immersive/enrichment.py#L22-L70), [`build_data()`](enrichement-extractor/immersive/build.py#L79-L351), [`prepare_study_data()`](enrichement-extractor/immersive/study_data.py#L51-L77) | `cd enrichement-extractor && .venv\Scripts\python.exe -m unittest discover -s tests -v` |

---

## 2. High-Level System Architecture: How the Two Halves Connect

The workspace is split into two top-level directories that cooperate seamlessly through deterministic IDs (`book-<16 hex>`) and atomic file/lock contracts:

1. **[`immersive-reader/`](immersive-reader) (.NET 10 + Photino.NET Desktop Application)**
   - **Purpose**: High-scale EPUB library management (24,000+ books), real-time pre-render EPUB enrichment (`AngleSharp` + `MeCab.DotNet`), sub-5ms SQLite dictionary popovers, flashcard mining, and the AI Comprehension & $n+1$ Persona Chat companion.
   - **Zero pre-processing required to read**: You can drop any raw `.epub` into the library and open it immediately; chapters are enriched in-memory on the fly in ~15–40 ms when navigated to.
2. **[`enrichement-extractor/`](enrichement-extractor) (Python 3.12 Batch Study-Pack & Printable PDF Generator)**
   - **Purpose**: Offline, whole-book linguistic analysis (`SudachiPy`), 20-word ordered learning sets (`vocabulary-sets.json`), kanji context cards (`kanji-context.json`), companion HTML sites, and printable A4 study sheets (`kanji-master.pdf`, `vocabulary-master.pdf`, `grammar-master.pdf`, `chapter-XX-*.pdf`).

```mermaid
flowchart TB
    subgraph Desktop["immersive-reader/ (.NET 10 + Photino.NET Desktop App)"]
        UI["Photino WebView UI\n(wwwroot/index.html, app.js, styles.css)"]
        RPC["LibraryRpcDispatcher\n(JSON-RPC 2.0 over Photino IPC)"]
        SCHEME["ir-book:// Scheme Interceptor\n(BookSchemeHandler + AngleSharpDomEnricher)"]
        MECAB["JapaneseLanguagePlugin\n(MeCab.DotNet + Katakana->Hiragana)"]
        CACHE["ChapterEnrichmentCache\n(In-Memory RichToken & Sentence Index)"]
        LIB["LibraryManager + LibraryDatabase\n(.library-data/library.db: Books, FTS5, Folders,\nTags, Custom Fields, Vocab Status, AI Sessions)"]
        DICT["DictionaryLookupEngine + JapaneseDeinflector\n(.library-data/dictionaries.db: Yomitan, KANJIDIC2)"]
        BRIDGE["PythonStudyPackBridge + FlashcardMiningService"]
        PLUGIN["FeaturePluginHost + AiCompanionPlugin\n(ReaderContextService + SnippetEnricher\n+ ComprehensionQuizService + PersonaChatService)"]
    end

    subgraph Python["enrichement-extractor/ (Python 3.12 Batch Study Pipeline)"]
        PYCLI["immersive.cli (build | enrich | decks)"]
        PUB["output/.publication/\nactive.json + leases/<gen>/<uuid>.lock"]
        OUT["output/data/<book-id>/ (reader.json, vocabulary-sets.json)\noutput/pdf/<book-id>/ (*.pdf)\noutput/data/flashcards/ (library.json, mined-anki.tsv)"]
    end

    UI <-->|"window.external.sendMessage\n(JSON-RPC 2.0)"| RPC
    UI <-->|"iframe src='ir-book://{bookId}/{href}'\n+ /ir-local-pdf/ & /ir-local-cover/"| SCHEME
    SCHEME --> MECAB
    SCHEME --> CACHE
    RPC --> LIB
    RPC --> DICT
    DICT --> CACHE
    DICT --> LIB
    RPC --> BRIDGE
    RPC --> PLUGIN
    PLUGIN --> SCHEME
    PLUGIN --> MECAB
    PLUGIN --> LIB
    BRIDGE <-->|"Byte-0 OS Lock Lease\n+ CLI Subprocess"| PUB
    BRIDGE <--> OUT
    PYCLI --> PUB --> OUT
```

### How `.NET` and `Python` Share Identity & Data
- **Shared Deterministic Book ID (`book-<16 hex>`)**:
  Both [`BookIdentity.ComputeBookId`](immersive-reader/src/ImmersiveReading.Core/Epub/BookIdentity.cs) in C# and [`uid("book", sha256(epub_bytes).hexdigest())`](enrichement-extractor/immersive/build.py#L96) in Python compute the exact same ID: `"book-" + sha256(raw_epub_bytes)[0..16].ToLowerInvariant()`.
- **Shared Publication & Lease Protocol (`output/.publication/`)**:
  When Python publishes a study pack, it writes an immutable snapshot to `enrichement-extractor/output/.publication/releases/<32-hex-generation>/` and updates `output/.publication/active.json`.
  When the .NET reader opens a book's Study Pack drawer, [`PythonStudyPackBridge.AcquireSnapshotLease`](immersive-reader/src/ImmersiveReading.Library/PythonStudyPackBridge.cs#L152-L173) creates `.publication/leases/<generation>/<uuid>.lock` and locks byte `0` (`FileStream.Lock(0, 1)`). Python's garbage collector ([`retention.py`](enrichement-extractor/immersive/retention.py#L66-L78)) probes byte `0` before deleting old releases, guaranteeing zero race conditions while PDFs or JSON sets are open in the reader.

---

## 3. Solution Layout (`immersive-reader/`)

Inside [`immersive-reader/ImmersiveReading.sln`](immersive-reader/ImmersiveReading.sln), dependencies flow strictly downward (no circular references):

```text
immersive-reader/
├── ImmersiveReading.sln
├── .library-data/                                    # Runtime SQLite databases & extracted covers
│   ├── library.db                                    # User library, hierarchy, vocab states, AI sessions
│   ├── dictionaries.db                               # Imported Yomitan & KANJIDIC2 dictionaries
│   └── covers/                                       # Extracted EPUB cover thumbnails (<book-id>.jpg)
├── src/
│   ├── ImmersiveReading.Core/                        # Zero-dependency contracts & domain records
│   │   ├── Library/                                  # BookRecord, FolderNode, CustomFieldDefinition, etc.
│   │   ├── Plugins/LanguagePluginContracts.cs        # ILanguagePlugin, RichToken, EnrichedParagraph
│   │   └── Extensions/FeaturePluginContracts.cs      # IFeaturePlugin, FeaturePluginHost, ILlmProvider
│   ├── ImmersiveReading.Library/                     # SQLite library, EPUB parser, Python bridge, mining
│   │   ├── LibraryDatabase.cs                        # SQLite WAL schema & migrations
│   │   ├── LibraryManager.cs                         # CRUD, 2-stage batch scanner, FTS5 search, custom fields
│   │   ├── EpubArchiveReader.cs                      # ZIP/OPF/NCX/Navigation & cover parser
│   │   ├── PythonStudyPackBridge.cs                  # .publication/active.json reader, OS lease lock, CLI runner
│   │   └── FlashcardMiningService.cs                 # Atomic library.json + mined-anki.tsv card exporter
│   ├── ImmersiveReading.Enrichment/                  # Real-time AngleSharp XHTML enrichment & context slicing
│   │   ├── AngleSharpDomEnricher.cs                  # Ruby-aware DOM walker, <span class="ir-tok"> injector
│   │   ├── BookSchemeHandler.cs                      # Resolves ir-book://{bookId}/{path} requests
│   │   ├── ChapterEnrichmentCache.cs                 # LRU cache of enriched chapters & synthetic snippets
│   │   ├── ReaderContextService.cs                   # Zero-spoiler text/token slicer up to current progress
│   │   └── SnippetEnricher.cs                        # Enriches AI quiz/chat HTML snippets for 1-click lookup
│   ├── ImmersiveReading.Dictionaries/                # Sub-5ms dictionary lookup & deinflection
│   │   ├── DictionaryDatabase.cs                     # dictionaries.db schema & connection manager
│   │   ├── YomitanSqliteImporter.cs                  # Imports Yomitan format-3 .zip dictionaries
│   │   ├── KanjidicImporter.cs                       # Imports kanjidic2.xml.gz, ka_data.csv, KanjiVG SVGs
│   │   ├── JapaneseDeinflector.cs                    # Breadth-first rule deinflector (45+ conjugation rules)
│   │   ├── DictionaryLookupEngine.cs                 # Longest-prefix + ruby-priority + deinflection lookup
│   │   └── VocabularyTracker.cs                      # Reads/writes user_vocabulary in library.db
│   ├── Plugins/
│   │   ├── ImmersiveReading.Plugins.Japanese/        # MeCab.DotNet Japanese tokenizer & furigana aligner
│   │   └── ImmersiveReading.Plugins.AiCompanion/     # Progressive Quiz, n+1 Persona Chat & LLM provider
│   └── ImmersiveReading.App/                         # Photino.NET Desktop Host, Local Asset Server & Web UI
│       ├── Program.cs                                # Composition root, Photino window, local HTTP server
│       ├── Rpc/LibraryRpcDispatcher.cs               # JSON-RPC 2.0 router for all UI commands
│       └── wwwroot/                                  # Vanilla HTML/CSS/JS frontend (zero build step required)
│           ├── index.html                            # Library shell, Reader shell, Modals, Drawers
│           ├── app.js                                # Frontend state, RPC client, Popover, Study & AI UI
│           ├── reader-frame.js                       # Injected into every EPUB chapter iframe
│           ├── reader-injected.css                   # Injected into every EPUB chapter iframe
│           └── styles.css                            # Application theme & layout styles
└── tests/
    ├── ImmersiveReading.Core.Tests/                  # Phase 1 (Library) & Phase 4 (Study Pack/Mining) tests
    ├── ImmersiveReading.Enrichment.Tests/            # Phase 2 (AngleSharp/MeCab) & Phase 5 (AI/n+1) tests
    └── ImmersiveReading.Dictionaries.Tests/          # Phase 3 (Deinflection, Yomitan, Lookup) tests
```

---

## 4. Five Core End-to-End Flows (How Things Are Connected)

Understanding these 5 flows will explain 95% of what happens at runtime.

### Flow 1: Frontend Button Click $\rightarrow$ C# Backend (`JSON-RPC 2.0`)
1. **Frontend ([`app.js`](immersive-reader/src/ImmersiveReading.App/wwwroot/app.js#L62-L87))**:
   Any UI action calls `await rpcCall("domain.methodName", { ...params })`.
   - Under the hood, `rpcCall` generates a request ID (`req-1`, `req-2`, ...), stores a Promise in `pendingRpc`, and calls `window.external.sendMessage(JSON.stringify({ id, method, params }))`.
2. **Photino Bridge ([`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs#L156-L169))**:
   `window.RegisterWebMessageReceivedHandler` receives the JSON string on a background task and passes it to [`LibraryRpcDispatcher.DispatchJsonAsync`](immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs#L64-L91).
3. **Router ([`LibraryRpcDispatcher.cs`](immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs#L93-L550))**:
   - Built-in routes (`library.*`, `reader.*`, `lookup.*`, `vocab.*`, `dicts.*`, `studyPack.*`, `flashcards.*`, `plugins.*`) are matched in the `switch (method)` block.
   - Plugin routes (such as `ai.quiz.*`, `ai.chat.*`, `ai.persona.*`, `ai.settings.*`) fall through to `default:` where [`_featurePluginHost.TryHandleRpcAsync(method, p, ct)`](immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs#L533-L541) dispatches them to the registered [`IFeaturePlugin`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs#L15-L21).
4. **Response**:
   `DispatchJsonAsync` serializes `{ id, result, error }` and calls `window.SendWebMessage(responseJson)`, resolving the Promise in `app.js`.

---

### Flow 2: Opening a Book & Pre-Render Chapter Enrichment (`ir-book://`)
When a user clicks **Read** on a book card:
1. **Session Load ([`app.js`](immersive-reader/src/ImmersiveReading.App/wwwroot/app.js))**:
   Calls `rpcCall("reader.openBook", { bookId })`, which returns the book's spine chapters, TOC, saved `reading_progress` (`chapterIndex`, `tokenId`, `readingMode`), and the initial chapter's URL (`ir-book://book-4e0541b184c4ace9/item/xhtml/p-001.xhtml`).
2. **Custom Scheme Interception ([`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs#L140-L154) $\rightarrow$ [`BookSchemeHandler.cs`](immersive-reader/src/ImmersiveReading.Enrichment/BookSchemeHandler.cs))**:
   - The reader sets `<iframe id="reader-iframe" src="ir-book://...">` (or fetches via `reader.getChapterContent` fallback).
   - `RegisterCustomSchemeHandler("ir-book", ...)` calls [`BookSchemeHandler.ResolveAsync(uri)`](immersive-reader/src/ImmersiveReading.Enrichment/BookSchemeHandler.cs#L43-L108).
   - If the path is an image, font, or CSS file inside the EPUB ZIP, `BookSchemeHandler` streams the raw bytes directly from the `.epub` archive with the appropriate MIME type.
   - If the path is an `.xhtml` / `.html` spine document, `BookSchemeHandler` passes the raw XHTML to [`AngleSharpDomEnricher.EnrichChapterAsync`](immersive-reader/src/ImmersiveReading.Enrichment/AngleSharpDomEnricher.cs).
3. **Ruby-Aware DOM Walking & MeCab Tokenization ([`AngleSharpDomEnricher.cs`](immersive-reader/src/ImmersiveReading.Enrichment/AngleSharpDomEnricher.cs))**:
   - Parses the XHTML with `AngleSharp.Xml.Parser.XmlParser`.
   - Walks block paragraphs (`p`, `h1`–`h6`, `li`, `blockquote`, or `div` with direct text).
   - Extracts existing author `<ruby><rb>ベース</rb><rt>ふりがな</rt></ruby>` annotations **before** stripping `<rt>`/`<rp>` so ruby text never pollutes the base string sent to the tokenizer.
   - Passes the clean paragraph texts and author ruby spans to [`JapaneseLanguagePlugin.AnalyzeParagraphsAsync`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseLanguagePlugin.cs), which runs `MeCab.DotNet`, converts UniDic/IPADIC katakana readings to hiragana, splits okurigana stems (`align`), and preserves author `<ruby>` readings (`RubySource.EpubAuthor`) over tokenizer guesses (`RubySource.Tokenizer`).
   - Replaces text nodes in the AngleSharp DOM with interactive spans:
     ```html
     <span class="ir-tok ir-pos-noun" data-tid="42" data-sid="7" data-pid="3" data-lemma="魔法" data-read="まほう" data-pos="noun" data-status="learning">
       <ruby class="ir-ruby" data-ruby-src="epub"><rb>魔</rb><rt>ま</rt><rb>法</rb><rt>ほう</rt></ruby>
     </span>
     ```
   - Injects `/reader-injected.css` and `/reader-frame.js` into `<head>`/`<body>` and caches the `EnrichedChapterResult` (including the `IReadOnlyDictionary<int, RichToken>` map) inside [`ChapterEnrichmentCache`](immersive-reader/src/ImmersiveReading.Enrichment/ChapterEnrichmentCache.cs).

---

### Flow 3: Clicking a Word $\rightarrow$ Sub-5ms Dictionary Popover & Sentence Mining
1. **Click Inside Chapter or AI Snippet ([`app.js`](immersive-reader/src/ImmersiveReading.App/wwwroot/app.js))**:
   - Clicking any `.ir-tok` span reads its `data-tid` (`tokenId`), `data-sid` (`sentenceId`), and surface text, then notifies `app.js` via `window.parent.postMessage({ type: "ir:tokenClick", ... })`.
2. **Lookup RPC (`lookup.query`)**:
   - `app.js` calls `rpcCall("lookup.query", { bookId, chapterHref, tokenId, surfaceOverride, readingOverride, lemmaOverride })`.
   - [`DictionaryLookupEngine.LookupAsync`](immersive-reader/src/ImmersiveReading.Dictionaries/DictionaryLookupEngine.cs) fetches the cached chapter from `ChapterEnrichmentCache`, locates the clicked `RichToken` and its containing sentence, and extracts up to 16 characters of surface text starting at the clicked token's start offset.
   - Generates candidate substrings (longest first) and runs [`JapaneseDeinflector.Deinflect`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.Japanese/JapaneseDeinflector.cs) (e.g., `食べさせられなかった` $\rightarrow$ `食べる`).
   - Queries `dictionaries.db` (`terms` table by `expression` and `reading_hira`) and ranks matches by:
     1. Longest surface substring match
     2. Exact match with EPUB author `<ruby>` reading (`RubySource.EpubAuthor`)
     3. Exact match with `MeCab` lemma & reading
     4. Valid grammar part-of-speech chain from deinflection
     5. Dictionary priority & frequency rank
   - Also queries `kanji_characters` in `dictionaries.db` for every kanji character in the matched headword (meanings, on/kun readings, JLPT, stroke count, KanjiVG SVG).
3. **Mining a Sentence Flashcard (`flashcards.mine`)**:
   - Clicking **🃏 Mine Sentence to Anki** in the popover calls `rpcCall("flashcards.mine", { ... })` $\rightarrow$ [`FlashcardMiningService.MineCard`](immersive-reader/src/ImmersiveReading.Library/FlashcardMiningService.cs).
   - Writes atomically to `enrichement-extractor/output/data/flashcards/library.json` and `mined-anki.tsv` with `<ruby>` furigana and `<mark>` around the target word. **Story sentences are never translated into English.**

---

### Flow 4: Study Pack Bridge & Printable PDFs (`studyPack.*`)
1. When the user clicks **📚 Study Pack** in the Reader topbar, `app.js` calls `rpcCall("studyPack.get", { bookId })`.
2. [`PythonStudyPackBridge`](immersive-reader/src/ImmersiveReading.Library/PythonStudyPackBridge.cs) checks `enrichement-extractor/output/.publication/active.json`, acquires an OS byte-range lease (`AcquireSnapshotLease`), and inspects `output/pdf/<book-id>/` and `output/data/<book-id>/vocabulary-sets.json`.
3. Master and chapter PDFs are served to the frontend via the local asset server in [`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs#L244-L320) (`/ir-local-pdf/<book-id>/<filename>.pdf`), so they open directly inside the embedded PDF viewer modal or external viewer.
4. If a book does not yet have a prebuilt study pack, clicking **⚡ Build Study Pack** invokes `enrichement-extractor\.venv\Scripts\python.exe -m immersive.cli build|enrich` asynchronously.

---

### Flow 5: AI Companion Plugin (Zero-Spoiler Quiz & $n+1$ Persona Chat)
1. **Extension Bus Registration**:
   At startup in [`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs#L62-L70), [`AiCompanionPlugin`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/AiCompanionPlugin.cs) is registered with [`FeaturePluginHost`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs#L65-L120).
2. **Zero-Spoiler Context Slicing ([`ReaderContextService.cs`](immersive-reader/src/ImmersiveReading.Enrichment/ReaderContextService.cs))**:
   - Before generating a quiz or persona reply, `ReaderContextService` fetches the user's current `(CurrentChapterIndex, CurrentTokenOffset)` and filters chapters/sentences so **only tokens with `ChapterIndex < CurrentChapterIndex` or `(ChapterIndex == CurrentChapterIndex && TokenId <= CurrentTokenOffset)`** are included. Unread text in the current chapter or later chapters is physically excluded from the slice.
3. **Progressive Comprehension Quiz ([`ComprehensionQuizService.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/ComprehensionQuizService.cs))**:
   - Starts at `QuizStage.MultipleChoice` (Stage 1).
   - Tracks answers in `comprehension_questions` (`library.db`). When rolling accuracy over the last 5 questions reaches $\ge 80\%$, automatically promotes the user to `GuidedShortAnswer` (Stage 2) and then `FreeForm` (Stage 3).
4. **$n+1$ Vocabulary-Constrained Persona Chat ([`PersonaChatService.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/PersonaChatService.cs))**:
   - Queries [`VocabularyBudgetService.GetSnapshotAsync`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/VocabularyBudgetService.cs) for the user's `Known` lemmas (`N`) and `Learning` / next-20-set target words (`+1`).
   - Every sentence generated by the persona is tokenized by `JapaneseLanguagePlugin` (`MeCab.DotNet`) and checked by [`PersonaChatService.ValidateSentenceComplianceAsync`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/PersonaChatService.cs#L198-L250) to guarantee `unknownCount <= maxUnknownWordsPerSentence` (at most one $+1$ word per sentence).
5. **Interactive AI Snippets ([`SnippetEnricher.cs`](immersive-reader/src/ImmersiveReading.Enrichment/SnippetEnricher.cs))**:
   - Every Japanese question, option, model answer, and chat bubble is enriched via `SnippetEnricher.EnrichHtmlSnippetAsync`, which stores a synthetic chapter `__snippet__/snip-XXXXX.xhtml` in `ChapterEnrichmentCache`. Clicking any word inside the AI drawer opens the exact same Dictionary Popover as the EPUB reader!

---

## 5. Step-by-Step Recipes for Common Development Tasks

### Recipe A: Adding a New RPC Endpoint & UI Button in the Desktop App
1. **Backend Method**: Add your domain logic to the relevant service (e.g., [`LibraryManager.cs`](immersive-reader/src/ImmersiveReading.Library/LibraryManager.cs) or a plugin).
2. **Expose via JSON-RPC**:
   - For core features: Add a `case "myDomain.myAction":` in [`LibraryRpcDispatcher.HandleMethodAsync`](immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs#L93-L550).
   - For plugin features: Handle the method inside your [`IFeaturePlugin.HandleRpcAsync`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/AiCompanionPlugin.cs#L82-L195) implementation.
3. **Call from Frontend**:
   In [`wwwroot/app.js`](immersive-reader/src/ImmersiveReading.App/wwwroot/app.js), call:
   ```javascript
   const data = await rpcCall("myDomain.myAction", { bookId: currentBookId });
   ```
   *(Note: `wwwroot/` uses plain ES2022 JavaScript and CSS—no bundler or npm build step is required. Just edit and run!)*

### Recipe B: Adding a New Table or Column to `library.db`
1. Open [`LibraryDatabase.cs`](immersive-reader/src/ImmersiveReading.Library/LibraryDatabase.cs) and locate `InitializeSchema()`.
2. Add your `CREATE TABLE IF NOT EXISTS ...` or idempotent column migration in `EnsureColumnExists(...)`.
3. Add strongly-typed query/command methods on [`LibraryManager`](immersive-reader/src/ImmersiveReading.Library/LibraryManager.cs) or your service using `using var conn = _db.OpenConnection();`.
4. Write an xUnit test in [`immersive-reader/tests/`](immersive-reader/tests) using a temporary directory (`Path.Combine(Path.GetTempPath(), ...)`).

### Recipe C: Creating a New `IFeaturePlugin`
1. Create a class implementing [`IFeaturePlugin`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs#L15-L21) (see [`AiCompanionPlugin.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/AiCompanionPlugin.cs) as a template).
2. In `InitializeAsync(IFeaturePluginContext context, CancellationToken ct)`, you automatically receive:
   - `context.ReaderContext` ([`IReaderContextService`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs#L37-L44)) for zero-spoiler text slicing.
   - `context.VocabularyBudget` ([`IVocabularyBudgetService`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs#L53-L57)) for querying the user's `Known` and `Learning` vocabulary.
   - `context.SnippetEnricher` ([`ISnippetEnricher`](immersive-reader/src/ImmersiveReading.Core/Extensions/FeaturePluginContracts.cs#L46-L51)) for turning plain Japanese strings into interactive `<ruby>` + `.ir-tok` HTML.
3. Register your plugin in [`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs#L62-L70) via `featurePluginHost.RegisterPluginAsync(myPlugin)`.

### Recipe D: Modifying Offline Python Enrichment or PDF Layout (`enrichement-extractor/`)
1. Consult [`LLM-WIKI.md`](LLM-WIKI.md) and the modular pages in [`wiki/`](wiki/index.md):
   - Core token/vocabulary matching: [`immersive/build.py`](enrichement-extractor/immersive/build.py) and [`immersive/matching.py`](enrichement-extractor/immersive/matching.py).
   - Furigana alignment: [`immersive/furigana.py`](enrichement-extractor/immersive/furigana.py).
   - Study sets of 20 & kanji context: [`immersive/study_data.py`](enrichement-extractor/immersive/study_data.py) and [`immersive/context.py`](enrichement-extractor/immersive/context.py).
   - Printable A4 PDFs: [`immersive/kanji_pdf.py`](enrichement-extractor/immersive/kanji_pdf.py), [`immersive/master_pdf.py`](enrichement-extractor/immersive/master_pdf.py), and [`immersive/pdf_elements.py`](enrichement-extractor/immersive/pdf_elements.py).
2. If you change `reader.json` structure, update [`immersive/reader.schema.json`](enrichement-extractor/immersive/reader.schema.json), [`immersive/validation.py`](enrichement-extractor/immersive/validation.py), and `READER_FILES` in [`immersive/cache.py`](enrichement-extractor/immersive/cache.py#L24-L26).

---

## 6. Running, Debugging & Verifying Everything

### Run the Desktop Application Interactively
```powershell
cd k:\immersive-reading\immersive-reader
dotnet run --project src\ImmersiveReading.App
```

### Run Automated End-to-End Self-Tests (Headless & Photino UI)
[`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs) includes built-in verification modes that test the entire stack (library bootstrap, `ir-book://` DOM enrichment, dictionary lookup, study-pack bridge, flashcard mining, and AI comprehension quiz) and exit with code `0`:
```powershell
cd k:\immersive-reading\immersive-reader

# 1. Fast CLI smoke test (verifies JSON-RPC bootstrap state)
dotnet run --project src\ImmersiveReading.App -- --smoke-test

# 2. Full backend reader verification (enrichment, lookup, study pack lease, flashcards, AI)
dotnet run --project src\ImmersiveReading.App -- --verify-reader

# 3. Real Photino WebView UI self-test (opens actual window, renders book in iframe, clicks token, opens popover, runs study pack & AI quiz)
dotnet run --project src\ImmersiveReading.App -- --verify-ui
```

### Run Unit & Specification Test Suites
```powershell
# Run all 35 .NET 10 xUnit tests (Phases 1-5 Gherkin specs)
cd k:\immersive-reading\immersive-reader
dotnet test ImmersiveReading.sln

# Run all 134 Python unittest tests
cd k:\immersive-reading\enrichement-extractor
.venv\Scripts\python.exe -m unittest discover -s tests -v
```

---

## 7. Non-Negotiable Rules to Keep in Mind

Before submitting any change, verify that your code respects the project invariants defined in [`AGENTS.md`](AGENTS.md):
1. **Never Translate Story Sentences**: English is permitted *only* for dictionary word/phrase glosses, kanji meanings, and grammar explanations. Story sentences in the reader, flashcards, quizzes, and persona chat must remain untranslated Japanese.
2. **Zero Spoilers in AI Features**: Always obtain story passages through [`IReaderContextService`](immersive-reader/src/ImmersiveReading.Enrichment/ReaderContextService.cs) so tokens beyond `(CurrentChapterIndex, CurrentTokenOffset)` can never leak into prompts.
3. **Preserve Source Immutability**: Never mutate user `.epub` files, and never modify source `.csv` files unless explicitly running the Python `--decks-only` / `--update-decks` command with write-ahead provenance ([`provenance.py`](enrichement-extractor/immersive/provenance.py)).
4. **Strict Print Dependency Isolation in Python**: Base modules in `enrichement-extractor/immersive/` must never import `reportlab`, `pymupdf`, `fontTools`, or `PIL` at top level.

