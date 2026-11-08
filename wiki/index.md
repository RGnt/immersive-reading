# Immersive Reading — LLM Wiki Index & Glossary

Welcome to the **Immersive Reading LLM Wiki**. This knowledge base documents [`enrichement-extractor`](../enrichement-extractor) so that AI agents and developers can quickly locate code symbols, understand architectural boundaries, respect invariants, and execute modifications safely.

---

## Wiki Pages & Guides

-1. **[Comprehensive End-User Guide (`../USERGUIDE.md`)](../USERGUIDE.md)**
    - Complete guide for readers: library navigation, Vim motions, 1-click jiten.moe lookup, sub-5ms dictionary popover, 1-click Anki mining, and settings.
0. **[Developer Onboarding & Connections (`../DEVELOPER_GUIDE.md`)](../DEVELOPER_GUIDE.md)**
   - Practical guide for someone opening the codebase for the first time: how `immersive-reader/` (.NET 10) and `enrichement-extractor/` (Python) connect, the 5 end-to-end runtime flows, and where to jump in to modify any feature.
1. **[Master Single-File Reference (`../LLM-WIKI.md`)](../LLM-WIKI.md)**
   - High-density overview of the entire codebase, invariants, architecture diagram, and module map.
2. **[Architecture & Dataflow (`architecture-and-dataflow.md`)](architecture-and-dataflow.md)**
   - How inputs flow from `books/<book>/` and `data/` through `enrich_book`, `prepare_study_data`, exporters, and `Publication`.
   - Comparison of `reader`, `study`, and `decks` modes and CLI entry points (`immersive.cli` vs `immersive.pipeline`).
3. **[Module & Symbol Reference (`module-reference.md`)](module-reference.md)**
   - Detailed catalog of every file in `immersive/`, `scripts/`, and `tests/`, with clickable links to classes and functions.
4. **[Data Contracts & Schemas (`data-contracts-and-schemas.md`)](data-contracts-and-schemas.md)**
   - Structure of `reader.json`, `lookup.json`, `quality.json`, `study.config.json`, `vocabulary-sets.json`, `kanji-context.json`, and `flashcards/library.json`.
   - Deterministic ID hashing (`uid`) and coordinate conventions (Unicode code points vs UTF-16 code units vs EPUB DOM).
5. **[Matching & Linguistics (`matching-and-linguistics.md`)](matching-and-linguistics.md)**
   - EPUB spine/navigation parsing, ruby extraction without polluting base text, Sudachi split modes A and C, `MatchPolicy` reading verification, Yomitan format-3 ZIP import, furigana generation, example selection, and ambiguity modeling.
6. **[Publication, Caching & Resources (`publication-caching-and-resources.md`)](publication-caching-and-resources.md)**
   - `.publication/releases/<generation>/` atomic staging, `.publication/active.json`, compatibility views, OS file locks, `ResourceBundle` / `ResourceStore` / `WorkerContext` thread and process rules, `ContentCache`, `.enrichment/deck-provenance.json`, and `retention.py` GC/leases.
7. **[Study Exports & Validation (`study-exports-and-validation.md`)](study-exports-and-validation.md)**
   - `StudyData` preparation, first-appearance deduplication, learning sets of 20, kanji context selection (`context.py`), HTML and A4 PDF rendering, 3-layer validation (`validation.py`, `verification.py`, `pdf_validation.py`), and portable ZIP builds.
8. **[Playbooks & Invariants (`playbooks-and-invariants.md`)](playbooks-and-invariants.md)**
   - Non-negotiable rules, step-by-step playbooks for adding features or fixing bugs, and test/verification commands.
9. **[Testing Architecture & Synthetic Fixtures (`testing-and-fixtures.md`)](testing-and-fixtures.md)**
   - How `tests/test_reader_enrichment.py:fixture()` builds in-memory/temp EPUBs and dictionaries, subprocess `NoPrint` import blockers, and the 19-module test matrix.
10. **[Roadmap, Open Workstreams & Known Edge Cases (`roadmap-and-open-work.md`)](roadmap-and-open-work.md)**
    - Completed vs. planned reader-integration steps and documented real-book linguistic exceptions.
11. **[.NET + Photino.NET EPUB Library & Reader App Specifications (`../specs/`)](../specs/00-master-architecture-and-roadmap.md)**
    - [00 — Master Architecture & Roadmap](../specs/00-master-architecture-and-roadmap.md)
    - [01 — EPUB Library Management Spec](../specs/01-epub-library-management-spec.md)
    - [02 — Pre-Render AngleSharp DOM Enrichment & Language Plugin Spec](../specs/02-prerender-enrichment-and-plugin-spec.md)
    - [03 — On-the-Fly Dictionary Lookup Engine Spec](../specs/03-onthefly-dictionary-lookup-spec.md)
    - [04 — Photino.NET Desktop App, Custom Scheme & IPC Spec](../specs/04-photino-reader-ui-and-ipc-spec.md)
    - [05 — Unified Extensibility & AI Companion Spec](../specs/05-extensibility-and-ai-companion-spec.md)
    - [06 — Junior Developer Implementation Backlog](../specs/06-junior-developer-implementation-plan.md)
    - [07 — Global Configuration & Settings Spec](../specs/07-global-configuration-and-settings-spec.md)
    - [08 — Pluggable Python Pipeline & Study Pack Stages Spec](../specs/08-pluggable-python-pipeline-stages-spec.md)

