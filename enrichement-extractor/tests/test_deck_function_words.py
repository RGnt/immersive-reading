"""Particles / auxiliaries (deck ``Kind``) and LLM grammar notes (``deck-grammar.json``).

Contract: specs/16-deck-llm-enrichment-spec.md. The Python pipeline never calls a model; these tests feed it the files
the immersive-reader Deck Builder plugin writes.
"""
import copy
import csv
import json
import tempfile
import unittest
from pathlib import Path

from immersive import deck_grammar
from immersive.card_export import export_book
from immersive.enrichment import enrich_book
from immersive.render_outputs import render_book
from immersive.study_data import prepare_study_data, study_reader_data
from immersive.validation import validate_reader
from immersive.verification import validate_study, verify_html
from test_reader_enrichment import fixture, snapshot

PROJECT = Path(__file__).resolve().parents[1]

HEADER = ["Word", "ReadingKana", "Definitions", "ExampleSentence", "Kind", "PartOfSpeech", "UsageNote"]
ROWS = [
    ["海", "うみ", "sea", "海へ行く。", "vocabulary", "名詞", ""],
    ["見る", "みる", "see", "", "vocabulary", "動詞", ""],
    ["を", "を", "object marker", "", "particle", "助詞", "object: marks what is seen"],
    ["へ", "へ", "toward", "", "particle", "助詞", "direction: where one goes"],
]


def write_deck(folder, rows=ROWS, header=HEADER):
    with (folder / "deck.csv").open("w", encoding="utf-8", newline="") as stream:
        csv.writer(stream).writerows([header, *rows])


def point(pid, pattern, examples, meaning="meaning", explanation="explanation"):
    return {"id": pid, "pattern": pattern, "category": "grammar", "meaning": meaning, "explanation": explanation,
            "first_chapter_index": 0, "first_chapter_title": "", "examples": examples}


def write_grammar(folder, points, notes=None):
    (folder / deck_grammar.FILE_NAME).write_text(json.dumps({
        "schema": deck_grammar.SCHEMA, "status": "llm_generated_examples_text_verified", "book_title": "Reader fixture",
        "generator": {"plugin": "deckBuilder", "model": "test-model", "generated_at": "2026-11-14T00:00:00Z"},
        "grammar": points, "function_word_notes": notes or {}}, ensure_ascii=False), encoding="utf-8")


