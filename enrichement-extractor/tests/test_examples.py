import unittest
from immersive.examples import select_example


class ExampleSelectionTests(unittest.TestCase):
    def setUp(self):
        self.chapter = {"id":"chapter-01", "href":"text/one.xhtml", "text":"海に行く。海で泳ぐ。", "sentences":[
            {"id":"s1","text":"海に行く。","start":0,"end":5},
            {"id":"s2","text":"海で泳ぐ。","start":5,"end":10}]}
        self.later = {"id":"chapter-02","href":"text/two.xhtml","text":"海を見る。","sentences":[]}
        self.book = {"chapters":[self.chapter,self.later]}
        self.entry = {"word":"海","reading":"うみ","occurrences":[
            {"chapter_id":"chapter-01","start":5,"end":6,"sentence_id":"s2"},
            {"chapter_id":"chapter-01","start":0,"end":1,"sentence_id":"s1"}],"deck_examples":[]}

    def test_no_csv_uses_first_occurrence_even_when_input_is_unsorted(self):
        s = select_example(self.book,self.chapter,self.entry)
        self.assertEqual(s["source"],"epub_first_occurrence")
        self.assertEqual(s["text"],"海に行く。")
        self.assertEqual(s["sentence_id"],"s1")

    def test_csv_is_preferred_over_first_occurrence(self):
        self.entry["deck_examples"] = [{"text":"海で泳ぐ。","source":"deck.csv","csv_row":9}]
        s = select_example(self.book,self.chapter,self.entry)
        self.assertEqual(s["source"],"csv")
        self.assertEqual(s["text"],"海で泳ぐ。")
        self.assertEqual(s["csv_row"],9)

    def test_csv_from_later_chapter_is_retained_with_its_real_location(self):
        self.entry["deck_examples"] = [{"text":"海を見る。","source":"deck.csv"}]
        s = select_example(self.book,self.chapter,self.entry)
        self.assertEqual(s["chapter_id"],"chapter-02")
        self.assertEqual(s["source"],"csv")

    def test_unaligned_csv_is_not_replaced_or_given_invented_ruby(self):
        self.entry["deck_examples"] = [{"text":"未整列の文。","source":"deck.csv"}]
        s = select_example(self.book,self.chapter,self.entry)
        self.assertEqual(s["text"],"未整列の文。")
        self.assertEqual(s["status"],"source_provided_not_aligned")
        self.assertIsNone(s["start"])

    def test_blank_csv_counts_as_missing(self):
        self.entry["deck_examples"] = [{"text":" \n "}]
        self.assertEqual(select_example(self.book,self.chapter,self.entry)["source"],"epub_first_occurrence")

    def test_no_sources_is_explicitly_unavailable(self):
        self.entry["occurrences"] = []
        self.assertEqual(select_example(self.book,self.chapter,self.entry)["source"],"unavailable")


if __name__ == "__main__":
    unittest.main()