---

## Domain Glossary

| Term | Definition in Codebase |
| :--- | :--- |
| **Book ID (`book-<16 hex>`)** | Content-based identifier derived from `uid("book", sha256(epub_bytes).hexdigest())` in [`build.py`](../enrichement-extractor/immersive/build.py#L96). Changing EPUB bytes creates a new Book ID; changing CSVs or `study.config.json` keeps the same Book ID. |
| **Normalized Chapter Text (`chapter.text`)** | Concatenation of non-heading paragraph base strings in a chapter with whitespace (`[\s\u3000]+`) and `<rt>`/`<rp>` ruby readings removed ([`epub.py`](../enrichement-extractor/immersive/epub.py#L125-L133)). |
| **Source Ruby (`paragraph.ruby`)** | Original `<ruby>` annotations extracted from the EPUB using PUA markers (`\ue000...\ue001`), with half-open Unicode code-point offsets relative to `chapter.text`. |
| **Display Ruby (`sentence.display_ruby`)** | Sentence-relative furigana spans combining original EPUB ruby (`source: "epub"`) with offline Sudachi-generated readings for uncovered kanji (`source: "sudachi"`), produced by [`furigana.py`](../enrichement-extractor/immersive/furigana.py#L84-L89). |
| **Vocabulary Entry (`v-<16 hex>`)** | A single dictionary record in `reader.json.vocabulary`. For CSV decks, keyed by `JmDictWordId:Word:ReadingKana`; for Yomitan-only builds, keyed by `yomitan:expression:hira(reading)`. |
| **Grouped Vocabulary / Study Card** | In [`study_data.py`](../enrichement-extractor/immersive/study_data.py#L136-L162), multiple matched vocabulary entries with the **same spelling (`word`)** are consolidated into a single study card assigned to the earliest chapter where that spelling appears. Rejected homographs are excluded from matched study cards. |
| **Learning Set (`set-001`, ...)** | Ordered groups of 20 unique matched vocabulary spellings in first-appearance order across the entire book ([`vocabulary_learning_sets()`](../enrichement-extractor/immersive/study_data.py#L165-L176)). Sets can cross chapter boundaries; only the final set may have fewer than 20 words. |
| **Definition Candidate (`definition-<16 hex>`)** | A single source definition block in `vocabulary[].definition_candidates` ([`ambiguity.py`](../enrichement-extractor/immersive/ambiguity.py#L12-L28)). A flattened CSV `Definitions` cell is treated as one block (commas are not split as separate senses). |
| **Kanji Context Card** | A chapter kanji card enriched by [`context.py`](../enrichement-extractor/immersive/context.py#L32-L167) with one attested chapter word (`context_word`), reading (`context_reading`), and word gloss (`context_gloss`), kept strictly separate from the character's primary meanings (`Kanji alive` / `KANJIDIC2`). |
| **Generation (`<32 hex UUID>`)** | An immutable publication release under `output/.publication/releases/<generation>/` managed by [`publication.py`](../enrichement-extractor/immersive/publication.py#L285). Each book tracks `reader_generation` and optional `study_generation` independently. |
| **Compatibility View** | Repairable flat files in `output/data/`, `output/html/`, `output/pdf/`, `output/reports/`, and `output/study-pack.zip` copied from the active release by [`refresh_views()`](../enrichement-extractor/immersive/publication.py#L649-L688). |

---

## Mapping to Existing Feature Docs (`enrichement-extractor/docs/`)

The `enrichement-extractor/docs/` folder contains historical design and verification notes written during incremental development phases. This wiki synthesizes and cross-links them:
- [reader-integration-refactoring-plan.md](../enrichement-extractor/docs/reader-integration-refactoring-plan.md) & [pipeline-bug-fixes.md](../enrichement-extractor/docs/pipeline-bug-fixes.md)
- [reader-enrichment.md](../enrichement-extractor/docs/reader-enrichment.md) & [reader-data.md](../enrichement-extractor/docs/reader-data.md)
- [study-data.md](../enrichement-extractor/docs/study-data.md) & [card-data.md](../enrichement-extractor/docs/card-data.md)
- [orchestration-validation.md](../enrichement-extractor/docs/orchestration-validation.md) & [reliable-publication.md](../enrichement-extractor/docs/reliable-publication.md)
- [shared-resources.md](../enrichement-extractor/docs/shared-resources.md), [profiling-caching.md](../enrichement-extractor/docs/profiling-caching.md), & [retention.md](../enrichement-extractor/docs/retention.md)
- [yomitan-dictionaries.md](../enrichement-extractor/docs/yomitan-dictionaries.md), [ambiguity-handling.md](../enrichement-extractor/docs/ambiguity-handling.md), & [csv-provenance.md](../enrichement-extractor/docs/csv-provenance.md)
- [installation.md](../enrichement-extractor/docs/installation.md) & [portable-pipeline.md](../enrichement-extractor/docs/portable-pipeline.md)

