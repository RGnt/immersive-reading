# Reader-first pipeline review and implementation plan

Reviewed 2026-11-01. This document describes proposed work, not features already implemented. The scope is the Python enrichment pipeline, its exports, tests and portable packaging. The EPUB reader itself is out of scope.

Implementation update: the initial task separation from step 5 is available through `immersive.enrichment.enrich_book`, `write_reader_data`, `immersive.cli enrich` and `immersive.pipeline --reader-only`. CSV updates now require `--decks-only` or `--update-decks`, including study builds. See [implemented API and task boundaries](reader-enrichment.md). This does not complete step 5's broader orchestration, packaging or validation refactor, nor the matching/source-map work in earlier steps.

Bug-fix update: matching now shares a reading policy across morphology, normalized token sequences and token-boundary phrases. Source ruby takes priority; incompatible homographs are rejected. Context fallback no longer silently restores a rejected reading/gloss; explicit reviewed corrections remain supported. Tokenization, vocabulary matching, grammar detection and sentence splitting respect paragraph boundaries. Chapter filters that remove everything raise an input error instead of restoring the spine; navigation-free spine sections no longer overlap. Both study commands preserve other books in the HTML library index. See [the bug-fix record](pipeline-bug-fixes.md) for tests, uncertainty handling and remaining work.

Data/rendering update: step 2 is implemented through `immersive.study_data`, `immersive.card_export`, HTML-only `immersive.render`, and PDF-only `immersive.pdf_elements`. The full pipeline shares one prepared model across exporters, and rendering preserves its input enrichment object. See [the API and verification record](study-data.md). Source mapping and the reader lookup API (steps 3–4) are being handled separately and are outside this workstream.

Orchestration/validation update: both commands now call `immersive.orchestration.run_job`; reader, HTML/PDF, card and packaging tasks use explicit options. Reader validation is separate from supplementary checks, and verification/packaging are installed modules. Production checks stay active under `python -O`. See [the API, command presets and verification record](orchestration-validation.md). Reliable publication, cancellation and manifest-based packaging are now implemented; original-vs-rewritten CSV hash expansion remains outstanding. See [publication guarantees](reliable-publication.md).

Yomitan update: local format-3 dictionaries can supplement Jiten decks or supply vocabulary for EPUBs without CSVs. Candidates, metadata and source notices remain reusable data. See [dictionary support](yomitan-dictionaries.md).

## Conclusion and LLM audit

No LLM calls, model-provider SDK dependencies or API-key requirements were found in the reviewed project source, dependency declarations and lockfile. There is no LLM stage to replace. Sudachi performs local dictionary-based morphological analysis; grammar detection uses bundled rules. The `prompt` field in card JSON means the front of a card, not an instruction sent to a model. `urllib.parse` parses EPUB paths; it does not fetch translations. The portable builder's subprocess runs a local runtime check.

A synthetic EPUB/CSV build also completed with Python socket access blocked. This verifies that exercised enrichment path, not every possible path or dependency implementation. Add permanent offline coverage below.

The main work is to make enrichment independently callable and its positions reliable in an original EPUB. PDFs should become optional consumers of enrichment, alongside HTML and card JSON.

```text
EPUB + deck CSV + local dictionaries + configuration
                         |
                    enrichment
                         |
                 validated reader bundle
                  /                 \
       positional/text lookup     study-card preparation
                                      |
                              JSON cards / HTML / PDF
```

Preserve these requirements throughout:

- Never translate story sentences. English belongs to word/phrase meanings and grammar explanations.
- Use a supplied CSV example first; otherwise use the first matching chapter sentence. Preserve provenance and whether a supplied example aligns with the book. A deck example can legitimately come from a later chapter.
- Keep original EPUB furigana first, with offline-generated readings identified separately. Missing or uncertain readings remain visible diagnostics.
- Keep all occurrences and dictionary candidates in reader data. Only study exports deduplicate entries into their first chapter and vocabulary sets of 20.
- Retain kanji meaning sources, dictionary glosses, source locators, licenses and KanjiVG provenance. Do not substitute a vocabulary gloss for a character meaning.
- Preserve the existing PDF layout, covers, blank cover reverse, practice areas and kana charts during architectural changes.
- Keep card creation tooling; do not reintroduce a reviewing application.

