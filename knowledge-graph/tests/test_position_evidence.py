import unittest

from immersive_knowledge.evidence import locate, normalize
from immersive_knowledge.position import StoryPosition, as_of, chapter_key


class PositionTests(unittest.TestCase):
    def test_order_is_volume_then_chapter_then_offset(self):
        P = StoryPosition.parse
        self.assertLess(P("1:2"), P("1:3"))
        self.assertLess(P("1:9"), P("2:0"))
        self.assertEqual(P("2:1:5"), P("2:1:5"))
        self.assertLess(P("1:2:5"), P("1:2:6"))

    def test_key_order_matches_tuple_order(self):
        a, b, c = StoryPosition(1, 9, 999_999), StoryPosition(2, 0, 0), StoryPosition(2, 0, 1)
        self.assertLess(a.key, b.key)
        self.assertLess(b.key, c.key)
        self.assertEqual(StoryPosition.from_key(c.key), c)

    def test_parse_and_format_round_trip(self):
        for text in ("1:0", "3:12", "2:4:350"):
            self.assertEqual(str(StoryPosition.parse(text)), text)

    def test_invalid_positions_are_rejected(self):
        for bad in ("", "1", "a:b", "1:2:3:4", "-1:0", "1:2:x"):
            with self.assertRaises(ValueError):
                StoryPosition.parse(bad)
        with self.assertRaises(ValueError):
            StoryPosition(1, 10 ** 6, 0)

    def test_as_of_is_the_start_of_the_chapter(self):
        self.assertEqual(as_of(1, 2), StoryPosition(1, 2, 0))
        self.assertEqual(as_of(1, 2).key, chapter_key(1, 2))
        # anything inside chapter 2 is at or after the boundary, so chapter 2 facts are not visible "as of 1:2"
        self.assertGreater(StoryPosition(1, 2, 1).key, as_of(1, 2).key)


class EvidenceTests(unittest.TestCase):
    TEXT = "春の朝、少女ミナは森の入口で出会った。\n　仮面の男は「この森には　近づくな」と言った。\n"

    def test_unique_quote_returns_original_offsets(self):
        loc = locate(self.TEXT, "少女ミナは森の入口")
        self.assertEqual(self.TEXT[loc.start:loc.end], "少女ミナは森の入口")
        self.assertEqual(loc.occurrences, 1)

    def test_whitespace_differences_do_not_matter_and_offsets_stay_original(self):
        loc = locate(self.TEXT, "この森には近づくな")
        self.assertEqual(self.TEXT[loc.start:loc.end], "この森には　近づくな")

    def test_full_width_forms_match_after_nfkc(self):
        loc = locate("ミナは１２歳の少女だった。", "ミナは12歳の少女")
        self.assertIsNotNone(loc)

    def test_missing_short_and_empty_quotes_are_rejected(self):
        self.assertIsNone(locate(self.TEXT, "存在しない一文です"))
        self.assertIsNone(locate(self.TEXT, "仮面"))     # too short (< 4 normalised characters)
        self.assertIsNone(locate(self.TEXT, "   "))
        self.assertIsNone(locate(self.TEXT, "あ" * 500))

    def test_repeated_quote_uses_the_last_occurrence_the_spoiler_safe_choice(self):
        text = "同じ言葉です。間の文章。同じ言葉です。"
        loc = locate(text, "同じ言葉です")
        self.assertEqual(loc.occurrences, 2)
        self.assertEqual(loc.start, text.rindex("同じ言葉です"))

    def test_brackets_and_sentence_end_marks_at_the_edges_are_ignored_but_not_inside(self):
        text = "春の朝。「お帰りなさいませ、ユナ様。アップデート情報があります」と言った。"
        for quote in ("「お帰りなさいませ、ユナ様」", "お帰りなさいませ、ユナ様。", "『お帰りなさいませ、ユナ様』。", "お帰りなさいませ、ユナ様"):
            loc = locate(text, quote)
            self.assertIsNotNone(loc, quote)
            self.assertEqual(text[loc.start:loc.end], "お帰りなさいませ、ユナ様")
        # punctuation INSIDE the quote must match: a reworded or reflowed middle is not the same text
        self.assertIsNone(locate(text, "お帰りなさいませ、ユナ様！アップデート情報"))
        self.assertIsNone(locate(text, "「」。、"))                    # nothing left after the edges are trimmed

    def test_normalization_can_be_reused(self):
        prepared = normalize(self.TEXT)
        self.assertEqual(locate(self.TEXT, "少女ミナは森", normalized=prepared), locate(self.TEXT, "少女ミナは森"))


if __name__ == "__main__":
    unittest.main()
