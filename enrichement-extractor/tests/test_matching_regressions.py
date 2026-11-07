import csv
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

from immersive.deck_enrichment import enrich_decks
from immersive.enrichment import enrich_book
from immersive.epub import read_epub
from test_reader_enrichment import fixture


def document(paragraphs):
    return '<html><body><h1 id="a">第一章</h1>' + paragraphs + '<h1 id="b">第二章</h1><p>終わり。</p></body></html>'


class MatchingRegressionTests(unittest.TestCase):
    def test_homograph_rejected_in_every_path_and_deck_sort_key(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p>人気がある。</p>'), rows=[
                ["人気", "にんき", "popularity", ""], ["人気", "ひとけ", "presence of people", ""],
            ])
            book = enrich_book(folder, resources)
            popular, presence = book["vocabulary"]
            self.assertTrue(popular["occurrences"])
            self.assertFalse(presence["occurrences"])
            self.assertIsNone(presence["first_chapter_id"])
            self.assertEqual(presence["selected_examples"], {})
            enrich_decks(book, folder)
            with (folder / "deck.csv").open(encoding="utf-8", newline="") as stream:
                rows = list(csv.DictReader(stream))
            self.assertTrue(rows[0]["first_appearance"])
            self.assertEqual(rows[1]["first_appearance"], "")

    def test_source_ruby_overrides_tokenizer_homograph_guess(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p><ruby>人気<rt>ひとけ</rt></ruby>がある。</p>'), rows=[
                ["人気", "にんき", "popularity", ""], ["人気", "ひとけ", "presence of people", ""],
            ])
            book = enrich_book(folder, resources)
            popular, presence = book["vocabulary"]
            self.assertFalse(popular["occurrences"])
            self.assertTrue(presence["occurrences"])

    def test_context_fallback_cannot_reintroduce_rejected_homograph(self):
        from immersive.context import contextual_cards
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p><ruby>人気<rt>ひとけ</rt></ruby>がある。</p>'), rows=[
                ["人気", "にんき", "popularity", ""],
            ])
            book = enrich_book(folder, resources)
            chapter = book["chapters"][0]
            cards = contextual_cards(book, chapter)
            self.assertTrue(cards)
            for card in cards:
                self.assertIsNone(card["context_entry_id"])
                self.assertEqual(card["context_gloss_scope"], "character_dictionary")
            # Existing reviewed corrections (e.g. 半身 in the supplied book)
            # remain explicit, attributed exceptions.
            book["build_inputs"]["book_config"]["print_context"] = {chapter["id"]: {"人": {"word": "人気"}}}
            reviewed = next(c for c in contextual_cards(book, chapter) if c["title"] == "人")
            self.assertEqual(reviewed["context_reading"], "ひとけ")
            self.assertIn("CSV has", reviewed["source_reading_note"])

    def test_partial_source_ruby_and_multi_token_phrase_readings(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p><ruby>気<rt>き</rt></ruby>になる。</p>'), rows=[
                ["気になる", "きになる", "be concerned", ""],
                ["気になる", "けになる", "incompatible reading", ""],
            ])
            book = enrich_book(folder, resources)
            correct, incorrect = book["vocabulary"]
            self.assertTrue(correct["occurrences"])
            self.assertFalse(incorrect["occurrences"])

    def test_inflections_and_legitimate_orthographic_aliases_survive(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p>気になった。泳いだ。見た。きょうだ。</p>'), rows=[
                ["気になる", "きになる", "be concerned", ""], ["泳ぐ", "およぐ", "swim", ""],
                ["見る", "みる", "see", ""], ["今日", "きょう", "today", ""],
            ])
            book = enrich_book(folder, resources)
            for entry in book["vocabulary"]:
                self.assertTrue(entry["occurrences"], entry["word"])
            self.assertIn("normalized_token_sequence", {o["method"] for o in book["vocabulary"][0]["occurrences"]})

    def test_irregular_and_colloquial_inflections_survive(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p>信じた。しょうがねえな。おっかねえ。</p>'), rows=[
                ["信じる", "しんじる", "believe", ""], ["しょうがない", "しょうがない", "cannot be helped", ""],
                ["おっかない", "おっかない", "scary", ""],
            ])
            book = enrich_book(folder, resources)
            for entry in book["vocabulary"]:
                self.assertTrue(entry["occurrences"], entry["word"])

    def test_numeric_and_latin_spellings_are_not_literal_kana_readings(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p>１匹いる。ＮＰＣだ。</p>'), rows=[
                ["１匹", "いっぴき", "one animal", ""], ["ＮＰＣ", "エヌピーシー", "non-player character", ""],
            ])
            book = enrich_book(folder, resources)
            for entry in book["vocabulary"]:
                self.assertTrue(entry["occurrences"], entry["word"])
                for occurrence in entry["occurrences"]:
                    self.assertIn(occurrence["reading_match"]["status"], {"exact_reading", "reading_unverified"})

    def test_counter_pronunciation_changes_remain_explicit_candidates(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p>一<ruby>本<rt>ぽん</rt></ruby>だ。<ruby>十回<rt>じゅっかい</rt></ruby>だ。</p>'), rows=[
                ["本", "ほん", "book; counter for long objects", ""], ["十回", "じっかい", "ten times", ""],
            ])
            book = enrich_book(folder, resources)
            for entry in book["vocabulary"]:
                self.assertTrue(entry["occurrences"], entry["word"])
                evidence = entry["occurrences"][0]["reading_match"]
                self.assertEqual(evidence["status"], "reading_unverified")
                self.assertEqual(evidence["reason"], "counter_pronunciation")
                self.assertNotEqual(evidence["reading"], entry["reading"])

    def test_paragraph_boundaries_do_not_create_words_phrases_or_sentences(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p>人</p><p>気</p><p>気に</p><p>なった。</p>'), rows=[
                ["人気", "にんき", "popularity", ""], ["気になる", "きになる", "be concerned", ""],
                ["人", "ひと", "person", ""], ["気", "き", "spirit", ""],
            ])
            book = enrich_book(folder, resources)
            for entry in book["vocabulary"][:2]:
                self.assertFalse(entry["occurrences"], entry["word"])
            for entry in book["vocabulary"][2:]:
                self.assertTrue(entry["occurrences"], entry["word"])
            chapter = book["chapters"][0]
            self.assertEqual([s["text"] for s in chapter["sentences"]], ["人", "気", "気に", "なった。"])
            for sentence in chapter["sentences"]:
                self.assertEqual(chapter["text"][sentence["start"]:sentence["end"]], sentence["text"])
            for token in chapter["tokens"]:
                self.assertTrue(any(p["start"] <= token["start"] < token["end"] <= p["end"] for p in chapter["paragraphs"]))

    def test_grammar_cannot_match_across_paragraphs(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text=document('<p>しか</p><p>ない。</p>'))
            book = enrich_book(folder, resources)
            self.assertNotIn("shika-nai", {g["id"] for g in book["grammar"]})

    def test_no_navigation_fallback_has_disjoint_sections_and_respects_filters(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, _ = fixture(Path(temp))
            path = folder / "spine.epub"
            with ZipFile(path, "w") as archive:
                archive.writestr("META-INF/container.xml", '<container><rootfiles><rootfile full-path="content.opf"/></rootfiles></container>')
                archive.writestr("content.opf", '<package><manifest><item id="one" href="one.xhtml"/><item id="two" href="two.xhtml"/></manifest><spine><itemref idref="one"/><itemref idref="two"/></spine></package>')
                archive.writestr("one.xhtml", '<html><body><p>海。</p></body></html>')
                archive.writestr("two.xhtml", '<html><body><p>川。</p></body></html>')
            book = read_epub(path)
            self.assertEqual([c["text"] for c in book["chapters"]], ["海。", "川。"])
            filtered = read_epub(path, {"include_titles": ["Section 1"]})
            self.assertEqual([c["text"] for c in filtered["chapters"]], ["海。"])

    def test_filtering_all_chapters_never_restores_excluded_spine(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, _ = fixture(Path(temp))
            path = folder / "sample.epub"
            for config in ({"exclude_titles": ["第一章", "第二章"]}, {"include_titles": ["No such chapter"]}):
                with self.subTest(config=config), self.assertRaisesRegex(ValueError, "No chapters"):
                    read_epub(path, config)


if __name__ == "__main__":
    unittest.main()