## Findings, ordered by reader impact

| Priority | Finding and evidence | Consequence |
| --- | --- | --- |
| High | In [`build.py`](../immersive/build.py), the morphology path checks an uninflected word's reading, but `normalized_token_sequence` does not. A temporary fixture containing 人気がある。 and deck entries 人気/にんき and 人気/ひとけ matched both; Sudachi read the text as にんき. | Incorrect reading candidates can become first appearances and chapter meanings. This is a reproduced bug. |
| High | [`epub.py`](../immersive/epub.py) strips whitespace and ruby readings, then concatenates paragraph text. Stored href, fragment and paragraph locations do not map every normalized character to an original DOM text node. | A reader cannot safely apply current offsets directly to its EPUB DOM. This limitation is already acknowledged in `reader-data.md`. |
| High | [`pipeline.py`](../immersive/pipeline.py) processes all book folders, rewrites source CSVs, renders PDFs and returns a ZIP path. [`render_outputs.py`](../immersive/render_outputs.py) saves reader JSON and adds derived fields while rendering. | Calling enrichment from a reader entails unrelated side effects and print work. |
| High | [`render.py`](../immersive/render.py) mixes indexes, grouping, card data, HTML and PDF types. [`context.py`](../immersive/context.py) imports its card functions. PDF libraries are imported even on paths intended to skip PDFs. | Core dictionary work cannot be cleanly deployed or tested independently of printing. |
| Medium | `context.py` trusts alignment when source ruby is absent and can select the first candidate/sense. | A dictionary gloss is not proof of the contextually correct sense. Ambiguity needs an explicit representation. |
| Medium | `lookup.json` maps spellings/readings to vocabulary IDs; token IDs cover exact spans only. | Tapping compounds and inflected phrases needs occurrence-based lookup, not just a token or spelling index. |
| Medium | `pipeline.py` executes top-level scripts through `runpy`. [`pyproject.toml`](../pyproject.toml) packages `immersive*`, not those scripts. | A normal wheel lacks paths expected at runtime. This is a static packaging finding; the portable bundle separately copies scripts. |
| Medium | Output is written in place; [`package_outputs.py`](../scripts/package_outputs.py) recursively packages existing output. The library index is rebuilt from the current invocation's books. | Failed builds can leave partial results; stale chapters can remain; a single-book operation can omit other books from navigation. |
| Medium | [`verify_outputs.py`](../scripts/verify_outputs.py) combines data/HTML/PDF checks, uses assertions, and expects print artifacts. Some tests depend on existing outputs and a particular book's counts. | Reader-only builds need independent validation; clean-checkout tests should not depend on private books or generated PDFs. |
| Lower | Indexes, study groups and card data are repeatedly reconstructed. UTF-16 prefix conversion and sentence searches repeat work. | Likely optimization opportunities, but profile before choosing performance targets. |

Other cleanup candidates: shared kana/kanji classification helpers; worker-owned tokenizer lifetimes; explicit resource paths for charts/fonts; validated configuration; source/schema version policy; dependency versions taken from the lockfile during release builds. These belong after the correctness and API boundaries above.

## Implementation sequence

Use one small pull request per step. Each PR must leave the existing study-pack command working. Do not combine architectural moves with visual PDF redesign. The module names below are suggestions; the acceptance checks are the requirements.

### 1. Add fixtures and fix reading compatibility

**Files:** `tests/`, `immersive/build.py`, `immersive/context.py`.

1. Add a fixture helper that writes a tiny valid EPUB and CSV to a temporary directory. Include two chapters, inline markup, source ruby, an inflected word, a repeated word and two dictionary entries with the same spelling but different readings. Use authored sample text so tests do not need either real book.
2. Turn the 人気 reproduction above into a failing regression test. Assert the incompatible ひとけ occurrence is not accepted through the normalized-token path.
3. Extract a shared candidate-acceptance function and call it from every matching path. Use available source ruby as the strongest evidence. Check readings consistently for uninflected matches; handle conjugations explicitly rather than comparing dictionary readings directly to inflected surface readings.
4. Preserve genuinely ambiguous candidates with an explicit reason/status. Do not solve ambiguity by taking the first CSV row. Distinguish dictionary senses from a reviewed contextual gloss.
5. Test that this fix does not remove legitimate conjugated or multi-token matches, and that rejected candidates do not influence `first_appearance`.

