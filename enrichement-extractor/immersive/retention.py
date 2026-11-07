"""Conservative cache eviction and release collection with reader leases."""
import argparse
from contextlib import contextmanager, ExitStack
import json
import math
from pathlib import Path
import shutil
import time
import uuid

from .publication import (PublicationLock, PublicationError, GENERATION, canonical_root,
                          contained, file_info, read_manifest, snapshot_root, relative_name)
from .validation import load_json


def referenced_generations(manifest):
    generations = {manifest["generation"]}
    def walk(value):
        if isinstance(value, dict):
            for child in value.values(): walk(child)
        elif isinstance(value, list):
            for child in value: walk(child)
        elif isinstance(value, str) and value.startswith(".publication/releases/"):
            parts = relative_name(value).parts
            if len(parts) < 4 or not GENERATION.fullmatch(parts[2]):
                raise PublicationError("Invalid release reference")
            generations.add(parts[2])
    walk(manifest)
    return generations


@contextmanager
def lease_manifest(output, manifest=None):
    """Resolve and lease immutable roots before allowing publication/GC to run."""
    output = canonical_root(output)
    paths = []
    with ExitStack() as leases:
        with PublicationLock(output):
            manifest = read_manifest(output) if manifest is None else manifest
            if manifest is not None:
                for generation in sorted(referenced_generations(manifest)):
                    if not contained(output, f".publication/releases/{generation}/manifest.json").is_file():
                        raise PublicationError(f"Requested snapshot has been retired: {generation}")
                    name = f".publication/leases/{generation}/{uuid.uuid4().hex}.lock"
                    leases.enter_context(PublicationLock(output, lock_name=name))
                    paths.append(name)
        try:
            yield manifest
        finally:
            leases.close()
            with PublicationLock(output):
                for name in paths:
                    contained(output, name).unlink(missing_ok=True)


@contextmanager
def open_snapshot(output, book_id, *, kind="reader"):
    if kind not in {"reader", "study"}:
        raise ValueError("Snapshot kind must be reader or study")
    with lease_manifest(output) as manifest:
        if manifest is None or book_id not in manifest["books"] or kind not in manifest["books"][book_id]:
            raise PublicationError("Requested book snapshot is unavailable")
        yield snapshot_root(output, manifest["books"][book_id][kind])


def _leased(output, generation):
    folder = contained(output, f".publication/leases/{generation}")
    if not folder.exists():
        return False
    for path in folder.iterdir():
        if not path.name.endswith(".lock") or not GENERATION.fullmatch(path.stem):
            return True  # Unknown lease files are preserved conservatively.
        try:
            with PublicationLock(output, timeout=0, lock_name=path.relative_to(output).as_posix()):
                pass
        except PublicationError:
            return True
    return False


def _owned_release(output, generation, manifest):
    expected = manifest.get("release_files")
    if not isinstance(expected, dict):
        return False
    root = contained(output, f".publication/releases/{generation}")
    seal = root / "seal.json"
    if not seal.is_file() or load_json(seal) != {"generation": generation, "manifest_sha256": file_info(root / "manifest.json")["sha256"]}:
        return False
    actual = {}
    for path in root.rglob("*"):
        checked = contained(root, path.relative_to(root).as_posix())
        if checked.is_file() and checked.name not in {"manifest.json", ".lease.lock", "seal.json"}:
            actual[checked.relative_to(root).as_posix()] = file_info(checked)
    lock = root / ".lease.lock"
    if lock.exists() and lock.read_bytes() != b"0":
        return False
    return actual == expected


