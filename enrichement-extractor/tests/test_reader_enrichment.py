"""Self-contained reader/task-boundary tests; no supplied books or outputs."""
import copy
import csv
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile

import jsonschema

from immersive.enrichment import enrich_book, write_reader_data
from immersive.pipeline import run_stages


PROJECT = Path(__file__).resolve().parents[1]


def fixture(root, *, text=None, rows=None, print_resources=False):
    folder = root / "books" / "sample book"
    folder.mkdir(parents=True)
    with ZipFile(folder / "sample.epub", "w") as archive:
        archive.writestr("META-INF/container.xml", '<container><rootfiles><rootfile full-path="OPS/content.opf"/></rootfiles></container>')
        archive.writestr("OPS/content.opf", '<package><metadata><title>Reader fixture</title></metadata><manifest><item id="text" href="text.xhtml"/><item id="nav" href="nav.xhtml" properties="nav"/></manifest><spine><itemref idref="text"/></spine></package>')
        archive.writestr("OPS/nav.xhtml", '<html xmlns:epub="http://www.idpf.org/2007/ops"><nav epub:type="toc"><a href="text.xhtml#a">第一章</a><a href="text.xhtml#b">第二章</a></nav></html>')
        archive.writestr("OPS/text.xhtml", text if text is not None else '<html><body><h1 id="a">第一章</h1><p><ruby>海<rt>うみ</rt></ruby>を見る。</p><h1 id="b">第二章</h1><p><ruby>海<rt>うみ</rt></ruby>へ行く。</p></body></html>')
    with (folder / "deck.csv").open("w", encoding="utf-8", newline="") as stream:
        csv.writer(stream).writerows([
            ["Word", "ReadingKana", "Definitions", "ExampleSentence"],
            *(rows if rows is not None else [
                ["海", "うみ", "sea", "海へ行く。"],
                ["見る", "みる", "see", ""],
            ]),
        ])
    resources = root / "resources"
    resources.mkdir()
    (resources / "kanjidic2.xml").write_text('<kanjidic2><character><literal>海</literal><reading_meaning><rmgroup><reading r_type="ja_on">カイ</reading><reading r_type="ja_kun">うみ</reading><meaning>sea</meaning></rmgroup></reading_meaning></character></kanjidic2>', encoding="utf-8")
    # Create the compressed dictionary expected by the public default.
    import gzip
    with gzip.open(resources / "kanjidic2.xml.gz", "wb") as stream:
        stream.write((resources / "kanjidic2.xml").read_bytes())
    meanings = resources / "kanji-alive"
    meanings.mkdir()
    (meanings / "ka_data.csv").write_text("kanji,kmeaning\n海,sea\n", encoding="utf-8")
    (meanings / "source.json").write_text(json.dumps({
        "sha256": hashlib.sha256((meanings / "ka_data.csv").read_bytes()).hexdigest(),
        "url": "https://example.test/fixture", "copyright": "Test fixture", "commit": "fixture",
    }), encoding="utf-8")
    if print_resources:
        (resources / "kanjivg").mkdir()
        with ZipFile(resources / "kanjivg/source.zip", "w") as archive:
            archive.writestr("kanji/06d77.svg", '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 109 109"><path d="M1 2 L3 4"/></svg>')
        (resources / "fonts").mkdir()
        # Tests using this fixture mock the actual PDF renderer.
        (resources / "fonts/NotoSansJP-Regular.ttf").write_bytes(b"font placeholder for mocked PDF exporter")
    return folder, resources


def snapshot(folder):
    return {p.relative_to(folder): p.read_bytes() for p in folder.rglob("*") if p.is_file()}


