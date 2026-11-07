"""Optional local content cache; checksummed JSON, atomic replacement, no pickle."""
from collections import Counter
from contextvars import ContextVar
import csv
import gzip
import hashlib
import importlib.metadata
import io
import json
import os
from pathlib import Path
import platform
import sys
from threading import Lock
import unicodedata
import uuid
import zlib

from .jobs import check_cancelled
from .validation import load_json, validate_config

CACHE_FORMAT = 1
CACHE_ENABLED = ContextVar("immersive_persistent_cache_enabled", default=True)
READER_FILES = ("__init__.py", "build.py", "enrichment.py", "epub.py", "matching.py", "japanese.py", "furigana.py",
                "tokenization.py", "resources.py", "kanji_meanings.py", "yomitan.py", "examples.py",
                "validation.py", "reader.schema.json", "config.schema.json", "cache.py", "text_index.py", "ambiguity.py", "provenance.py")


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"), allow_nan=False).encode("utf-8")


def digest(value):
    return hashlib.sha256(canonical(value)).hexdigest()


def code_revision(names=READER_FILES):
    root = Path(__file__).parent
    return {name: hashlib.sha256((root / name).read_bytes()).hexdigest() for name in names if (root / name).is_file()}


class ContentCache:
    """Share one instance across threads/features, or use the directory across processes.

    Completed entries are replaceable, non-authoritative artifacts. Concurrent
    identical-key writers need no shared index: each replaces one file atomically.
    """
    def __init__(self, directory):
        self.directory = Path(directory).resolve()
        self._stats = Counter()
        self._lock = Lock()

    def count(self, namespace, status):
        with self._lock:
            self._stats[f"{namespace}.{status}"] += 1

    def stats(self):
        with self._lock:
            return dict(self._stats)

    def path(self, namespace, key):
        if namespace not in {"reader", "resource"} or len(key) != 64 or any(c not in "0123456789abcdef" for c in key):
            raise ValueError("Invalid cache namespace/key")
        from .publication import contained
        return contained(self.directory, f"{namespace}/{key[:2]}/{key}.json.gz")

    def get(self, namespace, key, *, cancel=None):
        from .publication import PublicationLock, PublicationError
        try:
            with PublicationLock(self.directory, cancel=cancel, timeout=2, lock_name=".retention.lock"):
                path = self.path(namespace, key)
                compressed = path.read_bytes()
                try: os.utime(path, None)
                except OSError: pass
        except FileNotFoundError:
            self.count(namespace, "miss")
            return None
        except (OSError, PublicationError):
            self.count(namespace, "unusable")
            return None
        return self._read(namespace, key, cancel=cancel, compressed=compressed)

    def _read(self, namespace, key, *, cancel=None, compressed=None):
        check_cancelled(cancel)
        try:
            source = io.BytesIO(compressed) if compressed is not None else self.path(namespace, key)
            with gzip.open(source, "rb") as stream:
                chunks = []
                while chunk := stream.read(1024 * 1024):
                    check_cancelled(cancel)
                    chunks.append(chunk)
            envelope = json.loads(b"".join(chunks))
            body = envelope["body"]
            if body["format"] != CACHE_FORMAT or body["key"] != key or digest(body) != envelope["sha256"]:
                raise ValueError("Invalid cache checksum/version")
        except FileNotFoundError:
            self.count(namespace, "miss")
            return None
        except (OSError, EOFError, ValueError, KeyError, TypeError, zlib.error):
            self.count(namespace, "unusable")
            return None
        check_cancelled(cancel)
        self.count(namespace, "hit")
        return body

    def put(self, namespace, key, data, *, validated=False, diagnostics=None, cancel=None):
        from .publication import PublicationError
        check_cancelled(cancel)
        path = self.path(namespace, key)
        temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
        body = {"format": CACHE_FORMAT, "key": key, "data": data,
                "validated": validated, "diagnostics": diagnostics or []}
        # Keep insertion order in the stored data so cache hits preserve the
        # same serialized reader bytes as an uncached build.
        payload = json.dumps({"body": body, "sha256": digest(body)}, ensure_ascii=False,
                             separators=(",", ":"), allow_nan=False).encode("utf-8")
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            with temporary.open("xb") as target:
                with gzip.GzipFile(fileobj=target, mode="wb", mtime=0) as compressed:
                    for start in range(0, len(payload), 1024 * 1024):
                        check_cancelled(cancel)
                        compressed.write(payload[start:start+1024*1024])
                target.flush()
                os.fsync(target.fileno())
            check_cancelled(cancel)
            from .publication import PublicationLock, PublicationError
            with PublicationLock(self.directory, cancel=cancel, timeout=2, lock_name=".retention.lock"):
                os.replace(temporary, self.path(namespace, key))
            self.count(namespace, "write")
            return True
        except (OSError, PublicationError):
            self.count(namespace, "write_error")
            return False
        finally:
            try:
                temporary.unlink(missing_ok=True)
            except OSError:
                pass


def reader_inputs(folder, store, *, cancel=None):
    """Keep current byte provenance while excluding only derived CSV sort keys."""
    folder = Path(folder)
    epubs, decks = sorted(folder.glob("*.epub")), sorted(folder.glob("*.csv"))
    if len(epubs) != 1 or (not decks and not store.bundle.yomitan):
        raise ValueError(f"{folder}: expected exactly one EPUB and a CSV deck or Yomitan dictionary")
    config_path = folder / "study.config.json"
    config = load_json(config_path) if config_path.exists() else {}
    validate_config(config, config_path)
    files, semantic, records = [], [], {}
    from .provenance import read_ledger, source_descriptor
    ledger = read_ledger(folder)
    for path in [*epubs, *decks]:
        check_cancelled(cancel)
        raw = path.read_bytes()
        files.append(source_descriptor(path, raw, ledger=ledger))
        if path.suffix == ".epub":
            semantic.append({"file": path.name, "sha256": files[-1]["sha256"]})
            continue
        reader = csv.DictReader(io.StringIO(raw.decode("utf-8-sig"), newline=""))
        fields = reader.fieldnames or []
        if len(fields) != len(set(fields)) or not {"Word", "ReadingKana", "Definitions"} <= set(fields):
            raise ValueError(f"{path}: invalid Jiten CSV columns (Word, ReadingKana, Definitions required; no duplicates)")
        rows = []
        for number, row in enumerate(reader, 2):
            if None in row or any(value is None for value in row.values()):
                raise ValueError(f"{path}: malformed CSV record {number}")
            records[(path.name, number)] = row
            rows.append({k: v for k, v in row.items() if k != "first_appearance"})
        semantic.append({"file": path.name, "columns": [f for f in fields if f != "first_appearance"], "rows": rows})
    from . import __version__
    inputs = {"format": CACHE_FORMAT, "pipeline_version": __version__, "schema_version": "1.0.0", "sources": semantic, "config": config,
              "resources": store.reader_manifest(cancel=cancel), "code": code_revision(),
              "runtime": {"python": list(sys.version_info[:3]), "implementation": platform.python_implementation(), "unicode": unicodedata.unidata_version},
              "packages": {name: importlib.metadata.version(name) for name in ("sudachipy", "sudachidict_core", "beautifulsoup4", "lxml", "jsonschema")}}
    return digest(inputs), files, records


def hydrate_reader(book, files, records):
    """Refresh byte hashes and original deck fields without altering analysis."""
    book["source_files"] = files
    for entry in book["vocabulary"]:
        for record in entry.get("deck_records", []):
            record["fields"] = dict(records[(record["file"], record["csv_row"])])
    return book
