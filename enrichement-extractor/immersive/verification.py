"""Capability-specific validation of persisted reader, HTML and PDF outputs."""
import argparse
import json
import uuid
from pathlib import Path
from urllib.parse import unquote, urlsplit

from .validation import load_json, require, validate_reader
from .jobs import check_cancelled


def validate_study(book):
    sets = book.get("learning_sets")
    require(isinstance(sets, list), "Missing learning sets")
    words = [w for group in sets for w in group["words"]]
    spellings = [w["word"] for w in words]
    expected = {v["word"] for v in book["vocabulary"] if v["occurrences"]}
    require(len(spellings) == len(set(spellings)), "Repeated vocabulary across learning sets")
    require(set(spellings) == expected, "Learning-set words disagree with matched vocabulary")
    require(all(g["size"] == len(g["words"]) == 20 for g in sets[:-1]), "Incomplete intermediate learning set")
    require(not sets or 1 <= sets[-1]["size"] == len(sets[-1]["words"]) <= 20, "Invalid final learning set")
    require([w["study_number"] for w in words] == list(range(1, len(words)+1)), "Study numbers are not consecutive")
    chapters = {c["id"]: c for c in book["chapters"]}
    for word in words:
        require(word["chapter_id"] in chapters, "Unknown learning-set chapter")
        require(any(v["id"] == word["card_id"] and v["word"] == word["word"] for v in book["vocabulary"]), "Unknown learning-set card")


def verify_html(book, output, *, cancel=None):
    from bs4 import BeautifulSoup
    from .furigana import KANJI
    html = output / "html" / book["id"]
    require((html / "index.html").is_file(), "Missing book HTML index", html)
    counts = {}
    for kind, field in (("vocabulary", "word"), ("kanji", "character")):
        owner = {}
        order = {c["id"]: i for i, c in enumerate(book["chapters"])}
        for item in book[kind]:
            for occurrence in item["occurrences"]:
                value, cid = item[field], occurrence["chapter_id"]
                if value not in owner or order[cid] < order[owner[value]]:
                    owner[value] = cid
        seen = set()
        for chapter in book["chapters"]:
            path = html / "chapters" / f'{chapter["id"]}-{kind}.html'
            require(path.is_file(), "Missing chapter HTML", path)
            soup = BeautifulSoup(path.read_text(encoding="utf-8"), "html.parser")
            cards = soup.select("article[data-card]")
            require(all(card.find("h2") is not None for card in cards), "Card has no heading", path)
            titles = [card.find("h2").get_text() for card in cards]
            require(len(titles) == len(set(titles)) and not seen.intersection(titles), "Repeated chapter study card", path)
            require(all(owner.get(title) == chapter["id"] for title in titles), "Study card in wrong chapter", path)
            seen.update(titles)
        require(seen == set(owner), "Missing first-appearance study cards", html)
        counts[kind] = len(seen)
    cache = {}
    for path in html.rglob("*.html"):
        check_cancelled(cancel)
        soup = BeautifulSoup(path.read_text(encoding="utf-8"), "html.parser")
        cache[path.resolve()] = soup
        for example in soup.select(".example"):
            for node in example.find_all(string=True):
                if KANJI.search(str(node)):
                    require(node.find_parent("ruby") is not None, "Example kanji missing furigana", path)
        for tag in soup.find_all(["a", "link", "script"]):
            href = tag.get("href") or tag.get("src")
            if not href:
                continue
            parsed = urlsplit(href)
            if parsed.scheme or parsed.netloc:
                continue
            target = (path.parent / unquote(parsed.path)).resolve() if parsed.path else path.resolve()
            require(target.exists(), f"Missing local link: {href}", path)
            if parsed.fragment and target.suffix == ".html":
                if target not in cache:
                    cache[target] = BeautifulSoup(target.read_text(encoding="utf-8"), "html.parser")
                require(cache[target].find(id=unquote(parsed.fragment)) is not None, f"Missing anchor: {href}", path)
    return counts


def verify(output=None, *, book_ids=None, html=True, pdf=True, previews=True, write_report=True, cancel=None):
    from .retention import lease_manifest
    if not (Path(output or "output") / ".publication/active.json").is_file():
        return _verify(output, book_ids=book_ids, html=html, pdf=pdf, previews=previews, write_report=write_report, cancel=cancel)
    with lease_manifest(Path(output or "output")):
        return _verify(output, book_ids=book_ids, html=html, pdf=pdf, previews=previews, write_report=write_report, cancel=cancel)


