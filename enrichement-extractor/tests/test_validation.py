import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from immersive.build import load_decks
from immersive.enrichment import enrich_book, save_json, write_reader_data
from immersive.render_outputs import render_book
from immersive.validation import ValidationError, validate_reader
from immersive.verification import verify
from test_reader_enrichment import fixture, snapshot


PROJECT = Path(__file__).resolve().parents[1]


class ValidationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.root = Path(cls.temporary.name)
        cls.folder, cls.resources = fixture(cls.root)
        cls.book = enrich_book(cls.folder, cls.resources)

    def test_valid_reader_reports_coverage_without_requiring_documents(self):
        original = copy.deepcopy(self.book)
        diagnostics = validate_reader(self.book)
        self.assertIn("missing_kanji_dictionary", {d["code"] for d in diagnostics})
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            write_reader_data(self.book, output)
            summaries = verify(output, html=False, pdf=False, write_report=False)
            self.assertEqual(summaries[0]["book_id"], self.book["id"])
            self.assertFalse((output / "reports").exists())
            self.assertEqual(len(list(output.rglob("*.json"))), 3)
        self.assertEqual(self.book, original)

    def test_schema_references_spans_and_provenance_have_actionable_errors(self):
        cases = []
        def bad(change, expected):
            book = copy.deepcopy(self.book)
            change(book)
            cases.append((book, expected))
        bad(lambda b: b.pop("title"), "required property")
        bad(lambda b: b["vocabulary"].append(copy.deepcopy(b["vocabulary"][0])), "duplicate id")
        bad(lambda b: b["chapters"][0]["tokens"][0].update(start_utf16=999), "UTF-16 offsets")
        bad(lambda b: b["vocabulary"][0]["occurrences"][0].update(sentence_id="missing"), "unknown occurrence sentence")
        bad(lambda b: b["vocabulary"][0].update(first_chapter_id="chapter-02"), "first_chapter_id")
        bad(lambda b: b["chapters"][0]["sentences"][0]["sources"][0].update(paragraph_index=999), "unknown sentence source")
        bad(lambda b: b["chapters"][0]["sentences"][0].update(translation="not allowed"), "sentence translations")
        bad(lambda b: b["chapters"][0]["sentences"][0]["display_ruby"][0].update(source="invented"), "source")
        bad(lambda b: b["kanji"][0]["dictionary"].pop("meaning_source"), "meaning_source")
        bad(lambda b: b["quality"].update(vocabulary_matched=999), "quality vocabulary counts")
        for book, expected in cases:
            with self.subTest(expected=expected), self.assertRaisesRegex(ValidationError, expected):
                validate_reader(book)

    def test_invalid_config_is_rejected_before_epub_processing(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            (folder / "study.config.json").write_text('{"aliases":{"海":"not-an-array"}}', encoding="utf-8")
            before = snapshot(folder)
            with patch("immersive.build.read_epub", side_effect=AssertionError("Should not parse EPUB")):
                with self.assertRaisesRegex(ValidationError, r"study.config.json.aliases"):
                    enrich_book(folder, resources)
            self.assertEqual(snapshot(folder), before)

    def test_malformed_decks_report_file_and_record(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "bad.csv"
            for contents, message in (("Word,Word,ReadingKana,Definitions\n海,海,うみ,sea\n", "duplicate CSV"),
                                      ("Word,ReadingKana,Definitions\n海,うみ\n", "record 2")):
                path.write_text(contents, encoding="utf-8")
                with self.subTest(message=message), self.assertRaisesRegex(ValueError, message):
                    load_decks([path])

    def test_html_validation_is_separate_and_rejects_broken_links(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            render_book(self.book, output, output / "pdf", skip_pdf=True)
            summary, = verify(output, html=True, pdf=False, write_report=False)
            self.assertEqual(summary["unique_chapter_cards"]["vocabulary"], 2)
            path = output / "html" / self.book["id"] / "chapters/chapter-01-vocabulary.html"
            path.write_text(path.read_text(encoding="utf-8").replace('../index.html', '../missing.html'), encoding="utf-8")
            with self.assertRaisesRegex(ValidationError, "Missing local link"):
                verify(output, html=True, pdf=False, write_report=False)

    def test_validation_stays_enabled_under_python_optimization(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "bad.json"
            book = copy.deepcopy(self.book)
            book["chapters"][0]["tokens"][0]["start_utf16"] = 999
            save_json(path, book)
            script = '''
import json,sys
from immersive.validation import validate_reader, ValidationError, require
for check in (lambda: validate_reader(json.load(open(sys.argv[1],encoding='utf-8'))), lambda: require(False,'still enabled')):
    try: check()
    except ValidationError: continue
    raise SystemExit('Validation was disabled')
'''
            completed = subprocess.run([sys.executable, "-O", "-c", script, str(path)], cwd=PROJECT, capture_output=True, text=True)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)

    def test_empty_analysis_token_is_a_diagnostic_not_an_invalid_occurrence(self):
        book = copy.deepcopy(self.book)
        token = copy.deepcopy(book["chapters"][0]["tokens"][0])
        token.update(surface="", end=token["start"], end_utf16=token["start_utf16"], vocabulary_ids=[])
        book["chapters"][0]["tokens"].append(token)
        self.assertIn("empty_analysis_tokens", {d["code"] for d in validate_reader(book)})

    def test_unresolved_readings_are_reader_diagnostics(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), text='<html><body><h1 id="a">第一章</h1><p>𠮷。</p><h1 id="b">第二章</h1><p>海。</p></body></html>')
            book = enrich_book(folder, resources)
            self.assertIn("unresolved_furigana", {d["code"] for d in validate_reader(book)})
            output = Path(temp) / "output"
            render_book(book, output, output / "pdf", skip_pdf=True)
            with self.assertRaisesRegex(ValidationError, "missing furigana"):
                verify(output, html=True, pdf=False, write_report=False)

    def test_companion_json_files_must_agree_with_reader_data(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            data = write_reader_data(self.book, output)
            save_json(data / "quality.json", {})
            with self.assertRaisesRegex(ValidationError, "Quality file disagrees"):
                verify(output, html=False, pdf=False, write_report=False)
            write_reader_data(self.book, output)
            lookup = json.loads((data / "lookup.json").read_text(encoding="utf-8"))
            lookup["forms"]["wrong"] = ["unknown-id"]
            save_json(data / "lookup.json", lookup)
            with self.assertRaisesRegex(ValidationError, "Unknown lookup"):
                verify(output, html=False, pdf=False, write_report=False)

    def test_empty_vocabulary_sets_are_valid_when_no_deck_entries_match(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp), rows=[])
            book = enrich_book(folder, resources)
            output = Path(temp) / "output"
            render_book(book, output, output / "pdf", skip_pdf=True)
            summaries = verify(output, html=True, pdf=False, write_report=False)
            self.assertEqual(summaries[0]["matched"], 0)


if __name__ == "__main__":
    unittest.main()
