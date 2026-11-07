import copy
import json
import subprocess
import sys
import tempfile
import unittest
from dataclasses import asdict
from pathlib import Path
from unittest.mock import patch

from immersive.card_export import export_book
from immersive.enrichment import enrich_book
from immersive.render import render_example
from immersive.render_outputs import render_book
from immersive.study_data import StudyIndexes, prepare_study_data, study_reader_data
from test_reader_enrichment import fixture, snapshot


PROJECT = Path(__file__).resolve().parents[1]


class StudyDataTests(unittest.TestCase):
    def test_one_preparation_is_shared_without_modifying_book_or_model(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            inputs = snapshot(folder)
            book = enrich_book(folder, resources)
            original = copy.deepcopy(book)
            with patch.object(StudyIndexes, "from_book", wraps=StudyIndexes.from_book) as build_indexes:
                study = prepare_study_data(book)
                model_before = asdict(study)
                exported = export_book(book, study=study)
                rendered = render_book(book, root / "output", root / "output/pdf", skip_pdf=True, study=study)
                self.assertEqual(build_indexes.call_count, 1)
                self.assertEqual(asdict(study), model_before)
            self.assertEqual(book, original)
            self.assertEqual(snapshot(folder), inputs)
            self.assertFalse((root / "output/pdf").exists())
            self.assertTrue((rendered / "chapters/chapter-01-kanji.html").exists())
            written = json.loads((root / "output/data" / book["id"] / "reader.json").read_text(encoding="utf-8"))
            self.assertTrue(written["learning_sets"])
            self.assertIn("context_source_notes", written["quality"])
            self.assertNotIn("learning_sets", book)
            self.assertNotIn("context_source_notes", book["quality"])
            self.assertEqual(exported, export_book(book))
            vocab = [c for ch in exported["chapters"] for c in ch["cards"] if c["kind"] == "vocabulary"]
            self.assertEqual({c["prompt"] for c in vocab}, {"海", "見る"})
            self.assertTrue(all(c["set_number"] == 1 for c in vocab))
            self.assertTrue(all(c["example_ruby"] for c in vocab))

    def test_cards_are_plain_data_with_relative_highlights_and_source_ruby(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, resources = fixture(Path(temp))
            book = enrich_book(folder, resources)
            study = prepare_study_data(book)
            for collection in (study.vocabulary, study.kanji, study.grammar, study.context):
                for cards in collection.values():
                    for card in cards:
                        self.assertNotIn("example", card)
                        self.assertNotIn("<ruby>", json.dumps(card, ensure_ascii=False))
                        highlight = card.get("example_highlight")
                        if highlight is not None:
                            self.assertTrue(0 <= highlight["start"] < highlight["end"] <= len(card["plain_example"]))
            sea = next(c for c in study.vocabulary["chapter-01"] if c["title"] == "海")
            self.assertEqual(sea["example_provenance"]["source"], "csv")
            self.assertEqual(sea["example_provenance"]["chapter_id"], "chapter-02")
            self.assertEqual(sea["ruby"][0]["source"], "epub")
            self.assertEqual(sea["plain_example"], "海へ行く。")
            self.assertEqual(sea["example_highlight"], {"start": 0, "end": 1})

    def test_html_escapes_text_and_preserves_ruby_and_highlight(self):
        card = {"plain_example": "海<&", "ruby": [{"start": 0, "end": 1, "reading": "うみ"}],
                "example_highlight": {"start": 0, "end": 1}}
        original = copy.deepcopy(card)
        self.assertEqual(render_example(card), '<mark><ruby>海<rt>うみ</rt></ruby></mark>&lt;&amp;')
        self.assertEqual(card, original)

    def test_study_model_for_wrong_book_is_rejected_before_output(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            book = enrich_book(folder, resources)
            study = prepare_study_data(book)
            other = dict(book, id="another-book")
            with self.assertRaisesRegex(ValueError, "another book"):
                render_book(other, root / "output", root / "output/pdf", skip_pdf=True, study=study)
            with self.assertRaisesRegex(ValueError, "another book"):
                export_book(other, study=study)
            self.assertFalse((root / "output").exists())
            partial = prepare_study_data(book, include_context=False)
            with self.assertRaisesRegex(ValueError, "include_context=True"):
                study_reader_data(book, partial)

    def test_preparation_card_export_and_html_work_with_print_libraries_blocked(self):
        script = '''
import importlib.abc, sys
from pathlib import Path
class BlockRenderers(importlib.abc.MetaPathFinder):
    allow_html = False
    def find_spec(self, fullname, path=None, target=None):
        if fullname.split('.')[0] in {'reportlab','fontTools','pymupdf','fitz'}:
            raise AssertionError('Print dependency loaded: ' + fullname)
        if fullname.startswith('immersive.render') and not self.allow_html:
            raise AssertionError('Renderer loaded during data preparation: ' + fullname)
block = BlockRenderers()
sys.meta_path.insert(0, block)
from immersive.enrichment import enrich_book
from immersive.study_data import prepare_study_data
from immersive.card_export import export_book
folder, resources, output = map(Path, sys.argv[1:])
book = enrich_book(folder, resources)
study = prepare_study_data(book)
assert export_book(book, study=study)['chapters']
assert 'immersive.render' not in sys.modules
block.allow_html = True
from immersive.render_outputs import render_book
render_book(book, output, output/'pdf', skip_pdf=True, study=study)
assert not (output/'pdf').exists()
'''
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            completed = subprocess.run([sys.executable, "-c", script, str(folder), str(resources), str(root / "output")],
                                       cwd=PROJECT, capture_output=True, text=True)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            legacy = subprocess.run([sys.executable, str(PROJECT / "scripts/export_flashcards.py"), "--help"],
                                    cwd=root, capture_output=True, text=True)
            self.assertEqual(legacy.returncode, 0, legacy.stdout + legacy.stderr)


if __name__ == "__main__":
    unittest.main()