def _verify(output=None, *, book_ids=None, html=True, pdf=True, previews=True, write_report=True, cancel=None):
    """Validate only requested capabilities and books, returning summaries.

    No PDF libraries are loaded when pdf=False. Reader-only verification accepts
    unresolved readings and never requires study metadata or document files.
    """
    output = Path(output or "output")
    from .publication import read_manifest, snapshot_root, verify_record
    manifest = read_manifest(output)
    roots = {}
    if manifest is not None:
        for bid, book in manifest["books"].items():
            if book_ids is not None and bid not in book_ids:
                continue
            record = book.get("study") if html or pdf else book["reader"]
            if record is None or (html and not record["capabilities"].get("html")) or (pdf and not record["capabilities"].get("pdf")):
                continue
            root = snapshot_root(output, record)
            verify_record(output, record, cancel=cancel)
            roots[root / "data" / bid / "reader.json"] = root
        paths = sorted(roots)
    else:
        paths = sorted((output / "data").glob("book-*/reader.json"))
    if book_ids is not None:
        requested = set(book_ids)
        paths = [p for p in paths if p.parent.name in requested]
        require({p.parent.name for p in paths} == requested, "Requested reader output is missing", output)
    require(paths, "No built reader.json files found", output)
    summaries = []
    for path in paths:
        check_cancelled(cancel)
        book_output = roots.get(path, output)
        book = load_json(path)
        require(book.get("id") == path.parent.name, "Book id disagrees with output directory", path)
        diagnostics = validate_reader(book)
        require(load_json(path.with_name("quality.json")) == book["quality"], "Quality file disagrees with reader data", path)
        lookup = load_json(path.with_name("lookup.json"))
        require(isinstance(lookup, dict) and lookup.get("book_id") == book["id"] and isinstance(lookup.get("forms"), dict),
                "Invalid lookup companion file", path)
        vocabulary = {entry["id"] for entry in book["vocabulary"]}
        require(all(isinstance(ids, list) and all(isinstance(eid, str) and eid in vocabulary for eid in ids)
                    for ids in lookup["forms"].values()), "Unknown lookup vocabulary reference", path)
        if html or pdf:
            validate_study(book)
        for kanji in book["kanji"]:
            stroke = kanji.get("stroke_order")
            if stroke and (html or pdf):
                require((path.parent / stroke["asset"]).is_file(), "Missing KanjiVG asset", path)
                require([s["number"] for s in stroke["strokes"]] == list(range(1, len(stroke["strokes"])+1)), "Invalid stroke order", path)
        summary = {"book_id": book["id"], "vocabulary": len(book["vocabulary"]),
                   "matched": book["quality"]["vocabulary_matched"], "kanji": len(book["kanji"]),
                   "grammar": len(book["grammar"]), "diagnostics": diagnostics,
                   "verified": ["schema", "references", "offsets", "ruby"]}
        sets = book.get("learning_sets", [])
        summary.update(stroke_order_coverage=sum(bool(k.get("stroke_order")) for k in book["kanji"]),
                       learning_sets=len(sets), last_set_size=sets[-1]["size"] if sets else 0)
        if html:
            summary["unique_chapter_cards"] = verify_html(book, book_output, cancel=cancel)
            summary["verified"] += ["new-only chapter cards", "HTML links", "example furigana"]
        if pdf:
            from .pdf_validation import verify_pdfs
            preview_root = output / "reports/validation-runs" / uuid.uuid4().hex / "previews" if manifest else None
            summary.update(verify_pdfs(book, book_output, path, previews=previews, cancel=cancel, preview_root=preview_root))
            summary["verified"] += ["PDF covers and blank pages", "PDF layout", "PDF text bounds", "embedded fonts"]
        summaries.append(summary)
    if write_report:
        from .enrichment import save_json
        save_json(output / ("reports/verification-latest.json" if manifest else "reports/verification.json"), summaries)
    return summaries


def main(argv=None):
    parser = argparse.ArgumentParser(description="Validate reader data and optional study documents")
    parser.add_argument("--output", type=Path, default=Path("output"))
    parser.add_argument("--book-id", action="append")
    parser.add_argument("--reader-only", action="store_true")
    parser.add_argument("--skip-pdf", action="store_true")
    parser.add_argument("--no-previews", action="store_true")
    parser.add_argument("--no-report", action="store_true")
    args = parser.parse_args(argv)
    try:
        result = verify(args.output, book_ids=args.book_id, html=not args.reader_only,
                        pdf=not (args.reader_only or args.skip_pdf), previews=not args.no_previews,
                        write_report=not args.no_report)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Validation error: {error}\n")
    print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
