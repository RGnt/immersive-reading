import csv
import hashlib
import json
import tempfile
import unittest
from pathlib import Path
from immersive.kanji_meanings import add_primary_meanings, split_meanings


class KanjiMeaningTests(unittest.TestCase):
    def test_character_meanings_prefer_learner_source_and_retain_complete_fallback(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            with (root/"ka_data.csv").open("w", encoding="utf-8", newline="") as stream:
                writer = csv.writer(stream)
                writer.writerows([["kanji", "kmeaning"], ["火", "fire, flame"]])
            metadata = {"sha256": hashlib.sha256((root/"ka_data.csv").read_bytes()).hexdigest(),
                        "url": "https://example.test/pinned.csv", "copyright": "Authors", "commit": "pinned"}
            (root/"source.json").write_text(json.dumps(metadata), encoding="utf-8")
            book = {"kanji": [{"character": "火", "dictionary": {"meanings": ["Tuesday"]}},
                              {"character": "炎", "dictionary": {"meanings": ["inflammation", "flame", "blaze"]}}],
                    "build_inputs": {}, "quality": {}, "attribution": []}
            add_primary_meanings(book, root)
            fire, flame = [entry["dictionary"] for entry in book["kanji"]]
            self.assertEqual(fire["primary_meanings"], ["fire", "flame"])
            self.assertEqual(fire["meanings"], ["Tuesday"])
            self.assertEqual(fire["meaning_source"]["name"], "Kanji alive")
            self.assertEqual(flame["primary_meanings"], ["inflammation", "flame", "blaze"])
            self.assertTrue(flame["meaning_source"]["fallback"])
            add_primary_meanings(book, root)
            self.assertEqual(len(book["attribution"]), 1)
            (root/"ka_data.csv").write_text("modified", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "checksum"):
                add_primary_meanings(book, root)

    def test_meaning_lists_preserve_parenthetical_phrases(self):
        self.assertEqual(split_meanings("life (animal, plant), birth; raw"), ["life (animal, plant)", "birth", "raw"])
