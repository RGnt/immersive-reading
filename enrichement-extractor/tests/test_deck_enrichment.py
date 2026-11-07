import codecs
import csv
import hashlib
import tempfile
import unittest
from pathlib import Path
from immersive.deck_enrichment import enrich_decks


class DeckEnrichmentTests(unittest.TestCase):
    def test_order_duplicates_unmatched_and_idempotence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            path = root / "deck.csv"
            with path.open("w", encoding="utf-8-sig", newline="") as f:
                csv.writer(f).writerows([
                    ["Word", "Definitions", "first_appearance"],
                    ["泳ぐ", "swim, float\nsecond line", "999"],
                    ["海", "sea", "999"], ["海", "ocean", "999"],
                    ["不在", "absent", "999"]])
            def entry(rows, occurrences):
                return {"sources": [{"file": "deck.csv", "csv_row": n} for n in rows],
                        "occurrences": occurrences,
                        "deck_records": [{"file": "deck.csv", "csv_row": n, "fields": {}} for n in rows]}
            book = {"chapters": [{"id": "one", "text": "海へ。"}, {"id": "two", "text": "泳いだ。"}],
                    "source_files": [{"file": "deck.csv", "sha256": "old"}],
                    "vocabulary": [entry([2], [{"chapter_id": "two", "start": 0}]),
                                   entry([3, 4], [{"chapter_id": "two", "start": 2}, {"chapter_id": "one", "start": 0}]),
                                   entry([5], [])]}
            report = enrich_decks(book, root)
            with path.open(encoding="utf-8-sig", newline="") as f:
                rows = list(csv.DictReader(f))
            self.assertEqual([r["first_appearance"] for r in rows], ["4", "1", "1", ""])
            self.assertEqual(rows[0]["Definitions"], "swim, float\nsecond line")
            self.assertEqual(report[0]["matched"], 3)
            before = path.read_bytes()
            self.assertTrue(before.startswith(codecs.BOM_UTF8))
            self.assertEqual(book["source_files"][0]["sha256"], hashlib.sha256(before).hexdigest())
            self.assertEqual(book["vocabulary"][0]["deck_records"][0]["fields"]["first_appearance"], "4")
            enrich_decks(book, root)
            self.assertEqual(path.read_bytes(), before)

    def test_add_column_multiple_decks_and_empty_chapters(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name in ["a.csv", "b.csv"]:
                (root / name).write_bytes("Word,Definitions\n海,sea\n".encode("utf-8"))
            book = {"chapters": [{"id": "empty", "text": ""}, {"id": "one", "text": "海"}],
                    "vocabulary": [{"sources": [{"file": name, "csv_row": 2} for name in ["a.csv", "b.csv"]],
                                    "occurrences": [{"chapter_id": "one", "start": 0}]}]}
            enrich_decks(book, root)
            for name in ["a.csv", "b.csv"]:
                self.assertEqual((root / name).read_bytes(), "Word,Definitions,first_appearance\n海,sea,1\n".encode())
