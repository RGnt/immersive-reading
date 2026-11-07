"""Resource reuse, worker isolation, explicit paths and capability boundaries."""
from concurrent.futures import ThreadPoolExecutor
import contextlib
from dataclasses import replace
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from threading import Barrier
import unittest
from unittest.mock import patch
from zipfile import ZipFile

from immersive.enrichment import enrich_book
from immersive.furigana import furigana
from immersive.kanjivg import add_stroke_order, copy_svg_assets
from immersive.orchestration import run_job, BuildOptions
from immersive.publication import read_manifest
from immersive.resources import ResourceBundle, ResourceStore, ResourceChangedError
from immersive.tokenization import default_worker, close_default_worker
from test_reader_enrichment import fixture, PROJECT


class ResourceTests(unittest.TestCase):
    def test_two_worker_threads_share_dictionary_data_without_sharing_tokenizer_state(self):
        from sudachipy.dictionary import Dictionary
        from immersive.build import load_kanjidic
        from immersive.kanji_meanings import load_primary_meanings
        with tempfile.TemporaryDirectory() as temp:
            folder, paths = fixture(Path(temp))
            bundle = ResourceBundle.from_directory(paths)
            barrier = Barrier(2)
            with ResourceStore(bundle) as store, patch("sudachipy.dictionary.Dictionary", wraps=Dictionary) as native, patch(
                    "immersive.build.load_kanjidic", wraps=load_kanjidic) as kanji, patch(
                    "immersive.kanji_meanings.load_primary_meanings", wraps=load_primary_meanings) as meanings:
                def build(_):
                    with store.worker() as worker:
                        barrier.wait(timeout=10)
                        book = enrich_book(folder, store, worker=worker)
                        readings = [furigana("海で食べる。", worker=worker) for _ in range(20)]
                        return book, worker._tokenizers["C"], readings
                with ThreadPoolExecutor(max_workers=2) as pool:
                    first, second = list(pool.map(build, range(2)))
                self.assertEqual(first[0], second[0])
                self.assertEqual(first[2], second[2])
                self.assertIsNot(first[1], second[1])
                self.assertEqual(native.call_count, 1)
                self.assertEqual(kanji.call_count, 1)
                self.assertEqual(meanings.call_count, 1)

    def test_shared_data_is_immutable_and_book_mutations_do_not_leak(self):
        with tempfile.TemporaryDirectory() as temp:
            folder, paths = fixture(Path(temp))
            with ResourceStore(ResourceBundle.from_directory(paths)) as store:
                data = store.kanjidic()
                self.assertIs(data, store.kanjidic())
                with self.assertRaises(TypeError): data["海"]["source"] = "changed"
                with self.assertRaises(AttributeError): data["海"]["meanings"].append("changed")
                first = enrich_book(folder, store)
                first["kanji"][0]["dictionary"]["meanings"].append("changed")
                first["kanji"][0]["dictionary"]["primary_meanings"].append("changed")
                second = enrich_book(folder, store)
                self.assertEqual(second["kanji"][0]["dictionary"]["meanings"], ["sea"])
                self.assertEqual(second["kanji"][0]["dictionary"]["primary_meanings"], ["sea"])

    def test_worker_ownership_and_store_lifetime_are_enforced(self):
        with ResourceStore(ResourceBundle()) as store:
            with store.worker() as worker:
                with self.assertRaisesRegex(RuntimeError, "Close worker contexts"): store.close()
                with ThreadPoolExecutor(max_workers=1) as pool:
                    with self.assertRaisesRegex(RuntimeError, "another worker"):
                        pool.submit(worker.tokenize, "海").result()
                with patch("immersive.resources.os.getpid", return_value=store._pid+1):
                    with self.assertRaisesRegex(RuntimeError, "process boundaries"): store.worker()
            with self.assertRaisesRegex(RuntimeError, "closed"): worker.tokenize("海")
        with self.assertRaisesRegex(RuntimeError, "closed"): store.worker()

    def test_explicit_bundle_survives_working_directory_change_and_independent_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, paths = fixture(root)
            grammar = root / "custom rules.json"
            grammar.write_text("[]", encoding="utf-8")
            bundle = ResourceBundle(kanjidic=paths / "kanjidic2.xml.gz", kanji_alive=paths / "kanji-alive", grammar=grammar)
            cwd = Path.cwd()
            other = root / "unrelated directory"; other.mkdir()
            try:
                os.chdir(other)
                book = enrich_book(folder, bundle)
            finally:
                os.chdir(cwd)
            self.assertFalse(book["grammar"])
            self.assertFalse(bundle.charts)
            self.assertEqual(book["build_inputs"]["resources"]["grammar"]["file"], "custom rules.json")

    def test_resource_change_rejects_publication_and_preserves_previous_generation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root)
            grammar = root / "grammar.json"
            grammar.write_text("[]", encoding="utf-8")
            bundle = replace(ResourceBundle.from_directory(paths), grammar=grammar)
            output = root / "output"
            first = run_job(root / "books", output, bundle)
            def change(event):
                if event["stage"] == "reader_complete": grammar.write_text("[ ]", encoding="utf-8")
            with self.assertRaises(ResourceChangedError):
                run_job(root / "books", output, bundle, on_event=change)
            self.assertEqual(read_manifest(output)["generation"], first.generation)

    def test_pinned_resource_replacement_requires_new_store(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); _, paths = fixture(root)
            grammar = root / "grammar.json"; grammar.write_text("[]", encoding="utf-8")
            bundle = replace(ResourceBundle.from_directory(paths), grammar=grammar)
            with ResourceStore(bundle) as store:
                self.assertFalse(store.grammar())
                grammar.write_text("[ ]", encoding="utf-8")
                with self.assertRaises(ResourceChangedError): store.grammar()
            with ResourceStore(bundle) as store:
                self.assertFalse(store.grammar())

    def test_kanjivg_glyphs_are_shared_but_export_records_and_assets_are_isolated(self):
        from immersive.kanjivg import parse_glyph
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root, print_resources=True)
            with ResourceStore(ResourceBundle.from_directory(paths)) as store, patch("immersive.kanjivg.parse_glyph", wraps=parse_glyph) as parser:
                one, two = enrich_book(folder, store), enrich_book(folder, store)
                add_stroke_order(one, store.bundle.kanjivg, store=store)
                one["kanji"][0]["stroke_order"]["strokes"][0]["d"] = "changed"
                add_stroke_order(two, store.bundle.kanjivg, store=store)
                self.assertEqual(parser.call_count, 1)
                self.assertEqual(two["kanji"][0]["stroke_order"]["strokes"][0]["d"], "M1 2 L3 4")
                copy_svg_assets(two, store.bundle.kanjivg, root / "assets", store=store)
                with ZipFile(store.bundle.kanjivg) as archive:
                    self.assertEqual((root / "assets/assets/kanjivg/06d77.svg").read_bytes(), archive.read("kanji/06d77.svg"))

    def test_default_ruby_workers_are_thread_local_and_can_be_released(self):
        parent = default_worker()
        with ThreadPoolExecutor(max_workers=1) as pool:
            def child():
                active = default_worker()
                result = active is parent, furigana("海")
                close_default_worker()
                return result
            same, ruby = pool.submit(child).result()
        self.assertFalse(same)
        self.assertEqual(ruby[0]["reading"], "うみ")
        close_default_worker()

    def test_html_doctor_and_cards_need_no_pdf_dependencies_or_font(self):
        script = '''
import importlib.abc,sys
from pathlib import Path
class NoPrint(importlib.abc.MetaPathFinder):
 def find_spec(self,fullname,path=None,target=None):
  if fullname.split('.')[0] in {'reportlab','pymupdf','fitz','fontTools'}: raise AssertionError(fullname)
sys.meta_path.insert(0,NoPrint())
from immersive.doctor import check
from immersive.resources import ResourceBundle,ResourceStore
from immersive.orchestration import run_job,BuildOptions
folder,resources,output=map(Path,sys.argv[1:])
with ResourceStore(ResourceBundle.from_directory(resources)) as store:
 check(resources=store,capabilities={'html'})
 r=run_job(folder.parent,output,store,book_folder=folder,options=BuildOptions(mode='study',html=True,cards=True))
 assert r.status=='complete',r.errors
'''
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); folder, paths = fixture(root, print_resources=True)
            (paths / "fonts/NotoSansJP-Regular.ttf").unlink()
            completed = subprocess.run([sys.executable, "-c", script, str(folder), str(paths), str(root / "output")], cwd=PROJECT, text=True, capture_output=True)
            self.assertEqual(completed.returncode, 0, completed.stdout+completed.stderr)

    def test_missing_custom_morphology_and_invalid_grammar_report_resource_paths(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp); _, paths = fixture(root)
            bundle = replace(ResourceBundle.from_directory(paths), sudachi_dictionary=root / "missing.dic")
            with ResourceStore(bundle) as store, store.worker() as worker:
                with self.assertRaises(FileNotFoundError): worker.tokenize("海")
            grammar = root / "invalid rules.json"; grammar.write_text('[{"id":"bad"}]', encoding="utf-8")
            with ResourceStore(replace(bundle, grammar=grammar)) as store:
                with self.assertRaisesRegex(ValueError, "invalid rules.json"): store.grammar()

    def test_study_exports_reuse_saved_readings_including_unaligned_csv_examples(self):
        from immersive.study_data import prepare_study_data
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, paths = fixture(root, rows=[["海", "うみ", "sea", "海だ。"]])
            book = enrich_book(folder, paths)
            saved = book["vocabulary"][0]["selected_examples"]["chapter-01"]["ruby"]
            with patch("immersive.tokenization.WorkerContext.tokenize", side_effect=AssertionError("Reanalyzed saved readings")):
                study = prepare_study_data(book)
            self.assertEqual(study.vocabulary["chapter-01"][0]["ruby"], saved)


if __name__ == "__main__":
    unittest.main()