**Done when:** the regression fails before the fix and passes afterward; all matching paths share the acceptance policy; existing tests pass. Document any intentional changes to real-book match counts instead of updating expected counts blindly.

### 2. Extract shared data preparation from rendering

**Status: implemented.** Compatibility adapters remain for historical imports; new internal consumers use the separated modules. See [shared study data](study-data.md).

**Files:** `render.py`, `context.py`, `render_outputs.py`, `scripts/export_flashcards.py`, PDF modules.

1. Move sentence indexes, vocabulary grouping, learning sets and card-data factories into modules such as `indexes.py` and `study_data.py` that import no PDF or HTML renderer.
2. Keep plain text, structured ruby spans, source references and meanings in this model. Generate HTML strings in HTML adapters. Move PDF Flowables and font handling behind the PDF adapter.
3. Build indexes and the study model once per book. Pass them into consumers instead of recomputing them per chapter/export.
4. Change context preparation and card export to use the new modules. Temporary forwarding functions may preserve existing imports while migrating callers.
5. Make exporters read prepared data without modifying reader dictionaries or quality fields.

**Done when:** a subprocess can import and prepare reader/card data while imports of ReportLab, PyMuPDF and fontTools are deliberately blocked. Card IDs, set membership, examples and ruby match the baseline except for the deliberate step 1 correction. No PDF layout change is intended.

### 3. Define the reader bundle contract and source mapping

**Files:** `epub.py`, `build.py`, `reader.schema.json`, `docs/reader-data.md`; proposed `models.py` and `source_map.py`.

1. Document required book, chapter, sentence, token, occurrence, dictionary-entry and diagnostic fields. Use standard-library dataclasses or TypedDict where useful; do not add a framework solely for type definitions.
2. State that spans are half-open `[start, end)`. Name the coordinate system on every API: normalized Unicode code points, normalized UTF-16 units, or original DOM UTF-16 units. Never accept an unlabelled integer offset.
3. While parsing, record source text-node locations and mappings through whitespace removal, inline elements and ruby removal. Use a documented body-rooted node path or another reproducible locator. Store a range when normalization is many-to-one; excluded reading text must have a defined unmapped result.
4. Keep paragraph boundaries so matching cannot accidentally join the end of one paragraph to the beginning of another. Test navigation anchors that split sections within the same XHTML file.
5. Preserve existing chapter IDs initially. Document that book IDs currently represent EPUB bytes and chapter ordinals can change after navigation/configuration changes. Scope all identifiers to a book revision. If changing identity rules, introduce a versioned migration rather than silently reassigning IDs.
6. Extend schema coverage to generated furigana, primary meanings, provenance and diagnostics. Validate configuration before processing. Define an additive/breaking schema-version policy and reader behavior for unsupported versions.

**Done when:** fixture tests map tapped characters from original XHTML to the expected normalized spans and back, including whitespace, nested elements, `<ruby>`, supplementary Unicode characters and paragraph edges. Unmappable positions return an explicit result. Tests verify actual sliced text, not just numerical offset equality.

### 4. Implement lookup without a renderer

**Files:** proposed `lookup.py`, `indexes.py`, reader serialization and tests.

1. Build a per-chapter index of occurrence intervals. Include multi-token phrases and overlapping entries, not only `tokens[].vocabulary_ids`.
2. Provide positional lookup after source-map conversion and a separate text lookup by spelling, reading, lemma and known aliases. Return structured vocabulary/kanji/grammar candidates with source spans and provenance.
3. Define deterministic candidate ordering: exact spans and stronger reading evidence first, with ties retained. Return match methods/reasons rather than invented confidence percentages.
4. Expose dictionary senses as candidates unless a reviewed override supplies a contextual choice. Preserve the selected deck example; optionally return the tapped local sentence as a separate field, with its own ruby and provenance.
5. Keep first-appearance filtering entirely outside lookup. A word repeated in chapter 2 must still resolve there even when its only study card is in chapter 1.

