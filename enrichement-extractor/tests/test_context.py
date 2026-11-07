import unittest
from unittest.mock import patch
from immersive.context import contextual_cards


class ContextTests(unittest.TestCase):
    def test_aligned_deck_reading_when_epub_has_no_ruby(self):
        chapter = {"id": "chapter-01", "text": "日だ。", "paragraphs": [], "sentences": []}
        entry = {"id": "day", "word": "日", "reading": "ひ", "definitions": "day, sun",
                 "sources": [{"file": "deck.csv", "csv_row": 2}],
                 "occurrences": [{"chapter_id": chapter["id"], "start": 0, "end": 1, "surface": "日"}]}
        book = {"chapters": [chapter], "vocabulary": [entry]}
        with patch("immersive.study_data.kanji_cards", return_value=[{"id": "sun", "title": "日"}]), \
             patch("immersive.context.select_example", return_value={"text": "日だ。"}), \
             patch("immersive.context.selection_span", return_value=(None, None)):
            card, = contextual_cards(book, chapter)
        self.assertEqual(card["context_reading"], "ひ")
        self.assertEqual(card["context_gloss"], "day")
        self.assertEqual(card["context_alignment"], "aligned_deck_word_csv_reading")
        self.assertIn("Reading from CSV", card["source_reading_note"])
        self.assertTrue(card["ruby"])
        self.assertEqual(card["ruby"][0]["source"], "sudachi")
