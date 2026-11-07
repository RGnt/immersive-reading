# Playbooks & Invariants

This page provides actionable checklists, architectural guardrails, and step-by-step playbooks for common development tasks in [`enrichement-extractor`](../enrichement-extractor).

---

## 1. Golden Rules & Guardrails Checklist

Before finalizing any code change in `enrichement-extractor`, verify these 10 rules:

1. **Offline & Deterministic**: Did you introduce any network requests, LLM calls, or non-deterministic ordering? *(Must be No. Use `dict.fromkeys` or `sorted` rather than unordered `set` iteration when serializing lists.)*
2. **No Story Translations**: Are all story sentences and examples kept strictly in Japanese? *(English is only for word glosses, kanji meanings, and grammar explanations.)*
3. **Print Import Isolation**: Can `enrich_book()`, `prepare_study_data()`, `export_book()`, `render_book(..., skip_pdf=True)`, and `verify(..., pdf=False)` run when `reportlab`, `pymupdf`, `fontTools`, and `PIL` are uninstalled or blocked? *(Test in [`tests/test_resources.py`](../enrichement-extractor/tests/test_resources.py) and [`tests/test_study_data.py`](../enrichement-extractor/tests/test_study_data.py) blocks these imports via `sys.modules`.)*
4. **Input Immutability**: Does `enrich_book` or default `build` leave `books/<book>/*.epub` and `*.csv` completely untouched?
5. **Cache Code Revision List**: If you added a new module that affects reader enrichment, did you add its filename to `READER_FILES` in [`immersive/cache.py#L24-L26`](../enrichement-extractor/immersive/cache.py#L24-L26)?
6. **`ResourceStore` Immutability**: If you added data to `ResourceStore`, is it wrapped in [`freeze()`](../enrichement-extractor/immersive/resources.py#L21-L28) when stored and [`thaw()`](../enrichement-extractor/immersive/resources.py#L31-L36) when copied into a mutable `book` dict?
7. **`WorkerContext` Thread Affinity**: Are all Sudachi `tokenize()` and `ruby()` calls routed through the active `worker` (`WorkerContext`) rather than creating ad-hoc global tokenizers?
8. **Cooperative Cancellation**: Do new loops or file-chunk readers call [`check_cancelled(cancel)`](../enrichement-extractor/immersive/jobs.py#L20-L22)?
9. **Optimization-Safe Checks**: Did you use [`require(condition, message, path)`](../enrichement-extractor/immersive/validation.py#L15-L18) instead of `assert` so validation stays active under `python -O`?
10. **Path Containment**: Do any new output/cache/manifest path joins pass through [`contained(root, relative_name)`](../enrichement-extractor/immersive/publication.py#L56-L67)?

---

## 2. Step-by-Step Task Playbooks

### Playbook A: Adding or Modifying a Field in `reader.json`
1. **Compute the field** in [`immersive/build.py`](../enrichement-extractor/immersive/build.py) (or a helper module called by `build_data()`).
   - If it is a study-export-only field (like `learning_sets` or `stroke_order`), attach it in [`study_reader_data()`](../enrichement-extractor/immersive/study_data.py#L80-L88) or [`add_stroke_order()`](../enrichement-extractor/immersive/kanjivg.py#L19-L42), **not** in `reader`-only enrichment.
2. **Update JSON Schema** in [`immersive/reader.schema.json`](../enrichement-extractor/immersive/reader.schema.json).
   - Note that many objects in `reader.schema.json` set `"additionalProperties": false`, so undeclared properties will immediately fail schema validation!
3. **Add semantic checks** in [`immersive/validation.py`](../enrichement-extractor/immersive/validation.py) inside `validate_reader(book)` (e.g., verifying IDs, spans, or cross-references).
4. **Check Cache Hydration**: If the field depends on raw on-disk file bytes that could change via `--update-decks`, update [`hydrate_reader()`](../enrichement-extractor/immersive/cache.py#L180-L186).
5. **Run validation & caching tests**:
   ```powershell
   .venv\Scripts\python.exe -m unittest tests.test_validation tests.test_reader_enrichment tests.test_cache -v
   ```

---

### Playbook B: Modifying Vocabulary Matching or Reading Verification
1. **Locate the policy**: All three matching paths (`morphology`, `normalized_token_sequence`, `token_boundary_phrase` in [`build.py#L198-L225`](../enrichement-extractor/immersive/build.py#L198-L225)) funnel through [`MatchPolicy.accept()`](../enrichement-extractor/immersive/matching.py#L70-L106).
2. **Edit [`immersive/matching.py`](../enrichement-extractor/immersive/matching.py)** (and [`immersive/context.py`](../enrichement-extractor/immersive/context.py) if kanji context alignment is also affected).
3. **Write a synthetic regression test** in [`tests/test_matching_regressions.py`](../enrichement-extractor/tests/test_matching_regressions.py) using the in-memory EPUB/CSV builder helper before changing behavior.
4. **Verify**:
   - Confirm paragraph boundaries (`policy.contains(start, end)`) are still enforced.
   - Confirm EPUB source ruby still takes precedence over Sudachi token readings.
   - Confirm rejected homographs do not leak into `first_appearance` or `grouped_vocabulary()` study cards.
   ```powershell
   .venv\Scripts\python.exe -m unittest tests.test_matching_regressions tests.test_ambiguity tests.test_context -v
   ```

---

### Playbook C: Adding or Editing Grammar Rules
1. Open [`immersive/grammar.json`](../enrichement-extractor/immersive/grammar.json).
2. Each entry is an object with:
   - `"id"`: Unique string identifier (e.g., `"g-te-iru"`).
   - `"label"`: Display title (e.g., `"〜ている"`).
   - `"meaning"`: Short English construction gloss.
   - `"note"`: English explanation of formation/usage.
   - Matching specification (choose one):
     - `"regex"`: Valid Python regular expression matched within each paragraph (`re.finditer(rule["regex"], paragraph["text"])`).
     - `"token": "particle"` and `"form": "<particle>"`: Matches Sudachi mode A tokens where `t["surface"] == rule["form"]` and `t["pos"][0] == "助詞"`.
     - `"token": "desire"`: Matches Sudachi mode A tokens where `t["lemma"] == "たい"` and `t["pos"][0] == "助動詞"`.
3. [`ResourceStore.grammar()`](../enrichement-extractor/immersive/resources.py#L190-L212) validates rule uniqueness, required string fields, and regex compilation automatically.

---

### Playbook D: Modifying PDF Layouts (`kanji_pdf.py` / `master_pdf.py`)
1. Remember that [`immersive/pdf_validation.py`](../enrichement-extractor/immersive/pdf_validation.py) enforces strict geometric and structural invariants on every generated PDF:
   - Total PDFs = `2 * len(chapters) + 3`.
   - Page 1 must contain `"COPYRIGHT / LICENSE NOTICES"` and end above `18 mm` (`bottom >= 18*mm`).
   - Page 2 must be completely blank (no text, drawings, or images).
   - Kanji pages (`pdf_page == i + 3`) must have `1..4` entries, `practice_rows == len(entries)`, `practice_bbox` strictly inside `bbox`, `>= 6.9 mm` vertical gap between consecutive cards, and `y0 >= 18 mm`.
   - `vocabulary-master.pdf` must have exactly `len(learning_sets) + 2 + chart_count` pages (1 page per set).
   - All text spans on content pages must stay inside `[x0 >= 20, y0 >= 15, x1 <= width - 18, y1 <= height - 12]` (in PDF points).
   - No drawing may have a fill with area `>= 1000` pt² (white background rule).
   - All Japanese fonts must be embedded (`doc.extract_font(font[0])[3]` non-empty).
2. Always wrap ReportLab font registration and rendering in [`with pdf_session(font_path):`](../enrichement-extractor/immersive/pdf_elements.py#L25-L30).
3. Test with [`tests/test_pipeline.py`](../enrichement-extractor/tests/test_pipeline.py) and inspect contact sheets under `output/previews/<book-id>/`.

---

### Playbook E: Embedding `enrichement-extractor` in a Downstream Reader App

#### Option 1: Direct Python API (Recommended for Python Reader/Backend)
```python
from pathlib import Path
from immersive.cache import ContentCache
from immersive.resources import ResourceBundle, ResourceStore
from immersive.orchestration import BuildOptions, run_job
from immersive.retention import open_snapshot
from immersive.validation import load_json

# 1. Initialize shared store & cache once at application startup
bundle = ResourceBundle.from_directory(Path("enrichement-extractor/data"))
cache = ContentCache(Path(".cache/immersive"))

with ResourceStore(bundle, cache=cache) as store:
    # 2. Enrich and publish a reader-only snapshot (fast, thread-safe, no PDFs)
    result = run_job(
        books=Path("enrichement-extractor/books"),
        book_folder=Path("enrichement-extractor/books/my-book"),
        output=Path("enrichement-extractor/output"),
        resources=store,
        options=BuildOptions(mode="reader", validate=True),
        cache=cache,
    )
    book_id = result.books[0].book_id

# 3. Read the published snapshot safely under an OS lease lock
with open_snapshot(Path("enrichement-extractor/output"), book_id, kind="reader") as root:
    reader_data = load_json(root / "data" / book_id / "reader.json")
    lookup_data = load_json(root / "data" / book_id / "lookup.json")
```

#### Option 2: Subprocess CLI Boundary (For Non-Python Readers: Electron / Tauri / Web / Rust)
Invoke `immersive.cli enrich` and parse its single-line JSON output from `stdout`:
```powershell
.venv\Scripts\python.exe -m immersive.cli enrich --book "books/my-book" --resources "data" --output "output"
```
- **Exit Code `0`**: Success. Parse each line of `stdout` as JSON:
  - `output`: Path to the immutable snapshot's `data/<book-id>` directory containing `reader.json`, `lookup.json`, `quality.json`.
  - `reader_generation`: 32-hex generation ID.
  - `diagnostics`: Array of nonfatal warning objects.
- **Exit Code `1`**: Input/validation or partial export error (details in JSON on `stderr`).
- **Exit Code `130`**: Cancelled via `SIGINT` / `CancellationToken` (no uncommitted data published).

---

## 3. Complete Verification Command Reference

Run all commands from [`k:\immersive-reading\enrichement-extractor`](../enrichement-extractor):

```powershell
# Run all 19 unit & regression test files
.venv\Scripts\python.exe -m unittest discover -s tests -v

# Run a single test module
.venv\Scripts\python.exe -m unittest tests.test_matching_regressions -v

# Verify production checks remain active under Python optimization (-O)
.venv\Scripts\python.exe -O -m unittest tests.test_validation -v

# Check runtime & resource health (reader-only vs full study)
.venv\Scripts\python.exe -m immersive.pipeline --doctor --reader-only
.venv\Scripts\python.exe -m immersive.pipeline --doctor

# Inspect active publication manifest or repair flat compatibility views
.venv\Scripts\python.exe -m immersive.publication status --output output
.venv\Scripts\python.exe -m immersive.publication repair --output output

# Benchmark enrichment & cache performance (writes to tmp/profile.json)
.venv\Scripts\python.exe -m immersive.profiling --book "books/<book_name>" --resources data --report tmp/profile.json --runs 2

# Build standalone Windows x64 portable ZIP in dist/
.venv\Scripts\python.exe scripts/build_portable.py
```

