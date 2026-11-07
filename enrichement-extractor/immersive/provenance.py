"""Current source bytes and durable, write-ahead CSV rewrite provenance."""
import hashlib
import re
from pathlib import Path

from .publication import atomic_json, contained
from .validation import load_json

LEDGER = ".enrichment/deck-provenance.json"
HASH = re.compile(r"[a-f0-9]{64}\Z")


def read_ledger(folder):
    path = contained(folder, LEDGER)
    if not path.exists():
        return {"schema_version": 1, "rewrites": []}
    ledger = load_json(path)
    if not isinstance(ledger, dict) or ledger.get("schema_version") != 1 or not isinstance(ledger.get("rewrites"), list):
        raise ValueError(f"{path}: invalid deck provenance ledger")
    for record in ledger["rewrites"]:
        if not isinstance(record, dict) or not isinstance(record.get("file"), str) or Path(record["file"]).name != record["file"] or not record["file"].endswith(".csv"):
            raise ValueError(f"{path}: invalid deck provenance record")
        if not all(isinstance(record.get(k), str) and HASH.fullmatch(record[k]) for k in ("input_sha256", "exported_sha256", "original_sha256")):
            raise ValueError(f"{path}: invalid deck provenance hashes")
    return ledger


def source_descriptor(path, raw=None, *, ledger=None):
    path = Path(path)
    raw = path.read_bytes() if raw is None else raw
    checksum = hashlib.sha256(raw).hexdigest()
    result = {"file": path.name, "sha256": checksum, "input_sha256": checksum}
    if path.suffix != ".csv":
        return result
    ledger = read_ledger(path.parent) if ledger is None else ledger
    records = [r for r in ledger["rewrites"] if r["file"] == path.name]
    exported = next((r for r in reversed(records) if r["exported_sha256"] == checksum), None)
    result.update(original_sha256=exported["original_sha256"] if exported else checksum,
                  exported_sha256=checksum if exported else None,
                  provenance_status="pipeline_export" if exported else "external_input" if records else "observed_input")
    return result


def record_rewrite(folder, path, original, updated):
    """Commit intent first; matching on-disk bytes prove whether it took effect."""
    ledger = read_ledger(folder)
    before = source_descriptor(path, original, ledger=ledger)
    record = {"file": Path(path).name, "operation": "first_appearance_v1",
              "input_sha256": before["sha256"], "exported_sha256": hashlib.sha256(updated).hexdigest(),
              "original_sha256": before["original_sha256"]}
    if not ledger["rewrites"] or ledger["rewrites"][-1] != record:
        ledger["rewrites"].append(record)
        atomic_json(contained(folder, LEDGER), ledger)
    return record