def prune_releases(output, *, keep=3, min_age_seconds=86400, apply=False, now=None):
    if keep < 0 or not math.isfinite(min_age_seconds) or min_age_seconds < 0:
        raise ValueError("Retention limits must be nonnegative")
    output, now = canonical_root(output), time.time() if now is None else now
    result = {"candidates": [], "removed": [], "preserved": []}
    if not (output / ".publication/active.json").is_file():
        return result
    with PublicationLock(output):
        active = read_manifest(output)
        protected = referenced_generations(active)
        releases = {}
        parent = contained(output, ".publication/releases")
        for path in parent.iterdir():
            if not GENERATION.fullmatch(path.name):
                continue
            root = contained(output, f".publication/releases/{path.name}")
            try:
                manifest = load_json(contained(root, "manifest.json"))
                if manifest.get("schema_version") != 1 or manifest.get("generation") != path.name:
                    raise ValueError("Unrecognized manifest")
                referenced_generations(manifest)
                releases[path.name] = (root, manifest)
            except (ValueError, OSError):
                result["preserved"].append({"generation": path.name, "reason": "unknown_manifest"})
        newest = sorted(releases, key=lambda g: releases[g][0].stat().st_mtime, reverse=True)[:keep]
        protected.update(newest)
        for generation in releases:
            if _leased(output, generation):
                protected.add(generation)
        # Retained historic manifests can point to older per-book/shared roots.
        pending = list(protected)
        while pending:
            generation = pending.pop()
            if generation in releases:
                for dependency in referenced_generations(releases[generation][1]) - protected:
                    protected.add(dependency); pending.append(dependency)
        for generation, (root, manifest) in releases.items():
            if generation in protected:
                result["preserved"].append({"generation": generation, "reason": "active_recent_or_leased"})
                continue
            if now-root.stat().st_mtime < min_age_seconds:
                result["preserved"].append({"generation": generation, "reason": "young"})
                continue
            try:
                owned = _owned_release(output, generation, manifest)
            except (ValueError, OSError):
                owned = False
            if not owned:
                result["preserved"].append({"generation": generation, "reason": "legacy_unknown_or_modified_files"})
                continue
            result["candidates"].append(generation)
            if apply:
                checked = contained(output, f".publication/releases/{generation}")
                if not checked.resolve().is_relative_to(parent.resolve()):
                    raise PublicationError("Release cleanup escaped its target directory")
                shutil.rmtree(checked)
                result["removed"].append(generation)
    return result


def prune_cache(directory, *, max_age_seconds=30*86400, max_bytes=1024**3, apply=False, now=None):
    if not math.isfinite(max_age_seconds) or max_age_seconds < 0 or max_bytes < 0:
        raise ValueError("Cache retention limits must be nonnegative")
    from .cache import ContentCache
    cache = ContentCache(directory)
    result = {"candidates": [], "removed": [], "preserved": []}
    if not cache.directory.exists():
        return result
    now = time.time() if now is None else now
    with PublicationLock(cache.directory, lock_name=".retention.lock"):
        entries = []
        for namespace in ("reader", "resource"):
            folder = contained(cache.directory, namespace)
            if not folder.exists(): continue
            for path in folder.glob("*/*.json.gz"):
                key = path.name.removesuffix(".json.gz")
                try:
                    checked = cache.path(namespace, key)
                    if checked != path or checked.parent.name != key[:2] or cache._read(namespace, key) is None:
                        continue
                    entries.append((checked.stat().st_mtime, checked.stat().st_size, checked))
                except (ValueError, OSError):
                    result["preserved"].append(str(path.relative_to(cache.directory)))
        total = sum(size for _, size, _ in entries)
        for stamp, size, path in sorted(entries):
            if now-stamp <= max_age_seconds and total <= max_bytes: continue
            name = path.relative_to(cache.directory).as_posix()
            result["candidates"].append(name); total -= size
            if apply:
                contained(cache.directory, name).unlink()
                result["removed"].append(name)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=Path("output"))
    parser.add_argument("--cache-dir", type=Path)
    parser.add_argument("--keep-releases", type=int, default=3)
    parser.add_argument("--min-age-days", type=float, default=1)
    parser.add_argument("--cache-max-days", type=float, default=30)
    parser.add_argument("--cache-max-mib", type=float, default=1024)
    parser.add_argument("--apply", action="store_true", help="Apply the computed plan; default is dry-run")
    args = parser.parse_args(argv)
    if args.keep_releases < 0 or any(not math.isfinite(v) or v < 0 for v in (args.min_age_days, args.cache_max_days, args.cache_max_mib)):
        parser.error("Retention limits must be finite and nonnegative")
    result = {"dry_run": not args.apply, "releases": prune_releases(args.output, keep=args.keep_releases, min_age_seconds=args.min_age_days*86400, apply=args.apply)}
    if args.cache_dir:
        result["cache"] = prune_cache(args.cache_dir, max_age_seconds=args.cache_max_days*86400, max_bytes=int(args.cache_max_mib*1024**2), apply=args.apply)
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
