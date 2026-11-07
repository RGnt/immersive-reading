"""Authored format-3 dictionaries; offline and independent of supplied books."""
import copy
import contextlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile

from immersive.enrichment import enrich_book
from immersive.jobs import CancellationToken, CancelledError
from immersive.orchestration import run_job, BuildOptions
from immersive.publication import PublicationConflict, read_manifest
from immersive.validation import validate_reader, ValidationError
from immersive.verification import verify
from immersive.yomitan import load_dictionary, dictionary_paths, glossary_text
from test_reader_enrichment import fixture, snapshot, PROJECT


def term(word, reading, *glosses):
    return [word, reading, "n", "", 0, list(glosses), 1, "common"]


def dictionary_zip(path, *, terms=None, index=None, extra=None):
    path.parent.mkdir(parents=True, exist_ok=True)
    banks = {
        "index.json": index if index is not None else {"title": "Authored dictionary", "revision": "test-1", "format": 3,
                                                      "targetLanguage": "en", "attribution": "Authored test content; CC0"},
        "term_bank_1.json": terms if terms is not None else [term("海", "うみ", "ocean"), term("見る", "みる", "to see"), term("行く", "いく", "to go")],
        "kanji_bank_1.json": [["海", "カイ", "うみ", "", ["ocean"], {"strokes": "9"}]],
        "term_meta_bank_1.json": [["海", "freq", {"reading": "うみ", "frequency": {"value": 42, "displayValue": "42 rank"}}],
                                  ["海", "pitch", {"reading": "うみ", "pitches": [{"position": 1}]}],
                                  ["海", "ipa", {"reading": "うみ", "transcriptions": [{"ipa": "ɯmi"}]}]],
        "kanji_meta_bank_1.json": [["海", "freq", 9]],
        "tag_bank_1.json": [["n", "partOfSpeech", 0, "noun", 0]],
        **(extra or {}),
    }
    with ZipFile(path, "w") as archive:
        for name, value in banks.items():
            archive.writestr(name, json.dumps(value, ensure_ascii=False))
        archive.writestr("LICENSE.txt", "Authored fixture. CC0.")
    return path


