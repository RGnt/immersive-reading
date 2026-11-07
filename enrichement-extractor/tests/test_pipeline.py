import csv
import json
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile
from immersive.epub import read_epub
from immersive.build import load_decks, utf16_offset
from immersive.render import vocab_cards, kanji_cards, grammar_cards, grouped_vocabulary, vocabulary_learning_sets
from immersive.kanjivg import add_stroke_order, copy_svg_assets


class PipelineTests(unittest.TestCase):
    def test_masters_follow_text_positions_even_when_inputs_are_reordered(self):
        paths = list(Path("output/data").glob("book-*/reader.json"))
        if not paths:
            self.skipTest("Build a book before running integration checks")
        book = json.loads(paths[0].read_text(encoding="utf-8"))
        book["grammar"].reverse()
        for grammar in book["grammar"]:
            grammar["occurrences"].reverse()
        for chapter in book["chapters"]:
            chapter["vocabulary_ids"].reverse()
        order = {c["id"]: i for i, c in enumerate(book["chapters"])}
        def position(entry):
            return min((order[o["chapter_id"]], o["start"], o["end"])
                       for o in entry["occurrences"])
        vocabulary = {v["id"]: v for v in grouped_vocabulary(book).values()}
        cards = [c for group in vocabulary_learning_sets(book) for c in group["cards"]]
        positions = [position(vocabulary[c["id"]]) for c in cards]
        self.assertEqual(positions, sorted(positions))
        grammar = {g["id"]: g for g in book["grammar"]}
        cards = [c for chapter in book["chapters"] for c in grammar_cards(book, chapter)]
        positions = [position(grammar[c["id"]]) for c in cards]
        self.assertEqual(positions, sorted(positions))
        self.assertEqual(len(cards), len(grammar))

    def test_ruby_same_file_chapters_and_nested_anchors(self):
        with tempfile.TemporaryDirectory(dir=".") as temp:
            path = Path(temp) / "book.epub"
            with ZipFile(path, "w") as z:
                z.writestr("META-INF/container.xml", '<container><rootfiles><rootfile full-path="OPS/content.opf"/></rootfiles></container>')
                z.writestr("OPS/content.opf", '<package><metadata><title>Test</title></metadata><manifest><item id="text" href="text.xhtml"/><item id="nav" href="nav.xhtml" properties="nav"/></manifest><spine><itemref idref="text"/></spine></package>')
                z.writestr("OPS/nav.xhtml", '<html xmlns:epub="http://www.idpf.org/2007/ops"><nav epub:type="toc"><a href="text.xhtml#a">第一章</a><a href="text.xhtml#b">第二章</a></nav></html>')
                z.writestr("OPS/text.xhtml", '<html><body><p><span id="a">第一章</span></p><p>　<ruby><rb>海</rb><rt>うみ</rt><rb>辺</rb><rt>べ</rt></ruby>へ。</p><p>もう一度<ruby>海<rt>うみ</rt></ruby>。</p><p><span id="b">第二章</span></p><p>泳ぐ。</p></body></html>')
            book = read_epub(path)
            a, b = book["chapters"]
            self.assertEqual(a["text"], "海辺へ。もう一度海。")
            self.assertEqual(b["text"], "泳ぐ。")
            ruby = [r for p in a["paragraphs"] for r in p["ruby"]]
            self.assertEqual([r["reading"] for r in ruby], ["うみ", "べ", "うみ"])
            for r in ruby:
                self.assertEqual(a["text"][r["start"]:r["end"]], r["surface"])

    def test_decks_preserve_spellings_merge_exact_duplicates(self):
        with tempfile.TemporaryDirectory(dir=".") as temp:
            path = Path(temp) / "deck.csv"
            with path.open("w", encoding="utf-8-sig", newline="") as f:
                w = csv.writer(f)
                w.writerow(["Word", "ReadingKana", "Definitions", "JmDictWordId", "ExampleSentence"])
                w.writerow(["海辺", "うみべ", "seaside, shore", "123", "海辺へ。"])
                w.writerow(["海べ", "うみべ", "seaside, shore", "123", "海べへ。"])
                w.writerow(["海辺", "うみべ", "seaside, shore", "123", "海辺へ。"])
            entries = load_decks([path])
            self.assertEqual(len(entries), 2)
            self.assertEqual(len(entries[0]["sources"]), 2)
            self.assertEqual(entries[0]["definitions"], "seaside, shore")

    def test_utf16_astral_offset(self):
        self.assertEqual(utf16_offset("𠮷野の海", 2), 3)

    def test_kanjivg_preserves_source_order_assets_and_missing_status(self):
        with tempfile.TemporaryDirectory(dir=".") as temp:
            root = Path(temp)
            archive = root / "source.zip"
            svg = b'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 109 109"><g><path id="s12" d="M1 2 L3 4"/><path id="s2" d="M5 6 C7 8 9 10 11 12"/></g></svg>'
            with ZipFile(archive, "w") as z:
                z.writestr("kanji/05317.svg", svg)
            book = {"kanji": [{"id": "north", "character": "北"}, {"id": "river", "character": "川"}],
                    "build_inputs": {}, "quality": {}, "attribution": []}
            add_stroke_order(book, archive)
            strokes = book["kanji"][0]["stroke_order"]["strokes"]
            self.assertEqual([s["id"] for s in strokes], ["s12", "s2"])
            self.assertEqual([s["number"] for s in strokes], [1, 2])
            self.assertEqual(book["quality"]["missing_stroke_order_ids"], ["river"])
            self.assertIsNone(book["kanji"][1]["stroke_order"])
            copy_svg_assets(book, archive, root)
            self.assertEqual((root / "assets/kanjivg/05317.svg").read_bytes(), svg)

    def test_same_spelling_merges_readings_and_uses_earliest_chapter(self):
        def entry(eid, reading, chapter, gloss):
            return {"id": eid, "word": "上", "reading": reading, "definitions": gloss,
                    "deck_examples": [], "occurrences": [{"chapter_id": chapter, "start": 0, "end": 1}]}
        book = {"chapters": [{"id": "chapter-01"}, {"id": "chapter-02"}], "vocabulary": [
            entry("late", "じょう", "chapter-02", "above"), entry("early", "うえ", "chapter-01", "top")]}
        merged = grouped_vocabulary(book)["上"]
        self.assertEqual(merged["first_chapter_id"], "chapter-01")
        self.assertEqual(merged["id"], "early")
        self.assertEqual(merged["reading"], "じょう / うえ")
        self.assertEqual(merged["definitions"], "above / top")

    def test_supplied_book_regressions(self):
        books = list(Path("output/data").glob("book-*/reader.json"))
        if not books:
            self.skipTest("Build a book before running integration checks")
        b = json.loads(books[0].read_text(encoding="utf-8"))
        self.assertEqual(len(b["vocabulary"]), 1467)
        self.assertEqual(b["chapters"][-1]["text"], "")
        self.assertEqual(b["chapters"][-2]["text"], "")
        fox = next(v for v in b["vocabulary"] if v["word"] == "きたきつね")
        self.assertFalse(fox["occurrences"], "Do not match できた + きつね as きたきつね")
        phrase = next(v for v in b["vocabulary"] if v["word"] == "気になる")
        self.assertTrue(phrase["occurrences"], "Conjugated phrases should align")
        self.assertTrue(all("translation" not in s for c in b["chapters"] for s in c["sentences"]))

    def test_chapter_study_cards_never_repeat(self):
        books = list(Path("output/data").glob("book-*/reader.json"))
        if not books:
            self.skipTest("Build a book before running integration checks")
        for path in books:
            book = json.loads(path.read_text(encoding="utf-8"))
            for factory, expected in [(vocab_cards, {v["word"] for v in book["vocabulary"] if v["occurrences"]}),
                                      (kanji_cards, {k["character"] for k in book["kanji"]})]:
                seen = set()
                for chapter in book["chapters"]:
                    cards = factory(book, chapter)
                    titles = [card["title"] for card in cards]
                    self.assertEqual(len(titles), len(set(titles)), chapter["id"])
                    self.assertFalse(seen.intersection(titles), chapter["id"])
                    seen.update(titles)
                self.assertEqual(seen, expected)

    def test_learning_sets_cover_new_words_once_in_order(self):
        paths = list(Path("output/data").glob("book-*/reader.json"))
        if not paths:
            self.skipTest("Build a book first")
        for path in paths:
            book = json.loads(path.read_text(encoding="utf-8"))
            groups = vocabulary_learning_sets(book)
            cards = [c for g in groups for c in g["cards"]]
            expected = [c["title"] for ch in book["chapters"] for c in vocab_cards(book, ch)]
            self.assertEqual([c["title"] for c in cards], expected)
            self.assertEqual([c["study_number"] for c in cards], list(range(1, len(cards)+1)))
            self.assertTrue(all(len(g["cards"]) == 20 for g in groups[:-1]))
            self.assertEqual(len(cards), len({c["title"] for c in cards}))

    def test_all_chapters_have_grounded_kanji_context(self):
        from immersive.kanji_pdf import contextual_cards
        paths = list(Path("output/data").glob("book-*/reader.json"))
        if not paths:
            self.skipTest("Build a book first")
        for path in paths:
            book = json.loads(path.read_text(encoding="utf-8"))
            seen = set()
            for chapter in book["chapters"]:
                cards = contextual_cards(book, chapter)
                expected = {k["id"] for k in book["kanji"] if k["first_chapter_id"] == chapter["id"]}
                self.assertEqual({c["id"] for c in cards}, expected)
                for card in cards:
                    self.assertNotIn(card["id"], seen)
                    seen.add(card["id"])
                    self.assertIn(card["context_surface"], chapter["text"])
                    if card["context_gloss_status"] == "ambiguous_dictionary_candidates":
                        self.assertIsNone(card["context_entry_id"])
                        for excerpt in card["context_gloss"].split(" / "):
                            self.assertIn(excerpt, card["context_definitions"])
                    else:
                        self.assertIn(card["context_gloss"], card["context_definitions"])
                    self.assertTrue(card["context_reading"])
                    self.assertTrue(card["context_sources"])
            self.assertEqual(len(seen), len(book["kanji"]))

    def test_new_book_needs_no_editorial_corrections(self):
        from immersive.context import contextual_cards
        paths = list(Path("output/data").glob("book-*/reader.json"))
        if not paths:
            self.skipTest("Build a book first")
        book = json.loads(paths[0].read_text(encoding="utf-8"))
        book["id"] = "unannotated-new-book"
        book["build_inputs"]["book_config"].pop("print_context", None)
        cards = [card for chapter in book["chapters"] for card in contextual_cards(book, chapter)]
        self.assertEqual(len(cards), len(book["kanji"]))
        self.assertTrue(any(c["context_gloss_scope"] == "character_dictionary" for c in cards))
        gold = next((c for c in cards if c["title"] == "金"), None)
        if gold:
            self.assertEqual(gold["context_gloss_scope"], "character_dictionary")
        for card in cards:
            self.assertTrue(card["context_gloss"])
            self.assertIn(card["example_provenance"]["source"], {"csv", "epub_first_occurrence", "unavailable"})


if __name__ == "__main__":
    unittest.main()
