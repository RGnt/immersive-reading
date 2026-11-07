from pathlib import Path
import tempfile
import unittest

from immersive.cache import ContentCache
from immersive.publication import Publication, read_manifest
from immersive.retention import lease_manifest, prune_cache, prune_releases
from test_publication import reader, ONE, TWO


def publish(output, bid=ONE):
    with Publication(output) as publication:
        reader(publication, bid)
        return publication.publish()[0]


class RetentionTests(unittest.TestCase):
    def test_active_references_and_leased_old_snapshots_are_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "output"
            old = publish(output)
            with lease_manifest(output, old):
                current = publish(output)
                plan = prune_releases(output, keep=0, min_age_seconds=0, apply=True)
                self.assertFalse(plan["removed"])
            plan = prune_releases(output, keep=0, min_age_seconds=0)
            self.assertIn(old["generation"], plan["candidates"])
            self.assertTrue((output / f'.publication/releases/{old["generation"]}').exists())
            applied = prune_releases(output, keep=0, min_age_seconds=0, apply=True)
            self.assertIn(old["generation"], applied["removed"])
            self.assertEqual(read_manifest(output)["generation"], current["generation"])

    def test_different_book_reference_prevents_collection(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "output"
            old = publish(output); publish(output, TWO)
            self.assertNotIn(old["generation"], prune_releases(output, keep=0, min_age_seconds=0, apply=True)["removed"])

    def test_unknown_or_modified_release_files_are_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "output"
            old = publish(output)
            root = output / f'.publication/releases/{old["generation"]}'
            (root / "my-notes.txt").write_text("keep", encoding="utf-8")
            publish(output)
            self.assertNotIn(old["generation"], prune_releases(output, keep=0, min_age_seconds=0, apply=True)["removed"])
            self.assertTrue((root / "my-notes.txt").exists())

    def test_cache_cleanup_only_removes_valid_owned_entries(self):
        with tempfile.TemporaryDirectory() as temp:
            cache = ContentCache(Path(temp) / "cache")
            cache.put("reader", "a"*64, {"value": 1})
            notes = cache.directory / "notes.txt"; notes.write_text("keep", encoding="utf-8")
            corrupt = cache.path("reader", "b"*64); corrupt.parent.mkdir(exist_ok=True); corrupt.write_bytes(b"unknown")
            plan = prune_cache(cache.directory, max_age_seconds=0, max_bytes=0)
            self.assertEqual(len(plan["candidates"]), 1)
            self.assertTrue(cache.path("reader", "a"*64).exists())
            prune_cache(cache.directory, max_age_seconds=0, max_bytes=0, apply=True)
            self.assertTrue(notes.exists()); self.assertTrue(corrupt.exists())
            self.assertFalse(cache.path("reader", "a"*64).exists())