class YomitanTests(unittest.TestCase):
    def test_import_keeps_banks_structured_content_metadata_and_notices(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            glossary = {"type": "structured-content", "content": {"tag": "div", "content": [
                "open ", {"tag": "span", "content": "ocean"}, {"tag": "br"},
                {"tag": "a", "href": "https://example.test/", "content": "sea"},
                {"tag": "img", "path": "assets/a.png", "alt": "diagram"}]}}
            path = dictionary_zip(root / "dict.zip", terms=[term("海", "うみ", "ocean", {"type": "text", "text": "sea"}, glossary)])
            with patch("socket.socket", side_effect=AssertionError("Network forbidden")):
                imported = load_dictionary(path)
            self.assertEqual(imported["terms"][0]["glossary"][-1], glossary)
            self.assertEqual(imported["terms"][0]["glossary_text"][-1], "open ocean\nseadiagram")
            self.assertEqual(imported["source"]["tags"]["n"]["notes"], "noun")
            self.assertEqual(len(imported["term_meta"]), 3)
            self.assertEqual(imported["kanji_meta"][0]["data"], 9)
            self.assertEqual(imported["source"]["notices"]["LICENSE.txt"], "Authored fixture. CC0.")
            self.assertEqual(sorted(p.name for p in root.iterdir()), ["dict.zip"])

    def test_csv_defaults_examples_and_reading_filter_are_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            path = dictionary_zip(root / "dict.zip", terms=[term("海", "ウミ", "ocean"), term("海", "かい", "wrong reading")],
                                  extra={"term_meta_bank_2.json": [["海", "freq", {"reading": "かい", "frequency": 1}]]})
            before = snapshot(folder)
            book = enrich_book(folder, resources, yomitan_paths=[path])
            sea = next(v for v in book["vocabulary"] if v["word"] == "海")
            self.assertEqual(sea["definitions"], "sea")
            self.assertEqual([v["glossary_text"] for v in sea["dictionary_entries"]], [["ocean"]])
            self.assertEqual(len(sea["dictionary_metadata"]), 3)
            self.assertEqual(sea["selected_examples"]["chapter-01"]["source"], "csv")
            self.assertEqual(snapshot(folder), before)
            self.assertEqual(book["kanji"][0]["dictionary"]["primary_meanings"], ["sea"])

    def test_dictionary_only_book_matches_inflections_and_uses_book_examples(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root, text='<html><body><h1 id="a">第一章</h1><p>人気がある。海を見た。気になった。</p><h1 id="b">第二章</h1><p>海を見た。</p></body></html>')
            (folder / "deck.csv").unlink()
            path = dictionary_zip(resources / "yomitan/dict.zip", terms=[term("海", "うみ", "ocean"), term("見る", "みる", "see"),
                term("気になる", "きになる", "be on one's mind"), term("人気", "にんき", "popularity"),
                term("人気", "ひとけ", "human presence"), term("絶対に無い言葉", "ぜったいにないことば", "absent")])
            before = snapshot(folder)
            book = enrich_book(folder, resources)
            words = {v["word"]: v for v in book["vocabulary"]}
            self.assertEqual(set(words), {"海", "見る", "気になる", "人気"})
            self.assertEqual(words["人気"]["reading"], "にんき")
            self.assertEqual(len(words["海"]["occurrences"]), 2)
            # Sudachi separates the past auxiliary た from the verb stem.
            self.assertEqual(words["見る"]["occurrences"][0]["surface"], "見")
            for entry in words.values():
                self.assertTrue(entry["sources"][0]["dictionary_id"])
                self.assertFalse(entry["deck_examples"])
                self.assertTrue(entry["definitions"])
                for example in entry["selected_examples"].values():
                    self.assertEqual(example["source"], "epub_first_occurrence")
                    self.assertTrue(example["ruby"])
            self.assertEqual(snapshot(folder), before)
            self.assertEqual(book["gloss_language"], "en")
            self.assertEqual(len(book["source_files"]), 1)
            self.assertEqual(book["quality"]["vocabulary_total"], 4)
            self.assertEqual(dictionary_paths(resources), [path.resolve()])

    def test_priority_combines_first_dictionary_senses_and_retains_other_candidates(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            (folder / "deck.csv").unlink()
            a = dictionary_zip(root / "a.zip", terms=[term("海", "うみ", "ocean"), term("海", "うみ", "sea")])
            b = dictionary_zip(root / "b.zip", terms=[term("海", "うみ", "large water")],
                               index={"title": "Second", "revision": "1", "version": 3, "targetLanguage": "ja"})
            book = enrich_book(folder, resources, yomitan_paths=[a, b, a])
            sea = book["vocabulary"][0]
            self.assertEqual(sea["definitions"], "ocean\nsea")
            self.assertEqual(len(sea["dictionary_entries"]), 3)
            self.assertEqual(len(book["dictionary_sources"]), 2)
            self.assertEqual(book["gloss_language"], "mul")
            reordered = enrich_book(folder, resources, yomitan_paths=[b, a])
            self.assertEqual(reordered["vocabulary"][0]["id"], sea["id"])
            self.assertEqual(reordered["vocabulary"][0]["definitions"], "large water")

    def test_dictionary_only_study_html_cards_zip_and_staged_validation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            (folder / "deck.csv").unlink()
            path = dictionary_zip(root / "dict.zip", terms=[term("海", "うみ", '<script>alert("no")</script> ocean'),term("見る", "みる", "see")])
            archive_path = resources / "kanjivg/source.zip"
            archive_path.parent.mkdir()
            with ZipFile(archive_path, "w") as archive:
                archive.writestr("kanji/06d77.svg", '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 109 109"><path id="s1" d="M1 2 L3 4"/></svg>')
            result = run_job(root / "books", root / "output", resources, options=BuildOptions(mode="study", html=True, cards=True, package=True), yomitan_paths=[path])
            self.assertEqual(result.status, "complete", result.errors)
            self.assertEqual(verify(root / "output", html=True, pdf=False)[0]["vocabulary"], 2)
            html = (result.books[0].html_directory / "vocabulary.html").read_text(encoding="utf-8")
            self.assertNotIn('<script>alert("no")', html)
            self.assertIn("&lt;script&gt;", html)
            self.assertIn("Dictionary candidates", html)
            with ZipFile(result.archive) as archive:
                self.assertTrue(any(n.endswith("dictionary-sources.html") for n in archive.namelist()))
                library = json.loads(archive.read("data/flashcards/library.json"))
                self.assertTrue(library["books"][0]["dictionary_sources"])
                cards = [c for ch in library["books"][0]["chapters"] for c in ch["cards"] if c["kind"] == "vocabulary"]
                self.assertTrue(cards[0]["dictionary_entries"])

    def test_empty_selection_disables_discovery_and_metadata_only_needs_deck(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            path = dictionary_zip(resources / "yomitan/meta.zip", terms=[])
            self.assertNotIn("dictionary_sources", enrich_book(folder, resources, yomitan_paths=[]))
            book = enrich_book(folder, resources)
            self.assertTrue(book["vocabulary"][0]["dictionary_metadata"])
            (folder / "deck.csv").unlink()
            # Metadata-only archives can supplement a CSV, but cannot supply
            # a vocabulary list on their own.
            with ZipFile(path) as src:
                contents = {n: src.read(n) for n in src.namelist() if not n.startswith("term_bank_")}
            with ZipFile(path, "w") as target:
                for n, content in contents.items(): target.writestr(n, content)
            with self.assertRaisesRegex(ValueError, "term dictionary"):
                enrich_book(folder, resources)

    def test_malformed_archives_and_banks_have_actionable_errors(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            cases = [({"index.json": {"title": "Old", "revision": "1", "format": 2}}, "unsupported Yomitan format"),
                     ({"term_bank_1.json": [["海", "うみ"]]}, "term record"),
                     ({"term_meta_bank_1.json": [["海", "pitch", {"reading": "うみ", "pitches": [{"position": -1}]}]]}, "pitch position"),
                     ({"../escaped.json": []}, "unsafe archive"),
                     ({"term_bank_1.json": [term("海", "うみ", {"type": "unknown"})]}, "glossary type")]
            for number, (extra, message) in enumerate(cases):
                with self.subTest(message=message):
                    path = dictionary_zip(root / f"bad{number}.zip", extra=extra)
                    with self.assertRaisesRegex(ValueError, message): load_dictionary(path)
            path = dictionary_zip(root / "oversize.zip")
            with patch("immersive.yomitan.MAX_JSON_BYTES", 10):
                with self.assertRaisesRegex(ValueError, "128 MiB"): load_dictionary(path)
            with ZipFile(root / "missing.zip", "w") as archive: archive.writestr("x", "x")
            with self.assertRaisesRegex(ValueError, "index.json"): load_dictionary(root / "missing.zip")

    def test_pipeline_preset_and_doctor_accept_explicit_dictionary_paths(self):
        from immersive.pipeline import main
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            (folder / "deck.csv").unlink()
            path = dictionary_zip(root / "dict.zip")
            with contextlib.redirect_stdout(io.StringIO()) as buffer:
                self.assertEqual(main(["--doctor", "--reader-only", "--resources", str(resources), "--yomitan", str(path)]), 0)
            self.assertEqual(json.loads(buffer.getvalue())["yomitan_dictionaries"][0]["title"], "Authored dictionary")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(main(["--reader-only", "--book", str(folder), "--resources", str(resources), "--output", str(root / "output"), "--yomitan", str(path)]), 0)
            self.assertIsNotNone(read_manifest(root / "output"))

    def test_duplicate_dictionary_copies_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            path = dictionary_zip(root / "dict.zip")
            duplicate = root / "copy.zip"
            duplicate.write_bytes(path.read_bytes())
            with self.assertRaisesRegex(ValueError, "Duplicate Yomitan archive contents"):
                enrich_book(folder, resources, yomitan_paths=[path, duplicate])

    def test_cancelled_import_and_changed_dictionary_do_not_publish(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            path = dictionary_zip(root / "dict.zip")
            token = CancellationToken(); token.cancel()
            with self.assertRaises(CancelledError): load_dictionary(path, cancel=token)
            def change(event):
                if event["stage"] == "reader_complete": path.write_bytes(b"changed")
            with self.assertRaisesRegex(PublicationConflict, "Input changed"):
                run_job(root / "books", root / "output", resources, yomitan_paths=[path], on_event=change)
            self.assertIsNone(read_manifest(root / "output"))

    def test_deck_update_requires_csv_and_dictionary_references_are_validated(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            path = dictionary_zip(root / "dict.zip")
            book = enrich_book(folder, resources, yomitan_paths=[path])
            changed = copy.deepcopy(book)
            changed["vocabulary"][0]["dictionary_entries"][0]["dictionary_id"] = "unknown"
            with self.assertRaisesRegex(ValidationError, "unknown dictionary"): validate_reader(changed)
            (folder / "deck.csv").unlink()
            with self.assertRaisesRegex(ValueError, "deck updates require"):
                run_job(root / "books", root / "output", resources, options=BuildOptions(mode="decks"), yomitan_paths=[path])
            self.assertIsNone(read_manifest(root / "output"))

    def test_reader_cli_needs_no_print_libraries_or_network(self):
        script = '''
import importlib.abc,sys
from unittest.mock import patch
class NoPrint(importlib.abc.MetaPathFinder):
 def find_spec(self, fullname, path=None, target=None):
  if fullname.split('.')[0] in {'reportlab','fontTools','pymupdf','fitz'}: raise AssertionError(fullname)
sys.meta_path.insert(0,NoPrint())
with patch('socket.socket',side_effect=AssertionError('Network forbidden')):
 from immersive.cli import main
 raise SystemExit(main(['enrich','--book',sys.argv[1],'--resources',sys.argv[2],'--output',sys.argv[3],'--yomitan',sys.argv[4]]))
'''
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, resources = fixture(root)
            (folder / "deck.csv").unlink()
            path = dictionary_zip(root / "dict.zip")
            completed = subprocess.run([sys.executable, "-c", script, str(folder), str(resources), str(root / "output"), str(path)], cwd=PROJECT, text=True, capture_output=True)
            self.assertEqual(completed.returncode, 0, completed.stdout+completed.stderr)
            self.assertEqual(json.loads(completed.stdout)["matched"], 3)


if __name__ == "__main__":
    unittest.main()
