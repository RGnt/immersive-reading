"""Write book-order sort keys back to the source Jiten decks."""
import codecs
import csv
import hashlib
import io
import os
import tempfile
from pathlib import Path


def enrich_decks(book, folder):
    """Explicitly update CSV sort keys and their durable provenance journal."""
    from .publication import PublicationLock
    with PublicationLock(folder, lock_name=".enrichment/deck-update.lock"):
        return _enrich_decks(book, folder)


def _enrich_decks(book, folder):
    """Add a 1-based position in concatenated chapter.text; unmatched is blank.

    Uses the existing morphological alignments, including inflected words, rather
    than substring searches. Rows and all other field values retain their order.
    """
    offsets = {}
    position = 0
    for chapter in book["chapters"]:
        offsets[chapter["id"]] = position
        position += len(chapter["text"])
    values = {}
    for entry in book["vocabulary"]:
        appearances = [offsets[o["chapter_id"]] + o["start"] + 1
                       for o in entry["occurrences"]]
        first = str(min(appearances)) if appearances else ""
        for source in entry["sources"]:
            if "csv_row" in source:
                values[(source["file"], source["csv_row"])] = first

    reports = []
    for path in sorted(Path(folder).glob("*.csv")):
        original = path.read_bytes()
        from .provenance import HASH, record_rewrite, source_descriptor
        expected = next((s["sha256"] for s in book.get("source_files", []) if s["file"] == path.name), None)
        if expected and HASH.fullmatch(expected) and hashlib.sha256(original).hexdigest() != expected:
            raise ValueError(f"{path}: source CSV changed since enrichment; rebuild before updating")
        text = original.decode("utf-8-sig")
        # Jiten uses comma-separated UTF-8. Preserve BOM and line endings.
        newline = "\r\n" if "\r\n" in text else "\n"
        reader = csv.DictReader(io.StringIO(text, newline=""))
        fields = list(reader.fieldnames or [])
        if len(fields) != len(set(fields)):
            raise ValueError(f"{path}: duplicate CSV column names")
        if "first_appearance" not in fields:
            fields.append("first_appearance")
        rows = list(reader)
        for number, row in enumerate(rows, 2):
            if None in row or any(value is None for value in row.values()):
                raise ValueError(f"{path}: malformed CSV record {number}")
            row["first_appearance"] = values.get((path.name, number), "")
        stream = io.StringIO(newline="")
        writer = csv.DictWriter(stream, fieldnames=fields, lineterminator=newline)
        writer.writeheader()
        writer.writerows(rows)
        updated = stream.getvalue().encode("utf-8")
        if original.startswith(codecs.BOM_UTF8):
            updated = codecs.BOM_UTF8 + updated
        if updated != original:
            # Write completely before replacing, so an interrupted write cannot
            # leave a truncated input deck.
            temporary = None
            try:
                record_rewrite(folder, path, original, updated)
                with tempfile.NamedTemporaryFile(dir=path.parent, suffix=".tmp", delete=False) as f:
                    temporary = Path(f.name)
                    f.write(updated)
                os.replace(temporary, path)
            finally:
                if temporary is not None:
                    temporary.unlink(missing_ok=True)
        for source in book.get("source_files", []):
            if source["file"] == path.name:
                before = source_descriptor(path, original)
                after = source_descriptor(path, updated)
                source.update(after, input_sha256=before["sha256"])
        for entry in book["vocabulary"]:
            for record in entry.get("deck_records", []):
                if record["file"] == path.name:
                    record["fields"]["first_appearance"] = values.get((path.name, record["csv_row"]), "")
        provenance = source_descriptor(path, updated)
        reports.append({"file": path.name, "rows": len(rows),
                        "input_sha256": hashlib.sha256(original).hexdigest(),
                        "exported_sha256": provenance["sha256"], "original_sha256": provenance["original_sha256"],
                        "matched": sum(bool(r["first_appearance"]) for r in rows)})
    return reports
