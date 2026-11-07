"""Publication invariants; stub document bytes test ownership, not PDF layout."""
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest.mock import patch
from zipfile import ZipFile

from immersive.enrichment import write_reader_data, enrich_book
from immersive.jobs import CancellationToken, CancelledError
from immersive.orchestration import BuildOptions, run_job
from immersive.kanjivg import add_stroke_order
from immersive.render_outputs import render_book
from immersive.packaging import package
from immersive.publication import (Publication, PublicationConflict, PublicationError, PublicationLock,
                                   atomic_json, contained, file_info, inventory, read_manifest, repair_views, snapshot_root)
from test_reader_enrichment import fixture

PROJECT = Path(__file__).resolve().parents[1]
ONE, TWO = "book-1111111111111111", "book-2222222222222222"


def reader(publication, bid=ONE, title="First"):
    book = {"id": bid, "title": title, "vocabulary": [], "quality": {}}
    write_reader_data(book, publication.book_root(bid, "reader"))
    publication.add(bid, title, "reader", validated=False)
    return book


def study(publication, book, chapters=(1, 2)):
    bid = book["id"]
    root = publication.book_root(bid, "study")
    write_reader_data(book, root)
    for number in chapters:
        path = root / "pdf" / bid / f"chapter-{number:02d}-kanji.pdf"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(f"publication test payload {number}".encode())
    index = root / "html" / bid / "index.html"
    index.parent.mkdir(parents=True)
    index.write_text("Completed study fixture", encoding="utf-8")
    publication.add(bid, book["title"], "study", validated=False, capabilities={"html": True, "pdf": True})


def wait_for(path, processes, timeout=15):
    deadline = time.monotonic()+timeout
    while not path.exists():
        exited = next((p for p in processes if p.poll() is not None), None)
        if exited is not None:
            stdout, stderr = exited.communicate(timeout=2)
            raise AssertionError(f"Worker exited before barrier: {stdout} {stderr}")
        if time.monotonic() > deadline:
            raise AssertionError("Worker did not reach publication barrier")
        time.sleep(.02)


WORKER = '''
import sys,time
from pathlib import Path
from immersive.publication import Publication, read_manifest
from immersive.enrichment import write_reader_data
output,bid,ready,gate=map(Path,sys.argv[1:])
bid=str(bid)
base=read_manifest(output)
with Publication(output,base=base) as publication:
    write_reader_data({'id':bid,'title':bid,'vocabulary':[],'quality':{}},publication.book_root(bid,'reader'))
    publication.add(bid,bid,'reader',validated=False)
    ready.write_text(publication.generation)
    while not gate.exists(): time.sleep(.02)
    publication.publish()
print('published',flush=True)
'''


