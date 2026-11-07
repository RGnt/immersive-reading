# Retention and snapshot leases

Retention is explicit and defaults to a dry run. It does not run automatically during enrichment or remove existing book inputs.

```powershell
.venv\Scripts\python.exe -m immersive.retention --output output --cache-dir .cache/immersive
# Apply the same policy: three recent generations, minimum release age one day,
# cache age at most 30 days and a 1 GiB cache budget.
.venv\Scripts\python.exe -m immersive.retention --output output --cache-dir .cache/immersive --apply
```

Active manifest references, retained recent generations, their transitive dependencies and leased snapshots are protected. Consequently more than the requested number of generations may remain. Collection checks sealed release manifests and complete payload inventories. Legacy releases without these ownership records, modified files and unknown files are preserved. It never follows a symlink/junction to delete external content. Abandoned staging recovery remains separate.

Long-lived readers should resolve and lease a snapshot before consuming immutable paths:

```python
from immersive.retention import open_snapshot
with open_snapshot(output, book_id, kind="reader") as root:
    reader_path = root / "data" / book_id / "reader.json"
    # Keep the lease while using this generation.
```

`lease_manifest(output, manifest=None)` leases all roots of a publication. OS-owned lease locks expire when a process exits; stale unlocked lease files do not pin data forever. Verification and published card exports use leases automatically. Standalone returned paths are not permanent retention pins: reacquire a current snapshot or lease a retained manifest for long-lived use. Close the context when finished.

Cache cleanup only recognizes valid checksummed cache entries; unknown/corrupt files are preserved. Cache hits refresh last-use modification time. Reads/writes and eviction coordinate the short filesystem operations under an OS lock; decompression and analysis happen outside it. Temporary files are not evicted as entries. The cache is replaceable; release payloads are retained unless their ownership is verified.

No existing published outputs were collected while implementing this feature. Tests apply policies only to disposable fixtures and cover active references, old leases, modified/unknown files, dry runs and cache ownership.
