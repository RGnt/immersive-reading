# Testing Architecture & Synthetic Fixtures

This page documents how [`enrichement-extractor/tests/`](../enrichement-extractor/tests) is designed so you can write fast, self-contained unit and regression tests without depending on copyrighted EPUBs or pre-built `output/` directories.

---

## 1. Core Testing Principles

1. **Self-Contained & Private-Book Independent**: Every test in `tests/` constructs its own synthetic EPUB, CSV deck, KANJIDIC2 XML/GZ, Kanji alive CSV/provenance, and (when needed) KanjiVG/Yomitan ZIPs inside `tempfile.TemporaryDirectory()`. Tests never require the real books in `books/` or pre-existing files in `output/`.
2. **Fast PDF Mocking vs. Real PDF Verification**:
   - Most orchestration/publication/resource tests pass `print_resources=True` to `fixture()` and either run `mode="reader"`, `skip_pdf=True`, or mock `render_book` / `verify_pdfs` when testing state transitions.
   - Dedicated full-study integration tests in [`tests/test_pipeline.py`](../enrichement-extractor/tests/test_pipeline.py) and [`tests/test_yomitan.py`](../enrichement-extractor/tests/test_yomitan.py) use the real bundled `data/fonts/NotoSansJP-Regular.ttf` font to generate and verify actual 7-PDF study packs through ReportLab and PyMuPDF.
3. **Subprocess MetaPathFinder Guards**: To prove that `reader`, `html`, and `cards` paths do not accidentally import print libraries (`reportlab`, `pymupdf`, `fontTools`, `fitz`) or open network sockets (`socket.socket`), tests spawn a clean `subprocess.run([sys.executable, "-c", ...])` with a custom `sys.meta_path` finder that raises `AssertionError` if any forbidden module is imported.

---

## 2. The Canonical `fixture()` Helper

