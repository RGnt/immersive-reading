# AGENTS.md — AI Coding Agent Instructions for `immersive-reading`

When working in this workspace (`k:/immersive-reading`), **always read [`LLM-WIKI.md`](LLM-WIKI.md) first** and consult the modular topic pages in [`wiki/`](wiki/index.md) before reading or modifying source files.

---

## 1. Where to Look First

| Task Type | Read This Wiki Page First | Primary Source Files |
| :--- | :--- | :--- |
| **Developer onboarding & how `.NET` + `Python` connect** | [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md) | [`Program.cs`](immersive-reader/src/ImmersiveReading.App/Program.cs), [`LibraryRpcDispatcher.cs`](immersive-reader/src/ImmersiveReading.App/Rpc/LibraryRpcDispatcher.cs), [`App.tsx`](immersive-reader/src/ImmersiveReading.App/client/src/App.tsx) |
| **General orientation / architecture** | [LLM-WIKI.md](LLM-WIKI.md), [wiki/architecture-and-dataflow.md](wiki/architecture-and-dataflow.md) | [`orchestration.py`](enrichement-extractor/immersive/orchestration.py), [`cli.py`](enrichement-extractor/immersive/cli.py), [`pipeline.py`](enrichement-extractor/immersive/pipeline.py) |
| **Any C# / `immersive-reader` task, or an error you hit** | [wiki/troubleshooting-and-lessons.md](wiki/troubleshooting-and-lessons.md) (grep the symptom first), [wiki/dotnet-architecture.md](wiki/dotnet-architecture.md) | Known issues: [`dotnet-code-review.md`](wiki/dotnet-code-review.md) |
| **Plugin UI (ui.js, toolbar/drawer/view slots, plugin settings panels)** | [wiki/plugin-ui-authoring.md](wiki/plugin-ui-authoring.md) | `client/src/services/pluginModuleLoader.ts`, `pluginImportRewriter.ts`, `stores/usePluginStore.ts` |
| **EPUB chapters, ToC, spine, chapter list, reader chapter navigation, illustration/image page layout** | [wiki/dotnet-architecture.md](wiki/dotnet-architecture.md) section 8, [specs/12-chapter-grouping-spec.md](specs/12-chapter-grouping-spec.md), [specs/14-image-page-layout-spec.md](specs/14-image-page-layout-spec.md) | [`ChapterListBuilder.cs`](immersive-reader/src/ImmersiveReading.Core/Epub/ChapterListBuilder.cs), [`ChapterModelUpgrader.cs`](immersive-reader/src/ImmersiveReading.Library/Books/ChapterModelUpgrader.cs), [`ChapterDocumentComposer.cs`](immersive-reader/src/ImmersiveReading.Enrichment/Dom/ChapterDocumentComposer.cs) |
| **Importing / scanning EPUBs, skipped books, comic or image-only books** | [wiki/dotnet-architecture.md](wiki/dotnet-architecture.md) section 9, [specs/13-image-only-book-skip-spec.md](specs/13-image-only-book-skip-spec.md) | [`BookContentProfile.cs`](immersive-reader/src/ImmersiveReading.Core/Epub/BookContentProfile.cs), [`BookContentAnalyzer.cs`](immersive-reader/src/ImmersiveReading.Core/Epub/BookContentAnalyzer.cs), [`BookImportService.cs`](immersive-reader/src/ImmersiveReading.Library/Books/BookImportService.cs) |
| **Finding a function, class, or file** | [wiki/module-reference.md](wiki/module-reference.md) | `enrichement-extractor/immersive/*.py` |
| **JSON schemas, offsets, or reader integration** | [wiki/data-contracts-and-schemas.md](wiki/data-contracts-and-schemas.md) | [`reader.schema.json`](enrichement-extractor/immersive/reader.schema.json), [`config.schema.json`](enrichement-extractor/immersive/config.schema.json), [`enrichment.py`](enrichement-extractor/immersive/enrichment.py) |
| **EPUB parsing, vocabulary matching, furigana, Yomitan, or ambiguity** | [wiki/matching-and-linguistics.md](wiki/matching-and-linguistics.md) | [`epub.py`](enrichement-extractor/immersive/epub.py), [`build.py`](enrichement-extractor/immersive/build.py), [`matching.py`](enrichement-extractor/immersive/matching.py), [`furigana.py`](enrichement-extractor/immersive/furigana.py), [`yomitan.py`](enrichement-extractor/immersive/yomitan.py), [`ambiguity.py`](enrichement-extractor/immersive/ambiguity.py) |
| **Shared resources, thread safety, caching, publication, or GC** | [wiki/publication-caching-and-resources.md](wiki/publication-caching-and-resources.md) | [`resources.py`](enrichement-extractor/immersive/resources.py), [`tokenization.py`](enrichement-extractor/immersive/tokenization.py), [`cache.py`](enrichement-extractor/immersive/cache.py), [`publication.py`](enrichement-extractor/immersive/publication.py), [`retention.py`](enrichement-extractor/immersive/retention.py) |
| **Study cards, kanji context, HTML/PDF rendering, or validation** | [wiki/study-exports-and-validation.md](wiki/study-exports-and-validation.md) | [`study_data.py`](enrichement-extractor/immersive/study_data.py), [`context.py`](enrichement-extractor/immersive/context.py), [`render_outputs.py`](enrichement-extractor/immersive/render_outputs.py), [`kanji_pdf.py`](enrichement-extractor/immersive/kanji_pdf.py), [`validation.py`](enrichement-extractor/immersive/validation.py), [`verification.py`](enrichement-extractor/immersive/verification.py), [`pdf_validation.py`](enrichement-extractor/immersive/pdf_validation.py) |
| **Writing tests or synthetic fixtures** | [wiki/testing-and-fixtures.md](wiki/testing-and-fixtures.md) | [`tests/test_reader_enrichment.py`](enrichement-extractor/tests/test_reader_enrichment.py) |
| **Character knowledge graph (spoiler-gated, LLM-built), prompt templates** | [specs/18-character-knowledge-graph-spec.md](specs/18-character-knowledge-graph-spec.md), [specs/19-prompt-templates-and-conversation-coach-spec.md](specs/19-prompt-templates-and-conversation-coach-spec.md), [`knowledge-graph/README.md`](knowledge-graph/README.md) | [`gate.py`](knowledge-graph/immersive_knowledge/gate.py), [`ops.py`](knowledge-graph/immersive_knowledge/ops.py), [`builder.py`](knowledge-graph/immersive_knowledge/builder.py), [`scanner.py`](knowledge-graph/immersive_knowledge/scanner.py) |
| **Character Knowledge plugin (build/browse the graph from the app)** | [specs/20-character-knowledge-plugin-spec.md](specs/20-character-knowledge-plugin-spec.md) | [`CharacterKnowledgePlugin.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.CharacterKnowledge/CharacterKnowledgePlugin.cs), [`KnowledgeDb.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.CharacterKnowledge/KnowledgeDb.cs), `Assets/ui.src.jsx` |
| **Events, items and who has which gear (story pass of the knowledge graph)** | [specs/22-story-events-and-items-spec.md](specs/22-story-events-and-items-spec.md) | [`story.py`](knowledge-graph/immersive_knowledge/story.py), [`store.py`](knowledge-graph/immersive_knowledge/store.py) (`set_holding`, `rollback_story_from`), [`ops.py`](knowledge-graph/immersive_knowledge/ops.py) (`item_state`), [`builder.py`](knowledge-graph/immersive_knowledge/builder.py) (`_run_story`), `prompts/story.extract.*` |
| **Knowledge hub: other features reading knowledge graphs, what the user told us (user memory), future subject graphs** | [specs/24-knowledge-hub-and-user-memory-spec.md](specs/24-knowledge-hub-and-user-memory-spec.md) | [`KnowledgeContracts.cs`](immersive-reader/src/ImmersiveReading.Core/Knowledge/KnowledgeContracts.cs), [`KnowledgeHub.cs`](immersive-reader/src/ImmersiveReading.Knowledge/KnowledgeHub.cs), [`UserMemorySource.cs`](immersive-reader/src/ImmersiveReading.Knowledge/UserMemorySource.cs), [`StoryKnowledgeSource.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.CharacterKnowledge/StoryKnowledgeSource.cs) |
| **Reading quiz (last completed chapter), written-answer rubric, vocabulary nudges, favourites and reading lists, bookmark lines** | [specs/26-quiz-rubric-reading-lists-and-bookmarks-spec.md](specs/26-quiz-rubric-reading-lists-and-bookmarks-spec.md) | [`ComprehensionQuizService.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/ComprehensionQuizService.cs), [`QuizQuestionValidator.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/QuizQuestionValidator.cs), [`FreeFormRubric.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/FreeFormRubric.cs), [`BookFlagsService.cs`](immersive-reader/src/ImmersiveReading.Library/Books/BookFlagsService.cs), [`BookmarkLabel.cs`](immersive-reader/src/ImmersiveReading.Library/Books/BookmarkLabel.cs) |
| **Text editor (Monaco), editable prompts, user stylesheet, memory view in Settings** | [specs/25-editor-prompts-and-custom-css-spec.md](specs/25-editor-prompts-and-custom-css-spec.md) | [`loadMonaco.ts`](immersive-reader/src/ImmersiveReading.App/client/src/components/editor/loadMonaco.ts), [`CodeEditor.tsx`](immersive-reader/src/ImmersiveReading.App/client/src/components/editor/CodeEditor.tsx), [`PromptTemplate.cs`](immersive-reader/src/ImmersiveReading.Core/Prompts/PromptTemplate.cs), [`PromptCatalog.cs`](immersive-reader/src/ImmersiveReading.Core/Prompts/PromptCatalog.cs), [`UserCssStore.cs`](immersive-reader/src/ImmersiveReading.App/Appearance/UserCssStore.cs), [`PersonaMemory.cs`](immersive-reader/src/Plugins/ImmersiveReading.Plugins.AiCompanion/PersonaMemory.cs) |
| **Library management: bulk metadata editing (series, volume numbers, tags ...)** | [specs/23-library-bulk-edit-spec.md](specs/23-library-bulk-edit-spec.md) | [`BulkNumbering.cs`](immersive-reader/src/ImmersiveReading.Library/Books/BulkNumbering.cs), [`BookMetadataService.cs`](immersive-reader/src/ImmersiveReading.Library/Books/BookMetadataService.cs) (`BulkUpdateBooks`, `PreviewBulkUpdate`), `BulkEditModal.tsx`, `BulkActionBar.tsx` |
| **Library management, duplicates, removing/merging books** | [specs/21-library-duplicate-detection-spec.md](specs/21-library-duplicate-detection-spec.md) | [`DuplicateDetector.cs`](immersive-reader/src/ImmersiveReading.Library/Duplicates/DuplicateDetector.cs), [`DuplicateNormalizer.cs`](immersive-reader/src/ImmersiveReading.Library/Duplicates/DuplicateNormalizer.cs), [`DuplicateService.cs`](immersive-reader/src/ImmersiveReading.Library/Duplicates/DuplicateService.cs), `DuplicatesModal.tsx` |
| **Planned features (DOM source map, positional lookup)** | [wiki/roadmap-and-open-work.md](wiki/roadmap-and-open-work.md) | [`docs/reader-integration-refactoring-plan.md`](enrichement-extractor/docs/reader-integration-refactoring-plan.md) |