**Done when:** lookup resolves repeated words, compounds, inflections and ambiguous entries correctly in fixtures; unknown words return a useful empty/unknown response; each tap does not scan the entire book dictionary.

### 5. Introduce one public enrichment API and unify commands

**Status: orchestration and validation implemented.** The concrete API is `run_job(..., options=BuildOptions(...))`, alongside the existing `enrich_book` API. Formats are independently selectable; both commands share the executor; installed-wheel and optimization checks have been exercised. Original/exported CSV provenance expansion remains outstanding. Interface sketches below describe the initial proposal rather than the implemented signatures; use the [current guide](orchestration-validation.md).

**Files:** `pipeline.py`, `cli.py`, `build.py`, `deck_enrichment.py`, `render_outputs.py`, `scripts/verify_outputs.py`, `scripts/export_flashcards.py`, `scripts/package_outputs.py`.

Proposed interfaces, not current APIs:

```python
enrich_book(epub_path, deck_paths, resources, config,
            progress=None, cancel=None) -> EnrichmentResult
lookup_at(bundle, chapter_id, normalized_offset_utf16) -> LookupResult
export_bundle(result, output_dir, options) -> ExportResult
```

1. Extract a single-book orchestration function returning data, diagnostics and fingerprints. Reader enrichment must not rewrite source files or require HTML, PDF, stroke assets or a ZIP.
2. Make CSV `first_appearance` writing an explicit operation. Study builds now require an explicit `--update-decks` flag; reader enrichment is read-only. Track original source-byte hashes separately from exported/rewritten CSV hashes in a future provenance expansion.
3. Add independent export selections for reader JSON, card JSON, HTML and PDF. Keep the existing full-study preset and its approved output names/layout.
4. Move reusable validation, card export and packaging functions into the installed `immersive` package. Make scripts thin command wrappers; remove runtime `runpy` and `sys.path` manipulation.
5. Route both CLI entry points through this orchestration. Support one selected book as well as all books. Preserve unrelated books in shared indexes.
6. Split reader schema/reference/span validation from optional PDF/HTML validation. Replace production `assert` checks with explicit validation errors. Treat unresolved readings as diagnostics where usable reader output remains possible; strict print validation can remain stricter.

**Done when:** a reader-only run succeeds without print dependencies/resources, leaves source hashes unchanged and emits no PDFs. The study preset still performs requested CSV enrichment and exports. Invalid input produces actionable errors, including under `python -O`.

### 6. Make jobs, paths and publication reliable

**Status: output publication, cancellation and publication coordination implemented.** See [guarantees, APIs, recovery and verification](reliable-publication.md). Explicit resource bundles, shared immutable dictionary stores, capability-specific checks and per-worker tokenizer ownership are now implemented. See [resource and worker APIs](shared-resources.md). Immutable release garbage collection is not implemented.

**Files:** orchestration, resource loading, doctor, packaging; proposed `resources.py` and `publication.py`.

1. Create an explicit resource bundle for dictionaries, grammar rules and optional print assets. Remove implicit current-directory and project-parent assumptions, including chart lookup. Split doctor checks by requested capability.
2. Emit structured stage/progress events and check cancellation between chapters and costly export steps. Own tokenizer instances per worker initially; do not assume a cached global tokenizer is safe across threads.
3. Write each job to a private staging directory. Validate before publishing an immutable versioned bundle. Atomically replace a small active-manifest file on the same filesystem; do not assume Windows can atomically replace a nonempty output directory.
4. Record the files actually produced in a manifest. Build ZIPs from that list, not a recursive sweep of old output. Clean only obsolete files owned by previous manifests, with path containment checks.
5. Serialize shared library-index updates. A cancelled/failed job must leave the last completed reader bundle usable. A PDF failure must not invalidate an independently completed reader bundle; report export status separately.

**Done when:** failure/cancellation tests retain the previous active bundle; removing a chapter removes its stale exported files safely; building one book retains another book's library entry; simultaneous jobs cannot overwrite each other's staging files or lose index entries. Repeat from a different working directory and paths with spaces.

### 7. Profile and add reproducible caching

