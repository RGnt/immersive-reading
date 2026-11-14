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
| **Finding a function, class, or file** | [wiki/module-reference.md](wiki/module-reference.md) | `enrichement-extractor/immersive/*.py` |
| **JSON schemas, offsets, or reader integration** | [wiki/data-contracts-and-schemas.md](wiki/data-contracts-and-schemas.md) | [`reader.schema.json`](enrichement-extractor/immersive/reader.schema.json), [`config.schema.json`](enrichement-extractor/immersive/config.schema.json), [`enrichment.py`](enrichement-extractor/immersive/enrichment.py) |
| **EPUB parsing, vocabulary matching, furigana, Yomitan, or ambiguity** | [wiki/matching-and-linguistics.md](wiki/matching-and-linguistics.md) | [`epub.py`](enrichement-extractor/immersive/epub.py), [`build.py`](enrichement-extractor/immersive/build.py), [`matching.py`](enrichement-extractor/immersive/matching.py), [`furigana.py`](enrichement-extractor/immersive/furigana.py), [`yomitan.py`](enrichement-extractor/immersive/yomitan.py), [`ambiguity.py`](enrichement-extractor/immersive/ambiguity.py) |
| **Shared resources, thread safety, caching, publication, or GC** | [wiki/publication-caching-and-resources.md](wiki/publication-caching-and-resources.md) | [`resources.py`](enrichement-extractor/immersive/resources.py), [`tokenization.py`](enrichement-extractor/immersive/tokenization.py), [`cache.py`](enrichement-extractor/immersive/cache.py), [`publication.py`](enrichement-extractor/immersive/publication.py), [`retention.py`](enrichement-extractor/immersive/retention.py) |
| **Study cards, kanji context, HTML/PDF rendering, or validation** | [wiki/study-exports-and-validation.md](wiki/study-exports-and-validation.md) | [`study_data.py`](enrichement-extractor/immersive/study_data.py), [`context.py`](enrichement-extractor/immersive/context.py), [`render_outputs.py`](enrichement-extractor/immersive/render_outputs.py), [`kanji_pdf.py`](enrichement-extractor/immersive/kanji_pdf.py), [`validation.py`](enrichement-extractor/immersive/validation.py), [`verification.py`](enrichement-extractor/immersive/verification.py), [`pdf_validation.py`](enrichement-extractor/immersive/pdf_validation.py) |
| **Writing tests or synthetic fixtures** | [wiki/testing-and-fixtures.md](wiki/testing-and-fixtures.md) | [`tests/test_reader_enrichment.py`](enrichement-extractor/tests/test_reader_enrichment.py) |
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

## 3. Running Tests

Always run Python tests from `k:\immersive-reading\enrichement-extractor` using its virtual environment, and .NET tests from `k:\immersive-reading\immersive-reader`:

```powershell
cd k:\immersive-reading\enrichement-extractor
.venv\Scripts\python.exe -m unittest discover -s tests -v

cd k:\immersive-reading\immersive-reader
dotnet test ImmersiveReading.sln
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

