import unittest
from immersive.pos_chapter import classify_morpheme, process_chapter_tokens, process_payload


class PosChapterTests(unittest.TestCase):
    def test_classify_morpheme_categories(self):
        # Noun
        wt, st, cd = classify_morpheme("本", ("名詞", "一般"))
        self.assertEqual(wt, "noun")
        self.assertEqual(cd, "noun")

        # Single-kana particle
        wt, st, cd = classify_morpheme("を", ("助詞", "格助詞"))
        self.assertEqual(wt, "particle")
        self.assertEqual(st, "particle-single")
        self.assertEqual(cd, "prt-single")

        # Multi-kana particle
        wt, st, cd = classify_morpheme("から", ("助詞", "接続助詞"))
        self.assertEqual(wt, "particle")
        self.assertEqual(st, "particle-multi")
        self.assertEqual(cd, "prt-multi")

        # Verbs: Godan, Ichidan, Irregular
        wt, st, cd = classify_morpheme("書く", ("動詞", "一般", "*", "*", "五段-カ行"))
        self.assertEqual(wt, "verb")
        self.assertEqual(st, "verb-u")
        self.assertEqual(cd, "verb-u")

        wt, st, cd = classify_morpheme("食べる", ("動詞", "一般", "*", "*", "一段-バ行"))
        self.assertEqual(wt, "verb")
        self.assertEqual(st, "verb-ru")
        self.assertEqual(cd, "verb-ru")

        wt, st, cd = classify_morpheme("来る", ("動詞", "一般", "*", "*", "カ行変格"))
        self.assertEqual(wt, "verb")
        self.assertEqual(st, "verb-irr")
        self.assertEqual(cd, "verb-irr")

        # Adjectives: i-adj, na-adj
        wt, st, cd = classify_morpheme("赤い", ("形容詞", "一般", "*", "*", "形容詞"))
        self.assertEqual(wt, "adjective")
        self.assertEqual(st, "adjective-i")
        self.assertEqual(cd, "adj-i")

        wt, st, cd = classify_morpheme("静か", ("形状詞", "一般"))
        self.assertEqual(wt, "adjective")
        self.assertEqual(st, "adjective-na")
        self.assertEqual(cd, "adj-na")

        # Suffix & Prefix
        wt, st, cd = classify_morpheme("達", ("接尾辞", "名詞的"))
        self.assertEqual(wt, "suffix")
        self.assertEqual(st, "suffix")
        self.assertEqual(cd, "suffix")

        wt, st, cd = classify_morpheme("超", ("接頭辞", "*"))
        self.assertEqual(wt, "suffix")
        self.assertEqual(st, "prefix")
        self.assertEqual(cd, "suffix")

    def test_classify_kanji_words_accurately(self):
        # Kanji na-adjectives (tagged as 形状詞 or 名詞 with 形状詞可能)
        wt, st, cd = classify_morpheme("便利", ("名詞", "普通名詞", "形状詞可能", "*", "*", "*"))
        self.assertEqual(wt, "adjective")
        self.assertEqual(st, "adjective-na")
        self.assertEqual(cd, "adj-na")

        wt, st, cd = classify_morpheme("親切", ("名詞", "普通名詞", "形状詞可能", "*", "*", "*"))
        self.assertEqual(wt, "adjective")
        self.assertEqual(st, "adjective-na")
        self.assertEqual(cd, "adj-na")

        wt, st, cd = classify_morpheme("重要", ("形状詞", "一般", "*", "*", "*", "*"))
        self.assertEqual(wt, "adjective")
        self.assertEqual(st, "adjective-na")
        self.assertEqual(cd, "adj-na")

        # Kanji adverbs (tagged as 副詞 or 名詞 with 副詞可能)
        wt, st, cd = classify_morpheme("突然", ("副詞", "*", "*", "*", "*", "*"))
        self.assertEqual(wt, "adverb")
        self.assertEqual(st, "adverb")
        self.assertEqual(cd, "adverb")

        wt, st, cd = classify_morpheme("時々", ("名詞", "普通名詞", "副詞可能", "*", "*", "*"))
        self.assertEqual(wt, "adverb")
        self.assertEqual(st, "adverb")
        self.assertEqual(cd, "adverb")

        wt, st, cd = classify_morpheme("一応", ("名詞", "普通名詞", "副詞可能", "*", "*", "*"))
        self.assertEqual(wt, "adverb")
        self.assertEqual(st, "adverb")
        self.assertEqual(cd, "adverb")

        # Kanji pure nouns
        wt, st, cd = classify_morpheme("学校", ("名詞", "普通名詞", "一般", "*", "*", "*"))
        self.assertEqual(wt, "noun")
        self.assertEqual(st, "noun")
        self.assertEqual(cd, "noun")

    def test_process_payload(self):
        payload = {
            "bookId": "test-book",
            "chapterHref": "test.xhtml",
            "tokens": [
                {"id": 10, "surface": "猫", "pos": "名詞"},
                {"id": 11, "surface": "が", "pos": "助詞"},
                {"id": 12, "surface": "可愛い", "pos": "形容詞", "conjType": "形容詞"},
            ],
        }
        res = process_payload(payload)
        self.assertEqual(res["bookId"], "test-book")
        self.assertEqual(len(res["classifications"]), 3)
        self.assertIn("adjective", res["classifications"][2]["classes"])
        self.assertIn("adjective-i", res["classifications"][2]["classes"])
        self.assertIn("ir-pos-adj-i", res["classifications"][2]["classes"])

    def test_process_payload_paragraphs_with_sudachi(self):
        payload = {
            "bookId": "test-book-2",
            "chapterHref": "ch1.xhtml",
            "paragraphs": [
                {
                    "id": 0,
                    "text": "魔女たちの茶会が開かれる。"
                }
            ]
        }
        res = process_payload(payload)
        self.assertEqual(res["bookId"], "test-book-2")
        self.assertIn("paragraphs", res)
        self.assertEqual(len(res["paragraphs"]), 1)
        morphemes = res["paragraphs"][0]["morphemes"]
        self.assertTrue(len(morphemes) >= 5)

        # Check that 'たち' is classified as suffix
        tachi = next((m for m in morphemes if m["surface"] == "たち"), None)
        self.assertIsNotNone(tachi)
        self.assertEqual(tachi["wordType"], "suffix")

        # Check that 'の' is classified as single particle
        no_prt = next((m for m in morphemes if m["surface"] == "の"), None)
        self.assertIsNotNone(no_prt)
        self.assertEqual(no_prt["wordType"], "particle")
        self.assertEqual(no_prt["subType"], "particle-single")

    def test_process_payload_paragraphs_kanji_words(self):
        payload = {
            "bookId": "test-kanji-book",
            "chapterHref": "ch2.xhtml",
            "paragraphs": [
                {
                    "id": 0,
                    "text": "便利な道具を使って毎日勉強する。"
                }
            ]
        }
        res = process_payload(payload)
        morphemes = res["paragraphs"][0]["morphemes"]

        # '便利' must be categorized as adjective-na
        benri = next((m for m in morphemes if m["surface"] == "便利"), None)
        self.assertIsNotNone(benri)
        self.assertEqual(benri["wordType"], "adjective")
        self.assertEqual(benri["subType"], "adjective-na")
        self.assertIn("adjective-na", benri["classes"])

        # '毎日' must be categorized as adverb
        mainichi = next((m for m in morphemes if m["surface"] == "毎日"), None)
        self.assertIsNotNone(mainichi)
        self.assertEqual(mainichi["wordType"], "adverb")
        self.assertEqual(mainichi["subType"], "adverb")

        # '勉強' followed by 'する' must be categorized as irregular verb
        benkyou = next((m for m in morphemes if m["surface"] == "勉強"), None)
        self.assertIsNotNone(benkyou)
        self.assertEqual(benkyou["wordType"], "verb")
        self.assertEqual(benkyou["subType"], "verb-irr")


if __name__ == "__main__":
    unittest.main()

