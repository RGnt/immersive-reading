# Reliable output publication

`run_job` and both build commands stage output privately, validate it, seal an immutable release, and atomically replace `output/.publication/active.json`. The pointer names a checksum-verified manifest. Readers using returned snapshot paths see one completed version, even if another job publishes later.

## Paths and consumer contract

The manifest separates each book's reader snapshot from its supplementary study snapshot. `BookResult.data_directory` contains the raw reader/lookup/quality JSON; `study_directory` contains the study export root, including prepared JSON, HTML, PDFs, layout metadata and assets. `reader_generation` and `study_generation` identify these versions. Study records also record the reader generation from which they were built. Reader-only updates retain the previous study snapshot.

Releases live under `.publication/releases/<generation>/`. Use returned paths, or `read_manifest(output)` followed by `snapshot_root(output, record)`. Do not cache a flat output path as an immutable version. Root `data`, `pdf`, `html`, card-library and ZIP paths are repairable compatibility views. Updating several view files is not atomic; edited or unknown files may prevent a view refresh. The HTML library links directly to immutable study pages. Flat data views may contain newer core reader files alongside older study metadata.

`JobResult` includes `manifest`, `generation`, `status`, `errors` and `warnings`. A document failure can publish a valid new reader while retaining the last successful study snapshot. This returns `status="partial"` and per-book export errors. The commands exit 1 for partial exports; the legacy `run_stages` adapter raises `ExportFailure` carrying the result. Failed card/ZIP exports preserve previous shared artifacts. Automatic ZIP generation is skipped after document/card failure. Standalone card and packaging commands report export failures rather than claiming the retained artifact was newly produced.

## Cancellation and concurrency

```python
from immersive.jobs import CancellationToken, CancelledError
from immersive.orchestration import BuildOptions, run_job

token = CancellationToken()
# A controller can call token.cancel() from another thread.
try:
    result = run_job(books, output, resources,
                     options=BuildOptions(), cancel=token)
except CancelledError:
    pass  # No uncommitted snapshot becomes active.
```

Cancellation is checked during enrichment, chapter/card exports, verification, lock waits and ZIP chunks. It is cooperative; an individual library call may finish first. Commands return 130 on cancellation. Cancellation before the pointer switch preserves the previous publication; a completed switch remains committed.

An operating-system lock serializes publication and shared-index updates. Separate books merge against the latest manifest under the lock. A competing change to the same selected book/channel raises `PublicationConflict`; rerun against the new state. Source EPUB/CSV/config changes during staging also reject publication. Explicit `--update-decks` changes input files before export and is not rolled back by output cancellation or failure. Tokenizer thread ownership and the explicit resource-bundle refactor remain separate work.

Private staging directories hold OS leases. A later job removes recognized abandoned stages after worker termination, while preserving live stages. Locks are released by the OS when a process exits. Sealed releases, including unselected releases left by an interrupted commit, are retained. Automatic release garbage collection is not implemented, so disk use grows with builds; do not delete a release while a caller or active manifest may reference it.

## Ownership, repair and migration

Manifests list file sizes and SHA-256 hashes. Cleanup only retires previously owned compatibility files whose bytes still match their recorded hash. Unknown or modified files are preserved with warnings. Paths are checked for traversal, containment and symlink/junction escapes. An interrupted compatibility refresh does not invalidate the selected snapshot.

```powershell
.venv\Scripts\python.exe -m immersive.publication status --output output
.venv\Scripts\python.exe -m immersive.publication repair --output output
```

The first publication can adopt validated legacy reader bundles and recognizable study files. Referenced assets are retained; recognized obsolete generated chapter files/assets can be retired. Unknown or incomplete material stays unowned. This is a conservative migration, not permission to clean arbitrary files from an output directory.

ZIPs enumerate manifest-owned artifacts, not the output tree. They contain a self-contained HTML index, study files, current raw readers under `reader-data/<book-id>/`, shared exports and `publication-manifest.json`. The archive manifest records hashes, reader/study generations, study input generations and card inputs. Old ZIPs, arbitrary notes, staging and unselected releases are excluded. Explicit packaging can combine the latest reader with a retained older study; their generations remain visible.

Standalone verification follows canonical snapshots and checks their inventories. Reports and previews produced by that verification go outside immutable releases (`reports/verification-latest.json` and `reports/validation-runs/`). Low-level `write_reader_data` and custom exporter destinations remain direct-write building blocks; use `run_job` for coordinated publication.

## Verification

All 85 tests passed. The tests cover pointer failures before/after switching, edited/unowned file preservation, stale cleanup, checksums, cancellation during packaging/staging/lock waits, terminated workers, same-book conflicts, two-process different-book merging, input changes, legacy assets, path/junction attacks, partial exports and view repair. A two-process stress test passed 40 repetitions. A full authored-book smoke produced and verified seven PDFs (37 pages), HTML, cards and a hash-checked ZIP; standalone verification preserved snapshot inventories. A subsequent reader-only update preserved the old study and ZIP; explicit packaging correctly recorded the mixed reader/study generations. Existing books and the distributed portable ZIP were not rebuilt.
