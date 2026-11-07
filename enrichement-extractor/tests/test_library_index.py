import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from immersive.cli import main as cli_main
from immersive.pipeline import run_stages
from immersive.enrichment import enrich_book
from immersive.render_outputs import render_book
from test_reader_enrichment import fixture


class LibraryIndexTests(unittest.TestCase):
    def check_single_book_build(self, command):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / "output"
            first_root, second_root = root / "first", root / "second"
            first_root.mkdir(); second_root.mkdir()
            old_folder, old_resources = fixture(first_root)
            new_folder, new_resources = fixture(second_root, print_resources=True, text='<html><body><h1 id="a">第一章</h1><p>海へ行く。</p><h1 id="b">第二章</h1><p>海を見る。</p></body></html>')
            old = enrich_book(old_folder, old_resources)
            old["title"] = "Earlier <book>"
            new = enrich_book(new_folder, new_resources)
            new["title"] = "New & revised"
            render_book(old, output, output / "pdf", skip_pdf=True)
            with patch("immersive.orchestration.enrich_book", return_value=new), patch("immersive.kanjivg.add_stroke_order"), patch("immersive.kanjivg.copy_svg_assets"):
                if command == "pipeline":
                    run_stages(root / "missing-books", output, new_resources, book_folder=new_folder,
                               skip_pdf=True, skip_cards=True, skip_package=True)
                else:
                    cli_main(["build", "--book", str(new_folder), "--resources", str(new_resources),
                              "--output", str(output), "--skip-pdf"])
            index = (output / "html/index.html").read_text(encoding="utf-8")
            self.assertIn(old["id"] + "/index.html", index)
            self.assertIn(new["id"] + "/index.html", index)
            self.assertIn("Earlier &lt;book&gt;", index)
            self.assertIn("New &amp; revised", index)
            self.assertEqual(index.count(new["id"] + "/index.html"), 1)

    def test_pipeline_single_book_build_preserves_existing_library(self):
        self.check_single_book_build("pipeline")

    def test_cli_single_book_build_preserves_existing_library(self):
        self.check_single_book_build("cli")


if __name__ == "__main__":
    unittest.main()
