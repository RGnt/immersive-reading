import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from immersive.cli import main as cli_main
from immersive.enrichment import enrich_book, write_reader_data
from immersive.orchestration import BuildOptions, run_job
from immersive.pipeline import main as pipeline_main
from immersive.validation import ValidationError
from test_reader_enrichment import fixture, snapshot


class OrchestrationTests(unittest.TestCase):
    def test_both_reader_commands_use_same_validated_task(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            before = snapshot(folder)
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(pipeline_main(["--reader-only", "--book", str(folder), "--resources", str(resources), "--output", str(root / "one")]), 0)
                self.assertEqual(cli_main(["enrich", "--book", str(folder), "--resources", str(resources), "--output", str(root / "two")]), 0)
            one = next((root / "one/data").glob("*/reader.json"))
            two = next((root / "two/data").glob("*/reader.json"))
            self.assertEqual(one.read_bytes(), two.read_bytes())
            self.assertEqual(snapshot(folder), before)

    def test_cards_only_task_is_quiet_and_does_not_load_documents_or_strokes(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            before = snapshot(folder)
            output = root / "output"
            console, events = io.StringIO(), []
            with patch("immersive.render_outputs.render_book", side_effect=AssertionError("Document export")), patch(
                    "immersive.kanjivg.add_stroke_order", side_effect=AssertionError("Stroke resources")), contextlib.redirect_stdout(console):
                result = run_job(root / "missing", output, resources, book_folder=folder,
                                 options=BuildOptions(mode="study", cards=True, package=True), on_event=events.append)
            self.assertEqual(console.getvalue(), "")
            self.assertTrue(result.archive.is_file())
            self.assertFalse((output / "html").exists())
            self.assertFalse((output / "pdf").exists())
            self.assertTrue((output / "data/flashcards/library.json").is_file())
            self.assertEqual(snapshot(folder), before)
            self.assertEqual(events[-1]["stage"], "complete")

    def test_invalid_reader_prevents_csv_updates_and_exports(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            book = enrich_book(folder, resources)
            book["quality"]["vocabulary_total"] = 999
            before = snapshot(folder)
            with patch("immersive.orchestration.enrich_book", return_value=book), self.assertRaises(ValidationError):
                run_job(root / "missing", root / "output", resources, book_folder=folder,
                        options=BuildOptions(mode="study", update_decks=True))
            self.assertEqual(snapshot(folder), before)
            self.assertFalse((root / "output").exists())

    def test_html_only_validation_is_scoped_to_the_selected_book(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root, print_resources=True)
            output = root / "output"
            unrelated = output / "data/book-1111111111111111/reader.json"
            unrelated.parent.mkdir(parents=True)
            unrelated.write_text('{"unrelated":"incomplete older output"}', encoding="utf-8")
            with patch("immersive.kanjivg.add_stroke_order"), patch("immersive.kanjivg.copy_svg_assets"):
                result = run_job(root / "missing", output, resources, book_folder=folder,
                                 options=BuildOptions(mode="study", html=True))
            self.assertTrue(result.books[0].html_directory.is_dir())
            self.assertFalse((output / "pdf").exists())
            reports = json.loads((output / "reports/verification.json").read_text(encoding="utf-8"))
            self.assertEqual([r["book_id"] for r in reports], [result.books[0].book_id])

    def test_invalid_options_are_rejected_before_input_access(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for options in (BuildOptions(mode="unknown"), BuildOptions(update_decks=True), BuildOptions(mode="decks", pdf=True)):
                with self.subTest(options=options), self.assertRaises(ValueError):
                    run_job(root / "missing", root / "output", root / "resources", options=options)
            self.assertFalse((root / "output").exists())

    def test_pdf_only_selection_is_forwarded_to_rendering_and_validation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root, print_resources=True)
            def render_stub(book, output, *args, **kwargs):
                write_reader_data(book, output)
                path = output / "pdf" / book["id"] / "kanji-master.pdf"
                path.parent.mkdir(parents=True)
                path.write_bytes(b"publication test fixture, validation mocked")
            with patch("immersive.kanjivg.add_stroke_order"), patch("immersive.render_outputs.render_book", side_effect=render_stub) as renderer, patch("immersive.verification.verify") as verify_outputs:
                result = run_job(root / "missing", root / "output", resources, book_folder=folder,
                                 options=BuildOptions(mode="study", pdf=True))
            self.assertFalse(renderer.call_args.kwargs["include_html"])
            self.assertFalse(renderer.call_args.args[4])  # skip_pdf
            self.assertFalse(verify_outputs.call_args.kwargs["html"])
            self.assertTrue(verify_outputs.call_args.kwargs["pdf"])
            self.assertIsNone(result.books[0].html_directory)
            self.assertIsNotNone(result.books[0].pdf_directory)


if __name__ == "__main__":
    unittest.main()