class ReaderEnrichmentTests(unittest.TestCase):
    def test_api_preserves_sources_all_occurrences_and_schema_without_exporting(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder, resources = fixture(root)
            before = snapshot(folder)
            book = enrich_book(folder, resources)
            self.assertEqual(snapshot(folder), before)
            self.assertFalse((root / "output").exists())
            self.assertNotIn("reportlab", book["build_inputs"]["packages"])
            schema = json.loads((PROJECT / "immersive/reader.schema.json").read_text(encoding="utf-8"))
            jsonschema.validate(book, schema)
            sea = next(v for v in book["vocabulary"] if v["word"] == "海")
            self.assertEqual({o["chapter_id"] for o in sea["occurrences"]}, {"chapter-01", "chapter-02"})
            self.assertEqual(sea["selected_examples"]["chapter-01"]["source"], "csv")
            see = next(v for v in book["vocabulary"] if v["word"] == "見る")
            self.assertEqual(see["selected_examples"]["chapter-01"]["source"], "epub_first_occurrence")
            self.assertTrue(sea["selected_examples"]["chapter-01"]["ruby"])
            self.assertTrue(all("translation" not in s for c in book["chapters"] for s in c["sentences"]))
            original_book = copy.deepcopy(book)
            data = write_reader_data(book, root / "output")
            self.assertEqual(book, original_book)
            self.assertEqual(snapshot(folder), before)
            self.assertEqual(sorted(p.name for p in data.iterdir()), ["lookup.json", "quality.json", "reader.json"])
            self.assertEqual(json.loads((data / "reader.json").read_text(encoding="utf-8")), book)
            lookup = json.loads((data / "lookup.json").read_text(encoding="utf-8"))
            self.assertIn(sea["id"], lookup["forms"]["うみ"])

    def test_reader_commands_work_with_print_imports_metadata_and_network_blocked(self):
        # Fresh process avoids cached PDF imports from unrelated test modules.
        script = '''
import importlib.abc, importlib.metadata, sys
from unittest.mock import patch
class NoPrint(importlib.abc.MetaPathFinder):
    def find_spec(self, fullname, path=None, target=None):
        if fullname.split('.')[0] in {'reportlab', 'fontTools', 'pymupdf', 'fitz'}:
            raise AssertionError('Reader imported print library: ' + fullname)
sys.meta_path.insert(0, NoPrint())
version = importlib.metadata.version
def core_version(name):
    if name.lower() in {'reportlab', 'fonttools', 'pymupdf'}:
        raise AssertionError('Reader required print package metadata: ' + name)
    return version(name)
with patch('socket.socket', side_effect=AssertionError('Network forbidden')), patch('socket.create_connection', side_effect=AssertionError('Network forbidden')), patch('importlib.metadata.version', side_effect=core_version):
    from immersive.pipeline import main
    from immersive.cli import main as cli
    folder, resources, output = sys.argv[1:]
    main(['--reader-only', '--book', folder, '--resources', resources, '--output', output])
    main(['--doctor', '--reader-only', '--resources', resources])
    cli(['enrich', '--book', folder, '--kanjidic', resources + '/kanjidic2.xml.gz', '--output', output + '-cli'])
'''
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder, resources = fixture(root)
            before = snapshot(folder)
            completed = subprocess.run([sys.executable, "-c", script, str(folder), str(resources), str(root / "output")],
                                       cwd=PROJECT, capture_output=True, text=True)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            self.assertEqual(snapshot(folder), before)
            for name in ("output", "output-cli"):
                files = list((root / name / "data").rglob("*"))
                self.assertEqual(sorted(p.name for p in files if p.is_file()), ["lookup.json", "quality.json", "reader.json"])

    def test_deck_update_is_an_independent_explicit_task(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder, resources = fixture(root)
            original_epub = (folder / "sample.epub").read_bytes()
            run_stages(root / "books", root / "output", resources, decks_only=True)
            with (folder / "deck.csv").open(encoding="utf-8", newline="") as stream:
                rows = list(csv.DictReader(stream))
            self.assertTrue(all(row["first_appearance"] for row in rows))
            self.assertEqual((folder / "sample.epub").read_bytes(), original_epub)
            self.assertFalse((root / "output").exists())

    def test_study_build_csv_update_requires_opt_in(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            folder, resources = fixture(root, print_resources=True)
            before = snapshot(folder)
            output = root / "output"
            (output / "html").mkdir(parents=True)
            # Exercise orchestration while keeping expensive exporters separate.
            from immersive.card_export import export
            from immersive.render_outputs import render_book
            with patch("immersive.kanjivg.add_stroke_order"), patch("immersive.kanjivg.copy_svg_assets"), patch("immersive.render_outputs.render_book", wraps=render_book) as renderer, patch("immersive.card_export.export", wraps=export) as card_export:
                run_stages(root / "books", output, resources, verify=False, skip_pdf=True, skip_package=True)
                prepared = renderer.call_args.kwargs["study"]
                self.assertIs(card_export.call_args.kwargs["prepared_by_book"][prepared.book_id], prepared)
                self.assertEqual(snapshot(folder), before)
                run_stages(root / "books", output, resources, verify=False, update_decks=True, skip_pdf=True, skip_package=True)
                self.assertNotEqual((folder / "deck.csv").read_bytes(), before[Path("deck.csv")])

    def test_reader_mode_rejects_conflicting_deck_tasks_before_work(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for options in ({"decks_only": True}, {"update_decks": True}):
                with self.assertRaisesRegex(ValueError, "cannot be combined"):
                    run_stages(root, root / "output", root / "resources", reader_only=True, **options)
            self.assertEqual(list(root.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