class PublicationTests(unittest.TestCase):
    def test_standalone_exporters_report_optional_export_failures(self):
        from immersive.card_export import export
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "output"
            with Publication(output) as publication:
                reader(publication)
                publication.publish()
            with patch("immersive.packaging.write_archive", side_effect=OSError("archive unavailable")):
                with self.assertRaisesRegex(PublicationError, "Archive export failed"):
                    package(output)
            with patch("immersive.card_export.export_book", side_effect=OSError("cards unavailable")):
                with self.assertRaisesRegex(PublicationError, "Card export failed"):
                    export(output, output / "data/flashcards/library.json", announce=False)

    def test_legacy_adoption_keeps_referenced_assets_but_not_stale_or_unknown_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            book = enrich_book(folder, resources)
            archive_path = root / "kanjivg.zip"
            svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 109 109"><path id="s1" d="M1 2 L3 4"/></svg>'
            with ZipFile(archive_path, "w") as archive:
                archive.writestr("kanji/06d77.svg", svg)
            add_stroke_order(book, archive_path)
            output = root / "output"
            render_book(book, output, output / "pdf", skip_pdf=True, kanjivg_archive=archive_path)
            bid = book["id"]
            stale = output / "html" / bid / "chapters/chapter-99-kanji.html"
            stale.write_text("obsolete generated chapter", encoding="utf-8")
            extra = output / "data" / bid / "assets/kanjivg/deadbeef.svg"
            extra.write_text("obsolete generated asset", encoding="utf-8")
            notes = output / "data" / bid / "notes.txt"
            notes.write_text("private notes", encoding="utf-8")
            with Publication(output) as publication:
                reader(publication, TWO)
                manifest, _ = publication.publish(package=True)
            legacy_reader = snapshot_root(output, manifest["books"][bid]["reader"]) / "data" / bid
            self.assertEqual((legacy_reader / "assets/kanjivg/06d77.svg").read_text(encoding="utf-8"), svg)
            self.assertFalse(stale.exists())
            self.assertFalse(extra.exists())
            self.assertEqual(notes.read_text(encoding="utf-8"), "private notes")
            with ZipFile(output / manifest["shared"]["archive"]["path"]) as archive:
                self.assertIn(f"reader-data/{bid}/assets/kanjivg/06d77.svg", archive.namelist())
                self.assertFalse(any("deadbeef" in name or "chapter-99" in name or "notes.txt" in name for name in archive.namelist()))

    def test_pointer_failure_preserves_previous_snapshot_and_views(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with Publication(output) as publication:
                reader(publication)
                publication.publish()
            before = (output / ".publication/active.json").read_bytes()
            visible = (output / "data" / ONE / "reader.json").read_bytes()
            old = read_manifest(output)
            old_root = snapshot_root(output, old["books"][ONE]["reader"])
            old_files = inventory(old_root)
            original = atomic_json
            def fail_pointer(path, value):
                if Path(path).name == "active.json":
                    raise OSError("simulated pointer write failure")
                return original(path, value)
            with self.assertRaisesRegex(OSError, "pointer write"), Publication(output) as publication:
                reader(publication, title="Replacement")
                with patch("immersive.publication.atomic_json", side_effect=fail_pointer):
                    publication.publish()
            self.assertEqual((output / ".publication/active.json").read_bytes(), before)
            self.assertEqual((output / "data" / ONE / "reader.json").read_bytes(), visible)
            self.assertEqual(inventory(old_root), old_files)
            self.assertFalse(list((output / ".publication/staging").iterdir()))

    def test_stale_owned_files_removed_but_unknown_and_edited_files_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with Publication(output) as publication:
                book = reader(publication)
                study(publication, book, (1, 2, 3))
                publication.publish()
            notes = output / "pdf" / ONE / "notes.pdf"
            notes.write_bytes(b"my notes")
            edited = output / "pdf" / ONE / "chapter-03-kanji.pdf"
            edited.write_bytes(b"my annotated copy")
            with Publication(output) as publication:
                book = reader(publication, title="Updated")
                study(publication, book, (1,))
                manifest, warnings = publication.publish(package=True)
            self.assertFalse((output / "pdf" / ONE / "chapter-02-kanji.pdf").exists())
            self.assertEqual(notes.read_bytes(), b"my notes")
            self.assertEqual(edited.read_bytes(), b"my annotated copy")
            self.assertIn("modified_obsolete_file_preserved", {w["code"] for w in warnings})
            with ZipFile(output / manifest["shared"]["archive"]["path"]) as archive:
                names = set(archive.namelist())
                self.assertNotIn(f"pdf/{ONE}/notes.pdf", names)
                self.assertNotIn(f"pdf/{ONE}/chapter-02-kanji.pdf", names)
                self.assertNotIn(f"pdf/{ONE}/chapter-03-kanji.pdf", names)
                exported = json.loads(archive.read("publication-manifest.json"))
                self.assertEqual(names, set(exported["files"]) | {"publication-manifest.json"})
                self.assertFalse(any(n.startswith(".publication/") for n in names))

    def test_checksum_failure_prevents_packaging_and_pointer_change(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with Publication(output) as publication:
                reader(publication)
                manifest, _ = publication.publish(package=True)
            pointer = (output / ".publication/active.json").read_bytes()
            archive = output / manifest["shared"]["archive"]["path"]
            archive_bytes = archive.read_bytes()
            source = snapshot_root(output, manifest["books"][ONE]["reader"]) / "data" / ONE / "reader.json"
            source.write_text("tampered", encoding="utf-8")
            with self.assertRaisesRegex(PublicationError, "artifact changed"):
                package(output)
            self.assertEqual((output / ".publication/active.json").read_bytes(), pointer)
            self.assertEqual(archive.read_bytes(), archive_bytes)

    def test_concurrent_same_book_job_is_rejected_without_lost_update(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with Publication(output) as first, Publication(output) as second:
                reader(first, title="First writer")
                reader(second, title="Second writer")
                first.publish()
                with self.assertRaises(PublicationConflict):
                    second.publish()
            self.assertEqual(read_manifest(output)["books"][ONE]["title"], "First writer")

    def test_two_processes_publish_different_books_without_losing_either(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output, gate = root / "output", root / "go"
            workers = []
            try:
                for i, bid in enumerate((ONE, TWO)):
                    workers.append(subprocess.Popen([sys.executable, "-c", WORKER, str(output), bid, str(root / f"ready{i}"), str(gate)],
                                                    cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True))
                for i in range(2):
                    wait_for(root / f"ready{i}", workers)
                gate.touch()
                for worker in workers:
                    stdout, stderr = worker.communicate(timeout=15)
                    self.assertEqual(worker.returncode, 0, stdout+stderr)
                self.assertEqual(set(read_manifest(output)["books"]), {ONE, TWO})
            finally:
                for worker in workers:
                    if worker.poll() is None:
                        worker.kill()
                    worker.communicate(timeout=10)

    def test_dead_worker_staging_is_recovered_and_active_worker_is_not_deleted(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output, ready, gate = root / "output", root / "ready", root / "go"
            worker = subprocess.Popen([sys.executable, "-c", WORKER, str(output), ONE, str(ready), str(gate)],
                                      cwd=PROJECT, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                wait_for(ready, [worker])
                generation = ready.read_text()
                orphan = output / ".publication/staging" / generation
                with Publication(output):
                    self.assertTrue(orphan.is_dir(), "Active leased staging directory was removed")
                worker.kill()
                worker.communicate(timeout=10)
                with Publication(output) as publication:
                    reader(publication, TWO)
                    _, warnings = publication.publish()
                self.assertFalse(orphan.exists())
                self.assertIn("abandoned_staging_removed", {w["code"] for w in warnings})
            finally:
                if worker.poll() is None:
                    worker.kill()
                    worker.communicate(timeout=10)

    def test_compatibility_view_failure_is_repairable_after_atomic_commit(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with Publication(output) as publication:
                reader(publication)
                with patch("immersive.publication.atomic_copy", side_effect=OSError("view unavailable")):
                    manifest, warnings = publication.publish()
            self.assertIn("view_refresh_pending", {w["code"] for w in warnings})
            self.assertEqual(read_manifest(output)["generation"], manifest["generation"])
            self.assertEqual(repair_views(output), [])
            self.assertTrue((output / "data" / ONE / "reader.json").is_file())

    def test_path_traversal_and_linked_output_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for name in ("../outside", "C:/outside", "data/../../outside", "data\\outside"):
                with self.subTest(name=name), self.assertRaises(PublicationError):
                    contained(root, name)
            if os.name == "nt":
                extended = Path("\\\\?\\"+str(root.resolve()))
                self.assertEqual(contained(extended, "data/file.json"), contained(root, "data/file.json"))
            outside = root / "outside"
            outside.mkdir()
            link = root / "link"
            try:
                link.symlink_to(outside, target_is_directory=True)
            except OSError:
                if os.name == "nt":
                    import _winapi
                    _winapi.CreateJunction(str(outside), str(link))
                else:
                    raise
            with self.assertRaises(PublicationError):
                contained(root, "link/file.json")

    def test_error_after_pointer_switch_never_deletes_the_selected_release(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            original = atomic_json
            def fail_after_switch(path, value):
                original(path, value)
                if Path(path).name == "active.json":
                    raise OSError("interrupted after switch")
            with self.assertRaises(OSError), Publication(output) as publication:
                reader(publication)
                with patch("immersive.publication.atomic_json", side_effect=fail_after_switch):
                    publication.publish()
            manifest = read_manifest(output)
            root = snapshot_root(output, manifest["books"][ONE]["reader"])
            self.assertTrue((root / "data" / ONE / "reader.json").is_file())
            self.assertEqual(repair_views(output), [])

    def test_corrupted_cleanup_manifest_cannot_delete_outside_output(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output = root / "output"
            outside = root / "source.csv"
            outside.write_bytes(b"keep source input")
            with Publication(output) as publication:
                reader(publication)
                manifest, _ = publication.publish()
            manifest["retired"]["../source.csv"] = [file_info(outside)["sha256"]]
            pointer_path = output / ".publication/active.json"
            pointer = json.loads(pointer_path.read_text(encoding="utf-8"))
            manifest_path = output / pointer["manifest"]
            atomic_json(manifest_path, manifest)
            pointer["sha256"] = file_info(manifest_path)["sha256"]
            atomic_json(pointer_path, pointer)
            with self.assertRaises(PublicationError):
                repair_views(output)
            self.assertEqual(outside.read_bytes(), b"keep source input")

    def test_failed_archive_retains_last_archive_and_publishes_reader(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with Publication(output) as publication:
                reader(publication)
                before, _ = publication.publish(package=True)
            archive = (output / "study-pack.zip").read_bytes()
            with Publication(output) as publication:
                reader(publication, title="New reader")
                with patch("immersive.packaging.write_archive", side_effect=OSError("disk full")):
                    after, _ = publication.publish(package=True)
            self.assertEqual(after["status"], "partial")
            self.assertEqual(after["shared"]["archive"], before["shared"]["archive"])
            self.assertEqual((output / "study-pack.zip").read_bytes(), archive)
            self.assertEqual(after["books"][ONE]["title"], "New reader")

    def test_cancelled_packaging_does_not_publish_a_partial_reader(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp)
            with Publication(output) as publication:
                reader(publication)
                publication.publish()
            pointer = (output / ".publication/active.json").read_bytes()
            token = CancellationToken()
            with self.assertRaises(CancelledError), Publication(output, cancel=token) as publication:
                reader(publication, title="Cancelled")
                with patch("immersive.packaging.write_archive", side_effect=CancelledError("cancelled compression")):
                    publication.publish(package=True)
            self.assertEqual((output / ".publication/active.json").read_bytes(), pointer)

    def test_lock_wait_can_be_cancelled_and_killed_owner_does_not_leave_stale_lock(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            output, ready = root / "output", root / "ready"
            script = '''
import sys,time
from pathlib import Path
from immersive.publication import PublicationLock
with PublicationLock(Path(sys.argv[1])):
    Path(sys.argv[2]).touch()
    time.sleep(30)
'''
            worker = subprocess.Popen([sys.executable, "-c", script, str(output), str(ready)], cwd=PROJECT)
            try:
                wait_for(ready, [worker])
                token = CancellationToken()
                timer = threading.Timer(.1, token.cancel)
                timer.start()
                try:
                    with self.assertRaises(CancelledError), PublicationLock(output, cancel=token, timeout=3):
                        self.fail("Acquired another process's lock")
                finally:
                    timer.cancel()
                worker.kill()
                worker.wait(timeout=10)
                with PublicationLock(output, timeout=2):
                    pass
            finally:
                if worker.poll() is None:
                    worker.kill()
                    worker.wait(timeout=10)


class StagedJobTests(unittest.TestCase):
    def test_cancellation_preserves_published_data_and_removes_private_staging(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            output = root / "output"
            original = run_job(root / "books", output, resources)
            pointer = (output / ".publication/active.json").read_bytes()
            token = CancellationToken()
            with self.assertRaises(CancelledError):
                run_job(root / "books", output, resources, cancel=token,
                        on_event=lambda e: token.cancel() if e["stage"] == "reader_complete" else None)
            self.assertEqual((output / ".publication/active.json").read_bytes(), pointer)
            self.assertTrue((original.books[0].data_directory / "reader.json").is_file())
            self.assertFalse(list((output / ".publication/staging").iterdir()))

    def test_document_failure_publishes_reader_but_retains_last_study(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root, print_resources=True)
            output = root / "output"
            with patch("immersive.kanjivg.add_stroke_order"), patch("immersive.kanjivg.copy_svg_assets"):
                old = run_job(root / "books", output, resources, options=BuildOptions(mode="study", html=True, package=True))
            old_study = inventory(old.books[0].study_directory)
            old_archive = (output / "study-pack.zip").read_bytes()
            path = folder / "deck.csv"
            path.write_text(path.read_text(encoding="utf-8").replace("sea", "ocean"), encoding="utf-8")
            with patch("immersive.kanjivg.add_stroke_order"), patch("immersive.render_outputs.render_book", side_effect=OSError("PDF rendering failed")):
                result = run_job(root / "books", output, resources, options=BuildOptions(mode="study", pdf=True, package=True))
            self.assertEqual(result.status, "partial")
            self.assertEqual(result.books[0].export_status, "failed")
            self.assertEqual(result.books[0].study_directory, old.books[0].study_directory)
            self.assertEqual(inventory(old.books[0].study_directory), old_study)
            self.assertIn("ocean", (result.books[0].data_directory / "reader.json").read_text(encoding="utf-8"))
            self.assertNotEqual(result.books[0].reader_generation, old.books[0].reader_generation)
            self.assertIsNone(result.archive)
            self.assertEqual((output / "study-pack.zip").read_bytes(), old_archive)

    def test_module_command_exits_nonzero_for_partial_export_with_reader_available(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            output = root / "output"
            result = subprocess.run([sys.executable, "-m", "immersive.cli", "build", "--book", str(folder),
                                     "--resources", str(resources), "--output", str(output)],
                                    cwd=PROJECT, capture_output=True, text=True, encoding="utf-8")
            self.assertEqual(result.returncode, 1, result.stdout+result.stderr)
            self.assertNotIn("Traceback", result.stderr)
            summary = json.loads(result.stdout)
            self.assertEqual(summary["export_status"], "failed")
            self.assertTrue((Path(summary["output"]) / "reader.json").is_file())
            self.assertEqual(read_manifest(output)["status"], "partial")

    def test_input_change_during_staging_prevents_publication(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            folder, resources = fixture(root)
            output = root / "output"
            run_job(root / "books", output, resources)
            pointer = (output / ".publication/active.json").read_bytes()
            def edit(event):
                if event["stage"] == "reader_complete":
                    path = folder / "deck.csv"
                    path.write_text(path.read_text(encoding="utf-8").replace("sea", "ocean"), encoding="utf-8")
            with self.assertRaisesRegex(PublicationConflict, "Input changed"):
                run_job(root / "books", output, resources, on_event=edit)
            self.assertEqual((output / ".publication/active.json").read_bytes(), pointer)


if __name__ == "__main__":
    unittest.main()
