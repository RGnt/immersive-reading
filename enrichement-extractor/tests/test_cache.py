"""Content invalidation, source provenance, safe writes and cache bypass."""
import csv
import hashlib
from concurrent.futures import ThreadPoolExecutor
from dataclasses import replace
import json
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile
from unittest.mock import patch

from immersive.cache import ContentCache, reader_inputs
from immersive.enrichment import enrich_book, EnrichmentReport
from immersive.jobs import CancellationToken, CancelledError
from immersive.resources import ResourceBundle, ResourceStore
from immersive.text_index import utf16_offsets, SentenceIndex
from test_reader_enrichment import fixture


class CacheTests(unittest.TestCase):
    def test_validated_hit_is_equal_and_skips_analysis_and_validation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            cache = ContentCache(root / "cache")
            cold, warm = EnrichmentReport(), EnrichmentReport()
            expected = enrich_book(folder, paths, cache=cache, report=cold)
            with patch("immersive.enrichment.build_data", side_effect=AssertionError("Rebuilt")), patch(
                    "immersive.enrichment.validate_reader", side_effect=AssertionError("Revalidated")):
                actual = enrich_book(folder, paths, cache=cache, report=warm)
            self.assertEqual(actual, expected)
            self.assertEqual(json.dumps(actual, ensure_ascii=False), json.dumps(expected, ensure_ascii=False))
            self.assertEqual((cold.cache_status, warm.cache_status), ("miss", "hit"))
            self.assertTrue(warm.validated)
            actual["vocabulary"][0]["definitions"] = "changed"
            self.assertEqual(enrich_book(folder, paths, cache=cache), expected)

    def test_derived_sort_keys_refresh_provenance_without_reanalysis(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            cache = ContentCache(root / "cache")
            initial = enrich_book(folder, paths, cache=cache)
            path = folder / "deck.csv"
            with path.open(encoding="utf-8", newline="") as stream:
                rows = list(csv.DictReader(stream))
            for n, row in enumerate(rows): row["first_appearance"] = str(n+100)
            with path.open("w", encoding="utf-8-sig", newline="") as stream:
                writer = csv.DictWriter(stream, fieldnames=list(rows[0])); writer.writeheader(); writer.writerows(rows)
            with patch("immersive.enrichment.build_data", side_effect=AssertionError("Rebuilt")):
                reused = enrich_book(folder, paths, cache=cache)
            self.assertNotEqual(reused["source_files"], initial["source_files"])
            self.assertEqual(reused["vocabulary"][0]["deck_records"][0]["fields"]["first_appearance"], "100")
            uncached = enrich_book(folder, paths, cache=False)
            self.assertEqual(reused, uncached)

    def test_semantic_inputs_resources_versions_and_code_change_cache_keys(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            bundle = ResourceBundle.from_directory(paths)
            with ResourceStore(bundle) as store:
                original = reader_inputs(folder, store)[0]
                with patch("immersive.cache.code_revision", return_value={"new": "code"}):
                    self.assertNotEqual(reader_inputs(folder, store)[0], original)
                with patch("immersive.cache.importlib.metadata.version", return_value="new-version"):
                    self.assertNotEqual(reader_inputs(folder, store)[0], original)
                (folder / "study.config.json").write_text('{"context_glosses":{"海":"ocean"}}', encoding="utf-8")
                self.assertNotEqual(reader_inputs(folder, store)[0], original)
                (folder / "study.config.json").unlink()
                csv_path = folder / "deck.csv"
                csv_path.write_text(csv_path.read_text(encoding="utf-8").replace("sea", "ocean"), encoding="utf-8")
                self.assertNotEqual(reader_inputs(folder, store)[0], original)
            grammar = root / "grammar.json"; grammar.write_text("[]", encoding="utf-8")
            with ResourceStore(replace(bundle, grammar=grammar)) as store:
                self.assertNotEqual(reader_inputs(folder, store)[0], original)

    def test_unvalidated_entries_are_validated_and_upgraded_on_demand(self):
        from immersive.validation import validate_reader
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            cache = ContentCache(root / "cache")
            enrich_book(folder, paths, cache=cache, validate=False)
            with patch("immersive.enrichment.validate_reader", wraps=validate_reader) as validator:
                enrich_book(folder, paths, cache=cache)
                self.assertEqual(validator.call_count, 1)
                enrich_book(folder, paths, cache=cache)
                self.assertEqual(validator.call_count, 1)

    def test_explicit_bypass_on_shared_store_does_not_read_or_write_persistent_cache(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            cache = ContentCache(root / "cache")
            with ResourceStore(ResourceBundle.from_directory(paths), cache=cache) as store:
                with patch.object(cache, "get", side_effect=AssertionError("Cache read")), patch.object(cache, "put", side_effect=AssertionError("Cache write")):
                    book = enrich_book(folder, store, cache=False)
                self.assertTrue(book["vocabulary"])

    def test_resource_cache_reuses_parsed_dictionary_between_stores(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); _, paths = fixture(root)
            cache, bundle = ContentCache(root / "cache"), ResourceBundle.from_directory(paths)
            with ResourceStore(bundle, cache=cache) as store:
                expected = store.kanjidic()
            with patch("immersive.build.load_kanjidic", side_effect=AssertionError("Reparsed")):
                with ResourceStore(bundle, cache=cache) as store:
                    self.assertEqual(store.kanjidic(), expected)

    def test_corruption_interrupted_write_and_cancel_leave_cache_recoverable(self):
        with tempfile.TemporaryDirectory() as temp:
            cache = ContentCache(Path(temp) / "cache"); key = "a"*64
            cache.put("reader", key, {"value": "old"}, validated=True)
            with patch("immersive.cache.os.replace", side_effect=OSError("locked")):
                self.assertFalse(cache.put("reader", key, {"value": "new"}))
            self.assertEqual(cache.get("reader", key)["data"], {"value": "old"})
            token = CancellationToken(); token.cancel()
            with self.assertRaises(CancelledError): cache.put("reader", key, {}, cancel=token)
            cache.path("reader", key).write_bytes(b"corrupted")
            self.assertIsNone(cache.get("reader", key))
            self.assertFalse(list(cache.directory.rglob("*.tmp")))

    def test_unicode_indexes_match_slicing_and_sentence_edges(self):
        text = "海𠮷😀a"
        self.assertEqual(utf16_offsets(text), [len(text[:i].encode("utf-16-le"))//2 for i in range(len(text)+1)])
        sentences = [{"start": 0, "end": 3}, {"start": 4, "end": 8}]
        index = SentenceIndex(sentences)
        self.assertIs(index.at(2), sentences[0]); self.assertIs(index.at(4), sentences[1])
        self.assertIsNone(index.at(-1)); self.assertIsNone(index.at(3)); self.assertIsNone(index.at(8))

    def test_reused_report_does_not_skip_validation_of_another_build(self):
        from immersive.validation import ValidationError
        with tempfile.TemporaryDirectory() as temp:
            folder, paths = fixture(Path(temp))
            report = EnrichmentReport()
            book = enrich_book(folder, paths, report=report)
            book["quality"]["vocabulary_total"] = -1
            with patch("immersive.enrichment.build_data", return_value=book):
                with self.assertRaises(ValidationError): enrich_book(folder, paths, report=report)

    def test_concurrent_writers_publish_complete_cache_entries(self):
        with tempfile.TemporaryDirectory() as temp:
            cache = ContentCache(Path(temp) / "cache"); key = "b"*64
            with ThreadPoolExecutor(max_workers=4) as pool:
                list(pool.map(lambda n: cache.put("reader", key, {"value": n}), range(12)))
            self.assertIn(cache.get("reader", key)["data"]["value"], range(12))
            self.assertFalse(list(cache.directory.rglob("*.tmp")))

    def test_print_resources_do_not_invalidate_reader_analysis(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            cache = ContentCache(root / "cache"); bundle = ResourceBundle.from_directory(paths)
            expected = enrich_book(folder, bundle, cache=cache)
            changed = replace(bundle, font=root / "other-font.ttf", charts=(root / "other-chart.jpg",), kanjivg=root / "other-strokes.zip")
            with patch("immersive.enrichment.build_data", side_effect=AssertionError("Rebuilt for a print change")):
                self.assertEqual(enrich_book(folder, changed, cache=cache), expected)

    def test_epub_and_dictionary_content_changes_invalidate_analysis(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            cache = ContentCache(root / "cache")
            original = enrich_book(folder, paths, cache=cache)
            with ZipFile(folder / "sample.epub", "a") as archive: archive.writestr("extra.txt", "new revision")
            report = EnrichmentReport()
            revised = enrich_book(folder, paths, cache=cache, report=report)
            self.assertEqual(report.cache_status, "miss")
            self.assertNotEqual(original["id"], revised["id"])
            data = paths / "kanji-alive/ka_data.csv"
            data.write_text("kanji,kmeaning\n海,ocean\n", encoding="utf-8")
            source = paths / "kanji-alive/source.json"
            metadata = json.loads(source.read_text(encoding="utf-8")); metadata["sha256"] = hashlib.sha256(data.read_bytes()).hexdigest()
            source.write_text(json.dumps(metadata), encoding="utf-8")
            updated = enrich_book(folder, paths, cache=cache, report=report)
            self.assertEqual(report.cache_status, "miss")
            self.assertEqual(updated["kanji"][0]["dictionary"]["primary_meanings"], ["ocean"])


if __name__ == "__main__":
    unittest.main()
