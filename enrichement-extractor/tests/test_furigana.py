import unittest
from immersive.furigana import furigana, unresolved
from immersive.render import rich_example


class FuriganaTests(unittest.TestCase):
    def test_adds_readings_and_leaves_okurigana_in_text(self):
        text = "海で食べる。"
        ruby = furigana(text)
        self.assertEqual([(r["surface"], r["reading"]) for r in ruby], [("海", "うみ"), ("食", "た")])
        self.assertFalse(unresolved(text, ruby))
        self.assertTrue(all(r["source"] == "sudachi" for r in ruby))

    def test_preserves_author_reading_and_generates_missing_words(self):
        text = "明日海へ行く。"
        source = [{"surface": "明日", "reading": "あす", "start": 0, "end": 2}]
        ruby = furigana(text, source)
        self.assertEqual(ruby[0]["reading"], "あす")
        self.assertEqual(ruby[0]["source"], "epub")
        self.assertFalse(unresolved(text, ruby))
        self.assertEqual(len([r for r in ruby if r["start"] < 2]), 1)

    def test_html_examples_have_furigana_without_source_ruby(self):
        sentence = {"text": "海へ行く。", "start": 0, "end": 5}
        html = rich_example(sentence, {"text": sentence["text"], "paragraphs": []})
        self.assertIn("<ruby>海<rt>うみ</rt></ruby>", html)
        self.assertIn("<ruby>行<rt>い</rt></ruby>", html)

    def test_unknown_readings_are_reported_instead_of_invented(self):
        text = "𠮷"
        ruby = furigana(text)
        self.assertEqual(ruby, [])
        self.assertEqual(unresolved(text, ruby), [{"start": 0, "surface": "𠮷"}])