**Status: implemented for this pipeline workstream.** Reader/resource content caches, validated reuse, provenance refresh, Unicode/sentence indexes and stage/memory profiling are available. See [measurements, APIs and limits](profiling-caching.md). Tap-lookup latency belongs to the separately maintained reader API; cache eviction remains future retention work.

**Files:** `build.py`, `furigana.py`, `kanjivg.py`, dictionary loaders and indexes.

1. Measure enrichment time, peak memory, export time and tap-lookup latency separately on the same small and larger inputs. Record environment and results before setting targets.
2. Replace repeated UTF-16 prefix encoding with a precomputed offset map, and repeated sentence scans with a sorted interval index. Reuse per-book study/index results from step 2.
3. Cache immutable dictionary/resource data by content fingerprint and format version. Avoid reparsing shared dictionaries and stroke archives for every book.
4. Key enrichment caches by EPUB bytes, semantic deck contents, configuration, dictionary/rule versions, schema version and pipeline version. Exclude the pipeline-derived `first_appearance` column from the semantic deck key while retaining full input-byte hashes for audit.
5. Invalidate only dependent outputs. A PDF spacing change should not require re-tokenizing a book. Write caches safely and allow a no-cache run for diagnosis.

**Done when:** cached and uncached data are equivalent; changing each semantic input invalidates the right stage; rewriting only `first_appearance` does not create a rebuild loop. Report measured improvement rather than promising a speculative speedup.

### 8. Package and document the reader integration boundary

**Files:** `pyproject.toml`, `scripts/build_portable.py`, `doctor.py`, README and docs.

1. Move print-only dependencies to an optional dependency group after import isolation works. Keep them included in the existing full portable ZIP.
2. Build releases from the frozen lockfile and record code/resource/dependency fingerprints and licenses. Verify a clean installed wheel as well as the portable distribution; an editable checkout can hide missing packaged files.
3. Document the Python API. If the future reader is not Python, provide a small subprocess command accepting explicit paths and producing structured JSON results. Reserve stdout for machine output and stderr for progress/errors. No web server is necessary for this boundary.
4. Include a tiny authored sample, commands for single-book reader-only enrichment and lookup, schema compatibility rules, error examples and a cancellation example.
5. Update `reader-data.md`, `card-data.md` and the portable guide to match implemented behavior. Remove temporary forwarding functions only after every caller has migrated.

**Done when:** both clean-wheel and extracted-portable tests enrich a fixture without network access, an installed Python on the portable target, private books or pre-existing outputs. Reader-only mode works without print dependencies; full-study mode verifies all expected supplementary artifacts.

## Verification checklist for each change

From the project root, the existing baseline command is:

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests
```

Add self-contained fixtures before changing behavior. Existing output-dependent tests remain useful as optional real-book integration checks, but should not be the only coverage. Avoid assertions tied to whichever book a filesystem glob returns first.

The final acceptance suite should cover offline enrichment; CSV-example preference and first-sentence fallback; source/generated furigana; homographs and conjugations; all-occurrence lookup; source-map Unicode boundaries; schema/reference integrity; unchanged source files in reader mode; cancellation/publication; changed working directory; clean installation; and portable packaging. A socket guard tests Python network use; also run release smoke tests with network disabled at the OS level.

For exporter refactoring, compare semantic card/JSON contents and render representative PDF pages: cover/reverse, a dense kanji page, chapter vocabulary, grammar and kana charts. Do not require identical PDF bytes, since metadata can differ. Do not regenerate the whole library just to edit this plan.

## Limits and decisions to defer

The pipeline can remain fully automatic without promising perfect semantic disambiguation. Grammar rule coverage, dictionary coverage and generated readings have known limits; expose them through diagnostics and optional human overrides, not an implicit model step. Existing overrides are data inputs, not a requirement to call an LLM.

Defer reader UI, progress tracking, synchronization, accounts and database selection. Consider a different storage format only after measurements show a need. Matching fixes and data/rendering separation have landed; remaining work in this pipeline workstream includes original/rewritten CSV provenance expansion, cache/release retention and release packaging. Explicit ambiguity metadata and source candidates are implemented; perfect semantic disambiguation is not promised. See [ambiguity handling](ambiguity-handling.md). Source mapping and reader lookup are handled separately.
