"""Generate one chapter's proposed kanji PDF without changing production outputs."""
import argparse
import json
import sys
import hashlib
from zipfile import ZipFile
from bs4 import BeautifulSoup
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
from immersive.pdf_elements import pdf_session
from immersive.kanji_pdf import write_kanji_document
from immersive.resources import ResourceBundle

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--reader", type=Path, default=next((ROOT/"output/data").glob("book-*/reader.json")))
    parser.add_argument("--chapter", type=int, default=1)
    parser.add_argument("--resources", type=Path, default=ROOT/"data")
    parser.add_argument("--output", type=Path, default=ROOT/"output/pdf/drafts/chapter-01-kanji.pdf")
    args = parser.parse_args()
    book = json.loads(args.reader.read_text(encoding="utf-8"))
    epub_source = next(f for f in book["source_files"] if f["file"].endswith(".epub"))
    source_path = next(p for p in (ROOT/"books").rglob("*.epub") if p.name == epub_source["file"] and hashlib.sha256(p.read_bytes()).hexdigest() == epub_source["sha256"])
    with ZipFile(source_path) as archive:
        container = BeautifulSoup(archive.read("META-INF/container.xml"), "xml")
        package = BeautifulSoup(archive.read(container.find("rootfile")["full-path"]), "xml")
        book["publication"] = {key: package.find(key).get_text() if package.find(key) else "" for key in ("creator", "publisher", "date")}
    chapter = next(c for c in book["chapters"] if c["number"] == args.chapter)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    resources = ResourceBundle.from_directory(args.resources)
    with pdf_session(resources.font):
        result = write_kanji_document(args.output, book, chapter, metadata_dir=ROOT/"output/data"/book["id"]/"print/drafts", charts=resources.charts)
    print(json.dumps({"path": str(args.output), "cards": result["total_entries"], "study_pages": len(result["pages"]), "cover_pages": 1}))