Defined in [`tests/test_reader_enrichment.py#L23-L60`](../enrichement-extractor/tests/test_reader_enrichment.py#L23-L60) and imported across the test suite (`from test_reader_enrichment import fixture, PROJECT`):

```python
folder, resources = fixture(
    root=Path(temp_dir),
    text=optional_xhtml_string,      # Defaults to 2 chapters (<h1 id="a">, <h1 id="b">) with <ruby>海<rt>うみ</rt></ruby>
    rows=optional_csv_rows_list,     # Defaults to [["海", "うみ", "sea", "海へ行く。"], ["見る", "みる", "see", ""]]
    print_resources=False,           # If True, also writes resources/kanjivg/source.zip and a placeholder font
)
```

### What `fixture(root, ...)` Creates on Disk
```text
<root>/
├── books/
│   └── sample book/                 # Returned as `folder`
│       ├── sample.epub              # Valid EPUB3 with container.xml, OPS/content.opf, OPS/nav.xhtml, OPS/text.xhtml
│       └── deck.csv                 # UTF-8 CSV with headers: Word, ReadingKana, Definitions, ExampleSentence
└── resources/                       # Returned as `resources`
    ├── kanjidic2.xml                # Minimal KANJIDIC2 XML with character 海 (カイ / うみ / sea)
    ├── kanjidic2.xml.gz             # Gzipped copy used by default ResourceBundle
    ├── kanji-alive/
    │   ├── ka_data.csv              # kanji,kmeaning -> 海,sea
    │   └── source.json              # Valid SHA-256 matching ka_data.csv
    ├── kanjivg/source.zip           # (Only if print_resources=True) Contains kanji/06d77.svg
    └── fonts/NotoSansJP-Regular.ttf # (Only if print_resources=True) Placeholder bytes for mocked PDF tests
```

### Helper for Multi-Paragraph Chapter XHTML (`document()`)
In [`tests/test_matching_regressions.py#L13-L14`](../enrichement-extractor/tests/test_matching_regressions.py#L13-L14):
```python
def document(paragraphs):
    return '<html><body><h1 id="a">第一章</h1>' + paragraphs + '<h1 id="b">第二章</h1><p>終わり。</p></body></html>'
```
Use this whenever testing tokenization, ruby priority, homographs, inflections, counters, or paragraph boundary behavior.

---

## 3. Enforcing Import & Network Isolation in Tests

See [`tests/test_reader_enrichment.py#L95-L125`](../enrichement-extractor/tests/test_reader_enrichment.py#L95-L125) and [`tests/test_study_data.py`](../enrichement-extractor/tests/test_study_data.py) for the subprocess guard pattern:

```python
import importlib.abc, sys
from unittest.mock import patch

class NoPrint(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname.split('.')[0] in {'reportlab', 'fontTools', 'pymupdf', 'fitz'}:
            raise AssertionError('Forbidden print library imported: ' + fullname)

sys.meta_path.insert(0, NoPrint())
with patch('socket.socket', side_effect=AssertionError('Network forbidden')):
    # Run enrich_book, prepare_study_data, export_book, or render_book(..., skip_pdf=True)
    ...
```
Why run this in a subprocess? Because if an earlier test in the same `unittest` process already imported `reportlab` or `pymupdf`, it is cached in `sys.modules` and `MetaPathFinder.find_spec` won't be called for it. A fresh subprocess guarantees true import isolation.

---

## 4. Test Matrix by Feature Area

| Feature / Subsystem | Test File | Key Scenarios Verified |
| :--- | :--- | :--- |
| **Vocabulary Matching & Reading Policy** | [`tests/test_matching_regressions.py`](../enrichement-extractor/tests/test_matching_regressions.py) | Homograph rejection (`人気` にんき vs ひとけ), EPUB `<ruby>` overriding Sudachi guess, context fallback protection, partial ruby in multi-token phrases (`気になる`), conjugations (`泳いだ`, `信じた`, colloquial `しょうがねえ`), numeric/Latin (`１匹`, `ＮＰＣ`), counter sound changes (`一本/ぽん`, `十回/じゅっかい`), paragraph edge isolation (`<p>人</p><p>気</p>`), spine fallback boundaries. |
| **Ambiguity & Sense Candidates** | [`tests/test_ambiguity.py`](../enrichement-extractor/tests/test_ambiguity.py) | Flattened CSV commas kept as single `source_definition_block`, multiple CSV rows merged, Yomitan glossary candidate indexing, `ambiguous_reading` vs `multiple_entries`, exclusion of rejected homograph readings from matched study cards, `context_glosses` reviewed override provenance. |
| **Yomitan Format-3 Dictionaries** | [`tests/test_yomitan.py`](../enrichement-extractor/tests/test_yomitan.py) | Format-3 `index.json` validation, `term_bank`, `kanji_bank`, `term_meta_bank` (`freq`, `pitch`, `ipa`), `tag_bank`, structured content plain-text extraction, notice files, CSV + Yomitan supplement mode, CSVless Yomitan-only EPUB mode, archive traversal/size/duplicate-member rejection. |
| **Atomic Publication & Concurrency** | [`tests/test_publication.py`](../enrichement-extractor/tests/test_publication.py) | `.publication/active.json` atomic pointer commits, `PublicationConflict` on same-book concurrent edits, multi-process different-book merge under `PublicationLock`, partial study failure preserving reader snapshot + old study snapshot, abandoned `.publication/staging/` cleanup, user-modified flat view preservation, symlink/junction escape rejection. |
| **Shared Resources & Workers** | [`tests/test_resources.py`](../enrichement-extractor/tests/test_resources.py) | Multi-threaded `WorkerContext` sharing one `ResourceStore` and one native Sudachi dictionary, `MappingProxyType` immutability of `store.kanjidic()`, cross-thread/cross-process misuse rejection, `ResourceChangedError` when a pinned file changes on disk, working-directory independence. |
| **Persistent Content Cache** | [`tests/test_cache.py`](../enrichement-extractor/tests/test_cache.py) | Byte-identical `reader.json` between uncached and cached runs, `first_appearance` column excluded from semantic key while `hydrate_reader()` refreshes file hashes, invalidation on EPUB/CSV/config/code/dependency changes, upgrade of unvalidated cache entry to validated, corrupt `.json.gz` graceful miss. |
| **Retention & Snapshot Leases** | [`tests/test_retention.py`](../enrichement-extractor/tests/test_retention.py) | `open_snapshot` and `lease_manifest` OS locks blocking deletion of leased generations, transitive reference protection (active manifest referencing older study generation), `keep` count and `min_age_seconds` protection, `seal.json` ownership check before deletion, cache age/size eviction. |
| **CSV Provenance & Deck Rewrites** | [`tests/test_provenance.py`](../enrichement-extractor/tests/test_provenance.py), [`tests/test_deck_enrichment.py`](../enrichement-extractor/tests/test_deck_enrichment.py) | Write-ahead `.enrichment/deck-provenance.json` ledger, `observed_input` -> `pipeline_export` -> `external_input` transitions, UTF-8 BOM and CRLF/LF preservation, rejection if CSV changed since enrichment. |
| **Validation & Schema Checks** | [`tests/test_validation.py`](../enrichement-extractor/tests/test_validation.py) | `validate_config` unknown keys, `validate_reader` broken references, out-of-bounds spans, UTF-16 offset mismatches, overlapping ruby, unsorted chapters, active checks under `python -O`. |
| **Study Data, Context, Furigana & Examples** | [`tests/test_study_data.py`](../enrichement-extractor/tests/test_study_data.py), [`tests/test_context.py`](../enrichement-extractor/tests/test_context.py), [`tests/test_furigana.py`](../enrichement-extractor/tests/test_furigana.py), [`tests/test_examples.py`](../enrichement-extractor/tests/test_examples.py), [`tests/test_kanji_meanings.py`](../enrichement-extractor/tests/test_kanji_meanings.py), [`tests/test_library_index.py`](../enrichement-extractor/tests/test_library_index.py) | `StudyData` immutability, 20-word set numbering, kanji context ranking, okurigana exclusion from ruby bases, CSV-first example selection, Kanji alive SHA-256 verification and KANJIDIC2 fallback, multi-book `output/html/index.html` preservation. |