---

## 2. Hard Invariants (Never Violate)

1. **No LLM / No Network at Runtime**: Everything in `enrichement-extractor` runs 100% offline and deterministically.
2. **Never Translate Story Sentences**: English is permitted only for word/phrase dictionary glosses, kanji meanings, and grammar explanations.
3. **Strict Print Dependency Isolation**: Base modules (`enrichment.py`, `build.py`, `study_data.py`, `card_export.py`, `render.py`, `validation.py`, `resources.py`, `orchestration.py`, `publication.py`) must never import `reportlab`, `pymupdf`, `fontTools`, or `PIL` at top level.
4. **Source Input Immutability by Default**: `reader` mode and default `study` builds must never modify input `.epub` or `.csv` files. Source CSV `first_appearance` updates happen only under `--decks-only`, `--update-decks`, or `cli decks`, and must log write-ahead provenance via [`provenance.py`](enrichement-extractor/immersive/provenance.py).
5. **Thread & Process Boundaries**: [`ResourceStore`](enrichement-extractor/immersive/resources.py#L101-L350) is process-scoped (`os.getpid()`) and thread-safe with frozen data (`MappingProxyType`/`tuple`). [`WorkerContext`](enrichement-extractor/immersive/tokenization.py#L9-L66) is strictly single-thread (`threading.current_thread()`).
6. **Use `require()`, Never `assert`**: Production validation must remain active under `python -O`.
7. **Record Lessons**: after fixing a non-trivial bug or making a design decision, add a why / cause / fix / prevent entry to [`wiki/troubleshooting-and-lessons.md`](wiki/troubleshooting-and-lessons.md) (or the .NET pages) so it is never re-investigated.
8. **Keep the Wiki Updated**: When adding modules, changing schemas, or completing roadmap items, update [`LLM-WIKI.md`](LLM-WIKI.md) and the corresponding page in [`wiki/`](wiki/index.md).

---

9. **`knowledge-graph/` is not covered by invariants 1 to 6.** It is a separate project (own environment) that *deliberately* calls an LLM (an OpenAI-compatible endpoint the user configures) to build the character knowledge graph. `enrichement-extractor` must never import it or depend on it. Its own non-negotiables: nothing at or after the reader's position is ever stored as visible or sent to a model (spec 18, I-K1..I-K4), no fallback answers, API keys only in the environment.

---

## 3. Running Tests

Always run Python tests from `k:\immersive-reading\enrichement-extractor` using its virtual environment, and .NET tests from `k:\immersive-reading\immersive-reader`:

```powershell
cd k:\immersive-reading\enrichement-extractor
.venv\Scripts\python.exe -m unittest discover -s tests -v

cd k:\immersive-reading\immersive-reader
dotnet test ImmersiveReading.sln

cd k:\immersive-reading\knowledge-graph
.venv\Scripts\python.exe -m unittest discover -s tests -v
```

: When adding modules, changing schemas, or completing roadmap items, update [`LLM-WIKI.md`](LLM-WIKI.md) and the corresponding page in [`wiki/`](wiki/index.md).

---

## 3. Running Tests

Always run Python tests from `k:\immersive-reading\enrichement-extractor` using its virtual environment, and .NET tests from `k:\immersive-reading\immersive-reader`:

```powershell
cd k:\immersive-reading\enrichement-extractor
.venv\Scripts\python.exe -m unittest discover -s tests -v

cd k:\immersive-reading\immersive-reader
dotnet test ImmersiveReading.sln
```

