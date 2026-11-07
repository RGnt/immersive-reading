import hashlib
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from immersive.cache import ContentCache
from immersive.deck_enrichment import enrich_decks
from immersive.enrichment import enrich_book, EnrichmentReport
from immersive.provenance import read_ledger, source_descriptor, LEDGER
from test_reader_enrichment import fixture


class ProvenanceTests(unittest.TestCase):
    def test_original_input_and_export_hashes_survive_new_runs_and_cache_hits(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            original = hashlib.sha256((folder / "deck.csv").read_bytes()).hexdigest()
            cache = ContentCache(root / "cache")
            book = enrich_book(folder, resources, cache=cache)
            self.assertFalse((folder / ".enrichment").exists())
            report = enrich_decks(book, folder)[0]
            self.assertEqual(report["input_sha256"], original)
            self.assertNotEqual(report["exported_sha256"], original)
            self.assertEqual(book["source_files"][1]["input_sha256"], original)
            cached_report = EnrichmentReport()
            fresh = enrich_book(folder, resources, cache=cache, report=cached_report)
            self.assertEqual(cached_report.cache_status, "hit")
            source = fresh["source_files"][1]
            self.assertEqual(source["original_sha256"], original)
            self.assertEqual(source["exported_sha256"], source["sha256"])
            self.assertEqual(source["input_sha256"], source["sha256"])
            ledger = (folder / LEDGER).read_bytes()
            enrich_decks(fresh, folder)
            self.assertEqual((folder / LEDGER).read_bytes(), ledger)

    def test_external_changes_start_a_new_observed_lineage(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            enrich_decks(enrich_book(folder, resources), folder)
            path = folder / "deck.csv"
            path.write_text(path.read_text(encoding="utf-8").replace("sea", "ocean"), encoding="utf-8")
            source = source_descriptor(path)
            self.assertEqual(source["provenance_status"], "external_input")
            self.assertEqual(source["original_sha256"], source["sha256"])
            self.assertIsNone(source["exported_sha256"])

    def test_failed_csv_replace_leaves_an_intent_without_claiming_export(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            book = enrich_book(folder, resources)
            original = (folder / "deck.csv").read_bytes()
            original_replace = os.replace
            def replace(source, destination):
                if Path(destination) == folder / "deck.csv":
                    raise OSError("CSV locked")
                return original_replace(source, destination)
            with patch("immersive.deck_enrichment.os.replace", side_effect=replace):
                with self.assertRaises(OSError): enrich_decks(book, folder)
            self.assertEqual((folder / "deck.csv").read_bytes(), original)
            self.assertEqual(len(read_ledger(folder)["rewrites"]), 1)
            self.assertIsNone(source_descriptor(folder / "deck.csv")["exported_sha256"])
            enrich_decks(book, folder)
            self.assertTrue(read_ledger(folder)["rewrites"])

    def test_changed_csv_is_rejected_before_rewriting(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            book = enrich_book(folder, resources)
            path = folder / "deck.csv"; path.write_bytes(path.read_bytes()+b"\n")
            with self.assertRaisesRegex(ValueError, "changed since enrichment"): enrich_decks(book, folder)