class FunctionWordTests(unittest.TestCase):
    def test_function_words_are_kept_out_of_vocabulary_sets_and_get_their_own_cards(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            write_deck(folder)
            book = enrich_book(folder, resources)
            kinds = {v["word"]: v["kind"] for v in book["vocabulary"]}
            self.assertEqual(kinds, {"海": "vocabulary", "見る": "vocabulary", "を": "particle", "へ": "particle"})

            study = prepare_study_data(book)
            vocab = {c["title"] for cards in study.vocabulary.values() for c in cards}
            function = {c["title"]: c for cards in study.function_words.values() for c in cards}
            self.assertEqual(vocab, {"海", "見る"})
            self.assertEqual(set(function), {"を", "へ"})
            self.assertEqual(function["を"]["kind"], "function")
            self.assertEqual(function["を"]["details"], "object: marks what is seen")
            self.assertEqual(function["を"]["function_kind"], "particle")
            self.assertEqual(sorted(c["study_number"] for c in function.values()), [1, 2])
            # learning sets (20-word vocabulary sets) contain no particle, and still validate
            words = [w["word"] for group in study.learning_set_records() for w in group["words"]]
            self.assertEqual(sorted(words), ["海", "見る"])
            validate_study(study_reader_data(book, study))

    def test_html_has_a_separate_function_words_page_and_chapter_vocabulary_pages_stay_clean(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            write_deck(folder)
            book = enrich_book(folder, resources)
            study = prepare_study_data(book)
            rendered = render_book(book, root / "output", root / "output/pdf", skip_pdf=True, study=study)

            page = (rendered / "function-words.html").read_text(encoding="utf-8")
            self.assertIn("object: marks what is seen", page)
            self.assertIn("direction: where one goes", page)
            for chapter_page in (rendered / "chapters").glob("*-vocabulary.html"):
                text = chapter_page.read_text(encoding="utf-8")
                self.assertNotIn("object marker", text)
                self.assertNotIn("toward", text)
            self.assertIn("function-words.html", (rendered / "index.html").read_text(encoding="utf-8"))
            # link / first-appearance verification of the whole HTML set must still pass
            verify_html(study_reader_data(book, study), root / "output")

    def test_decks_without_kind_columns_behave_as_before(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            book = enrich_book(folder, resources)
            self.assertTrue(all(v["kind"] == "vocabulary" for v in book["vocabulary"]))
            study = prepare_study_data(book)
            self.assertTrue(all(not cards for cards in study.function_words.values()))
            rendered = render_book(book, root / "output", root / "output/pdf", skip_pdf=True, study=study)
            self.assertFalse((rendered / "function-words.html").exists())
            self.assertNotIn("deck_grammar", book)

    def test_flashcard_export_lists_function_words_as_their_own_kind(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            write_deck(folder)
            exported = export_book(enrich_book(folder, resources))
            cards = [c for ch in exported["chapters"] for c in ch["cards"]]
            self.assertEqual({c["prompt"] for c in cards if c["kind"] == "function"}, {"を", "へ"})
            self.assertEqual({c["prompt"] for c in cards if c["kind"] == "vocabulary"}, {"海", "見る"})

    def test_unknown_kind_values_fall_back_to_vocabulary(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            write_deck(folder, rows=[["海", "うみ", "sea", "", "Verb-ish", "名詞", ""], ["見る", "みる", "see", "", "", "", ""]])
            book = enrich_book(folder, resources)
            self.assertTrue(all(v["kind"] == "vocabulary" for v in book["vocabulary"]))


class DeckGrammarTests(unittest.TestCase):
    def build(self, points, notes=None, rows=ROWS):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        folder, resources = fixture(root)
        write_deck(folder, rows)
        write_grammar(folder, points, notes)
        return root, folder, resources

    def test_verified_points_become_grammar_candidates_and_invented_ones_are_dropped(self):
        root, folder, resources = self.build([
            point("dg-0000000001", "〜を見る", [{"text": "海を見る。", "surface": "を見る", "chapter_index": 0}], "to look at", "Object plus verb."),
            # The model "quoted" a sentence that is not in the book: this must never reach the study material.
            point("dg-0000000002", "〜てしまう", [{"text": "彼は魚を食べてしまった。", "surface": "食べてしまった", "chapter_index": 0}]),
            # Real sentence, but the surface is not inside it.
            point("dg-0000000003", "〜ながら", [{"text": "海を見る。", "surface": "歩きながら", "chapter_index": 0}]),
        ])
        book = enrich_book(folder, resources)

        llm = [g for g in book["grammar"] if g["status"] == "deck_llm_candidate"]
        self.assertEqual([g["id"] for g in llm], ["dg-0000000001"])
        entry = llm[0]
        self.assertEqual((entry["label"], entry["meaning"], entry["note"], entry["model"]), ("〜を見る", "to look at", "Object plus verb.", "test-model"))
        self.assertEqual(entry["first_chapter_id"], "chapter-01")
        occurrence = entry["occurrences"][0]
        self.assertEqual(occurrence["surface"], "を見る")
        self.assertIn("を見る", book["chapters"][0]["text"][occurrence["start"]:occurrence["end"]])
        self.assertTrue(occurrence["sentence_id"])

        report = book["deck_grammar"]
        self.assertEqual((report["points_in_file"], report["points_verified"]), (3, 1))
        self.assertEqual({d["id"] for d in report["dropped"]}, {"dg-0000000002", "dg-0000000003"})
        self.assertEqual(report["model"], "test-model")
        self.assertIn("dg-0000000001", book["chapters"][0]["new_grammar_ids"])
        validate_reader(book)

    def test_grammar_cards_flow_into_study_data_html_and_flashcards(self):
        root, folder, resources = self.build([
            point("dg-0000000001", "〜を見る", [{"text": "海を見る。", "surface": "を見る", "chapter_index": 0}], "to look at", "Object plus verb.")])
        book = enrich_book(folder, resources)
        study = prepare_study_data(book)
        cards = [c for cards in study.grammar.values() for c in cards if c["id"] == "dg-0000000001"]
        self.assertEqual(len(cards), 1)
        self.assertIn("LLM-written explanation", cards[0]["meta"])
        self.assertEqual(cards[0]["plain_example"], "海を見る。")
        rendered = render_book(book, root / "output", root / "output/pdf", skip_pdf=True, study=study)
        self.assertIn("Object plus verb.", (rendered / "grammar.html").read_text(encoding="utf-8"))
        flash = [c for ch in export_book(book, study=study)["chapters"] for c in ch["cards"] if c["kind"] == "grammar"]
        self.assertTrue(any(c["prompt"] == "〜を見る" for c in flash))

    def test_function_word_notes_fill_in_a_missing_csv_usage_note(self):
        rows = [r[:6] + [""] if r[0] == "を" else r for r in ROWS]
        root, folder, resources = self.build([], notes={"を": [{"function": "object", "note": "what is seen", "example": "海を見る。"}]}, rows=rows)
        book = enrich_book(folder, resources)
        notes = {v["word"]: v["usage_note"] for v in book["vocabulary"]}
        self.assertEqual(notes["を"], "object: what is seen")
        self.assertEqual(notes["へ"], "direction: where one goes")  # the CSV note wins where there is one

    def test_malformed_files_are_rejected_loudly(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / deck_grammar.FILE_NAME
            for bad in ('{"schema": "other/1", "grammar": []}', '{"schema": "immersive-deck-grammar/1", "grammar": {}}', "not json",
                        json.dumps({"schema": deck_grammar.SCHEMA, "grammar": [point("bad id", "x", [])]}),
                        json.dumps({"schema": deck_grammar.SCHEMA, "grammar": [point("dg-0000000001", "x", []), point("dg-0000000001", "y", [])]})):
                path.write_text(bad, encoding="utf-8")
                with self.assertRaises(ValueError):
                    deck_grammar.load(path)

    def test_the_grammar_file_is_read_only_input_and_changes_the_cache_key(self):
        root, folder, resources = self.build([
            point("dg-0000000001", "〜を見る", [{"text": "海を見る。", "surface": "を見る", "chapter_index": 0}])])
        before = snapshot(folder)
        book = enrich_book(folder, resources)
        self.assertEqual(snapshot(folder), before)  # EPUB, CSV and grammar file are never modified
        # changing only the grammar file must produce a different reader bundle, not a stale cached one
        write_grammar(folder, [point("dg-0000000009", "〜へ行く", [{"text": "海へ行く。", "surface": "へ行く", "chapter_index": 1}])])
        changed = enrich_book(folder, resources)
        self.assertEqual([g["id"] for g in changed["grammar"] if g["status"] == "deck_llm_candidate"], ["dg-0000000009"])
        self.assertEqual([g["id"] for g in book["grammar"] if g["status"] == "deck_llm_candidate"], ["dg-0000000001"])


class FunctionWordPdfTests(unittest.TestCase):
    def test_function_words_master_pdf_lists_usage_notes_separately(self):
        try:
            import pymupdf
            from immersive.pdf_elements import pdf_session
            from immersive.master_pdf import write_master
        except ImportError:
            self.skipTest("print dependencies are not installed")
        font = PROJECT / "data/fonts/NotoSansJP-Regular.ttf"
        if not font.exists() or font.stat().st_size < 1000:
            self.skipTest("Noto Sans JP font is not available")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            write_deck(folder)
            book = enrich_book(folder, resources)
            study = prepare_study_data(book)
            printable = copy.deepcopy(book)
            printable.setdefault("build_inputs", {}).setdefault("kanjivg", {})
            groups = [(f'{c["number"]:02d}  {c["title"]}', study.function_words[c["id"]]) for c in book["chapters"]]
            target = root / "function-words-master.pdf"
            with pdf_session(font):
                write_master(target, printable, "function", groups)
            with pymupdf.open(target) as doc:
                text = "\n".join(page.get_text() for page in doc)
                self.assertIn("Particles & function words master", text)
                self.assertIn("object: marks what is seen", text)
                self.assertIn("direction: where one goes", text)
                self.assertIn("USAGE / MEANING", text)
                self.assertNotIn("sea", text.split("USAGE / MEANING", 1)[1])  # vocabulary glosses are not in this sheet


SENTENCES = ('<html><body><h1 id="a">第一章</h1><p>海を見る。</p><p>山を見る。</p><p>空を見る。</p><p>星を見る。</p>'
             '<h1 id="b">第二章</h1><p>川を見る。</p></body></html>')


class ChapterBookletTests(unittest.TestCase):
    """Spec 16 / 21: the LLM grammar and particle notes reach the printable per-chapter booklets, not only the master PDFs."""

    def build(self, points, notes=None, rows=ROWS):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        root = Path(self.temp.name)
        folder, resources = fixture(root, text=SENTENCES)
        write_deck(folder, rows)
        write_grammar(folder, points, notes)
        return root, enrich_book(folder, resources)

    POINT = point("dg-0000000001", "〜を見る", [
        {"text": "海を見る。", "surface": "を見る", "chapter_index": 0}, {"text": "山を見る。", "surface": "を見る", "chapter_index": 0},
        {"text": "空を見る。", "surface": "を見る", "chapter_index": 0}, {"text": "星を見る。", "surface": "を見る", "chapter_index": 0},
        {"text": "川を見る。", "surface": "を見る", "chapter_index": 1}], "to look at", "Object plus verb.")

    def test_llm_cards_carry_a_provenance_label_and_up_to_three_distinct_verbatim_examples(self):
        root, book = self.build([self.POINT])
        study = prepare_study_data(book)
        llm = [c for cards in study.grammar.values() for c in cards if c["llm"]]
        self.assertEqual(len(llm), 1)
        card = llm[0]
        self.assertIn("LLM-written explanation (test-model)", card["meta"])
        texts = [card["plain_example"]] + [e["plain_example"] for e in card["extra_examples"]]
        self.assertEqual(len(texts), 3)
        self.assertEqual(len(set(texts)), 3)                      # three different sentences
        chapter_text = "".join(c["text"] for c in book["chapters"])
        self.assertTrue(all(t in chapter_text for t in texts))   # every one is verbatim book text
        self.assertEqual(texts[0], "海を見る。")                   # the first occurrence stays first
        # rule-based candidates are unchanged: one example, no label, no extras
        rule_based = [c for cards in study.grammar.values() for c in cards if not c["llm"]]
        self.assertTrue(all(c["extra_examples"] == [] for c in rule_based))
        # the HTML card shows the extra examples too
        from immersive.render import card_html
        html = card_html(dict(card, example=""))
        self.assertEqual(html.count('class="example"'), 3)

    def test_booklet_chapters_are_exactly_the_chapters_that_introduce_entries(self):
        root, book = self.build([self.POINT], rows=[["海", "うみ", "sea", "", "vocabulary", "名詞", ""],
                                                    ["を", "を", "object marker", "", "particle", "助詞", "object: marks what is seen"]])
        study = prepare_study_data(book)
        from immersive.study_data import function_word_chapters, grammar_chapters
        self.assertEqual(grammar_chapters(book), {cid for cid, cards in study.grammar.items() if cards})
        self.assertEqual(function_word_chapters(book), {cid for cid, cards in study.function_words.items() if cards})
        self.assertEqual(function_word_chapters(book), {"chapter-01"})          # を first appears in chapter 1; chapter 2 introduces none

    def test_render_writes_grammar_and_particle_booklets_only_for_chapters_that_have_entries(self):
        try:
            import pymupdf
        except ImportError:
            self.skipTest("print dependencies are not installed")
        font = PROJECT / "data/fonts/NotoSansJP-Regular.ttf"
        if not font.exists() or font.stat().st_size < 1000:
            self.skipTest("Noto Sans JP font is not available")
        from unittest import mock
        root, book = self.build([self.POINT], notes={"を": [{"function": "object", "note": "marks what is seen", "example": "海を見る。"}]},
                                rows=[["海", "うみ", "sea", "", "vocabulary", "名詞", ""],
                                      ["を", "を", "object marker", "", "particle", "助詞", ""]])
        study = prepare_study_data(book)
        printable = copy.deepcopy(book)
        printable.setdefault("build_inputs", {}).setdefault("kanjivg", {})
        output = root / "output"
        # Only the new booklets are under test: the kanji / chapter-vocabulary writers need the full print resource set.
        with mock.patch("immersive.kanji_pdf.write_kanji_document"), mock.patch("immersive.master_pdf.write_chapter_vocabulary"):
            render_book(printable, output, output / "pdf", font_path=str(font), skip_pdf=False, study=study)
        pdfs = {p.name for p in (output / "pdf" / book["id"]).glob("*.pdf")}
        from immersive.study_data import function_word_chapters, grammar_chapters
        self.assertEqual({n for n in pdfs if n.endswith("-grammar.pdf")}, {f"{c}-grammar.pdf" for c in grammar_chapters(book)})
        self.assertEqual({n for n in pdfs if n.endswith("-function-words.pdf")}, {f"{c}-function-words.pdf" for c in function_word_chapters(book)})
        self.assertIn("chapter-01-grammar.pdf", pdfs)
        self.assertNotIn("chapter-02-function-words.pdf", pdfs)                   # no empty booklets
        with pymupdf.open(output / "pdf" / book["id"] / "chapter-01-grammar.pdf") as doc:
            text = "\n".join(page.get_text() for page in doc)
        self.assertIn("LLM-written explanation (test-model)", text)
        self.assertIn("Object plus verb.", text)
        self.assertGreaterEqual(sum(text.count(w) for w in ("海", "山", "空")), 3)   # the extra verbatim examples are printed
        with pymupdf.open(output / "pdf" / book["id"] / "chapter-01-function-words.pdf") as doc:
            text = "\n".join(page.get_text() for page in doc)
        self.assertIn("marks what is seen", text)
        index = (output / "html" / book["id"] / "index.html").read_text(encoding="utf-8")
        self.assertIn("chapter-01-grammar.pdf", index)
        self.assertIn("chapter-01-function-words.pdf", index)
        self.assertNotIn("chapter-02-function-words.pdf", index)


if __name__ == "__main__":
    unittest.main()
