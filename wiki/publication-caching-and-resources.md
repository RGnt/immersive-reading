# Publication, Caching & Resources

This page details how [`enrichement-extractor`](../enrichement-extractor) manages shared resources across threads/processes, caches enriched data safely, publishes immutable snapshots atomically, tracks CSV rewrite provenance, and prunes old releases.

---

## 1. Resource Ownership & Thread Safety (`immersive/resources.py` & `tokenization.py`)

### Three-Tier Ownership Hierarchy

| Class | Scope & Thread Safety | Responsibilities |
| :--- | :--- | :--- |
| **[`ResourceBundle`](../enrichement-extractor/immersive/resources.py#L48-L90)** | Immutable (`@dataclass(frozen=True)`), shareable anywhere | Resolves and holds canonical `Path` objects for `kanjidic`, `kanji_alive`, `grammar`, `sudachi_dictionary`, `sudachi_user_dictionaries`, `kanjivg`, `font`, `charts`, and `yomitan`. |
| **[`ResourceStore`](../enrichement-extractor/immersive/resources.py#L101-L350)** | **One process** (`os.getpid()` checked), **thread-safe** (`RLock`) | Lazily loads and pins SHA-256 + file stat (`size, mtime_ns, ctime_ns, ino`) of resources. Freezes parsed dicts (`kanjidic`, `primary_meanings`, `grammar`, `kanjivg_glyphs`) using [`freeze()`](../enrichement-extractor/immersive/resources.py#L21-L28) (`MappingProxyType` + `tuple`) so callers can never mutate shared state. Holds one shared native `sudachipy.dictionary.Dictionary`. |
| **[`WorkerContext`](../enrichement-extractor/immersive/tokenization.py#L9-L66)** | **Single thread** (`threading.current_thread()` checked) | Created via `store.worker()`. Owns thread-local Sudachi `A`, `B`, `C` tokenizer instances and a bounded 8,192-entry `OrderedDict` LRU ruby cache. |

### Key Lifecycle Rules
- **Pinning & `ResourceChangedError`**: The first time `store.pin(path)` reads a file, it records `(stat_tuple, {file, sha256, size})`. If a subsequent `pin(path)` or `store.verify_current()` detects a changed stat or SHA-256, it raises [`ResourceChangedError`](../enrichement-extractor/immersive/resources.py#L92-L93) instead of silently mixing old and new resources.
- **Closing Order**: All `WorkerContext` instances must be closed (`store._workers == 0`) before calling `store.close()`, otherwise `RuntimeError("Close worker contexts before closing their ResourceStore")` is raised.
- **Borrowing Helper**: Use `with resource_worker(resources, worker=worker, cache=cache, **overrides) as active:` ([`resources.py#L353-L386`](../enrichement-extractor/immersive/resources.py#L353-L386)) to seamlessly accept an existing `WorkerContext`, borrow a `ResourceStore`, wrap a `ResourceBundle`, or construct a store from a directory path.

---

## 2. Persistent Content Cache (`immersive/cache.py`)

[`ContentCache`](../enrichement-extractor/immersive/cache.py#L42-L140) stores compressed, checksummed JSON artifacts under `.cache/immersive/` (by default beside `output/`):

- **Two Namespaces**:
  1. `resource/<key[:2]>/<key>.json.gz`: Caches parsed `kanjidic`, `primary_meanings`, and `grammar` structures keyed by input file `pin()` descriptors + `code_revision(("build.py", "kanji_meanings.py", "resources.py", "validation.py", "cache.py"))`.
  2. `reader/<key[:2]>/<key>.json.gz`: Caches enriched book dictionaries keyed by [`reader_inputs(folder, store)`](../enrichement-extractor/immersive/cache.py#L142-L177).
- **What `reader_inputs()` Includes in the Cache Key**:
  - `CACHE_FORMAT` (`1`), `pipeline_version` (`__version__`), `schema_version` (`"1.0.0"`).
  - EPUB filename & SHA-256.
  - Semantic CSV contents: column names and row dicts **excluding** the derived `first_appearance` column!
  - `study.config.json` contents.
  - `store.reader_manifest()` (hashes of KANJIDIC2, Kanji alive, grammar rules, Sudachi system/user dicts, Yomitan ZIPs).
  - `code_revision(READER_FILES)` (SHA-256 of all 19 core Python/schema files listed in `READER_FILES`).
  - Python version tuple, implementation, Unicode version, and versions of `sudachipy`, `sudachidict_core`, `beautifulsoup4`, `lxml`, `jsonschema`.
- **Why `hydrate_reader()` Exists** ([`cache.py#L180-L186`](../enrichement-extractor/immersive/cache.py#L180-L186)):
  - Because `first_appearance` is excluded from the semantic cache key, updating a CSV with `--update-decks` does not cause a cache miss on the next run. Instead, `hydrate_reader(book, files, records)` updates `book["source_files"]` and `deck_records[].fields` with the current on-disk hashes and CSV row values while reusing the cached analysis.

---

## 3. Atomic Staged Publication (`immersive/publication.py`)

[`Publication`](../enrichement-extractor/immersive/publication.py#L280-L646) guarantees that consumers never observe partially written or inconsistent outputs.

### Directory Layout Under `output/`
```text
output/
├── .publication/
│   ├── publish.lock                 # OS file lock (msvcrt / fcntl) serializing commits
│   ├── active.json                  # Atomic pointer: {schema_version: 1, generation, manifest, sha256}
│   ├── staging/<generation>/        # Private work area with .lease.lock & job.json
│   ├── leases/<generation>/*.lock   # Active reader leases preventing GC
│   └── releases/<generation>/       # Immutable committed release
│       ├── manifest.json            # Full inventory of books, snapshots, views, history, retired
│       ├── seal.json                # {generation, manifest_sha256} verifying release completeness
│       ├── books/<book-id>/reader/  # Immutable reader snapshot (data/<bid>/{reader,quality,lookup}.json)
│       ├── books/<book-id>/study/   # Immutable study snapshot (data/, html/, pdf/, previews/, reports/)
│       └── shared/                  # Shared flashcards/library.json, study-pack.zip, HTML redirects
├── data/                            # Repairable flat compatibility views
├── html/                            # Repairable flat compatibility views (redirects to immutable study snapshot)
├── pdf/                             # Repairable flat compatibility views
├── previews/                        # Repairable flat compatibility views
├── reports/                         # Repairable flat compatibility views
└── study-pack.zip                   # Repairable flat compatibility view
```

### Commit Sequence in `Publication.publish()`
1. Acquires `.publication/publish.lock` via [`PublicationLock`](../enrichement-extractor/immersive/publication.py#L194-L245).
2. Reads current `active.json` manifest (or adopts recognizable legacy outputs on first run via `_legacy()`).
3. Verifies no concurrent job updated the same `(book_id, kind)` since this job started (`_check_conflicts()`); otherwise raises [`PublicationConflict`](../enrichement-extractor/immersive/publication.py#L28-L29).
4. Exports shared flashcards (`library.json`), aggregates `verification.json` reports, and builds `study-pack.zip` (if requested and no export errors occurred).
5. Computes updated flat `views`, `history` (all SHA-256 hashes ever published by this pipeline for each view path), and `retired` views.
6. Writes `manifest.json` and `seal.json` inside `.publication/staging/<generation>/`.
7. Runs `precommit=unchanged_inputs()` to verify no source EPUB, CSV, config, or dictionary resource changed while staging.
8. Releases `.lease.lock`, renames `.publication/staging/<generation>` to `.publication/releases/<generation>`, and atomically replaces `.publication/active.json`.
9. Calls [`refresh_views(output, state)`](../enrichement-extractor/immersive/publication.py#L649-L688) to copy new files to flat compatibility paths and remove retired files **only if their on-disk SHA-256 matches `history`/`retired`** (user-modified or unknown files are preserved with a warning!).

---

## 4. CSV Rewrite Provenance (`immersive/provenance.py` & `deck_enrichment.py`)

When [`enrich_decks(book, folder)`](../enrichement-extractor/immersive/deck_enrichment.py#L11-L15) updates `first_appearance` in `books/<book>/*.csv`:
1. Acquires `books/<book>/.enrichment/deck-update.lock`.
2. Verifies the CSV's current SHA-256 matches the hash observed during enrichment.
3. Calls [`record_rewrite(folder, path, original_bytes, updated_bytes)`](../enrichement-extractor/immersive/provenance.py#L44-L54) **before** replacing the CSV file (write-ahead intent log in `books/<book>/.enrichment/deck-provenance.json`).
4. Writes the updated CSV to a temp file and atomically replaces `path` via `os.replace`.
5. Subsequent calls to [`source_descriptor(path)`](../enrichement-extractor/immersive/provenance.py#L28-L41) match the file's current SHA-256 against the ledger's `exported_sha256` to trace `original_sha256` across multiple pipeline rewrites (`provenance_status: "pipeline_export"`). If a user edits the CSV externally, its hash won't match `exported_sha256` and it begins a new lineage (`provenance_status: "external_input"`).

---

## 5. Snapshot Leases & Garbage Collection (`immersive/retention.py`)

- **Reading Immutable Snapshots Safely**:
  ```python
  from immersive.retention import open_snapshot
  with open_snapshot(output_dir, book_id, kind="reader") as snapshot_dir:
      reader_json_path = snapshot_dir / "data" / book_id / "reader.json"
  ```
  `open_snapshot()` and `lease_manifest()` hold OS file locks under `.publication/leases/<generation>/<uuid>.lock`. If a reader process crashes, the OS automatically releases the lock.
- **Pruning Old Releases (`prune_releases`)**:
  - Defaults to `keep=3`, `min_age_seconds=86400`, `apply=False` (dry run).
  - Protects: the active generation, all generations referenced transitively by protected manifests (e.g., an older `study` snapshot retained across newer `reader` builds), the `keep` newest releases, any leased generation (`_leased()`), releases younger than `min_age_seconds`, and any release whose files fail `seal.json` / `release_files` verification (`_owned_release()`).
- **Pruning Cache (`prune_cache`)**:
  - Defaults to `max_age_seconds=30*86400` (30 days), `max_bytes=1024**3` (1 GiB), `apply=False`.
  - Evicts oldest valid `.json.gz` cache files first until both age and total byte size are within budget. Unknown or corrupt files are preserved rather than deleted blindly.

