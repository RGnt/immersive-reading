"""Local KanjiVG release ingestion; original SVGs and ordered paths are retained."""
import json
from pathlib import Path
from xml.etree import ElementTree as ET
from .jobs import check_cancelled

SVG = "{http://www.w3.org/2000/svg}"


def parse_glyph(raw, name, filename):
    root = ET.fromstring(raw)
    paths = [{"number": i+1, "id": p.get("id"), "d": p.get("d")}
             for i, p in enumerate(root.iter(SVG+"path")) if p.get("d")]
    return {"source": "KanjiVG", "source_file": name, "asset": f"assets/kanjivg/{filename}",
            "view_box": [float(n) for n in root.get("viewBox", "0 0 109 109").split()],
            "strokes": paths, "license": "CC BY-SA 3.0", "author": "Ulrich Apel"}


def add_stroke_order(book, archive, *, cancel=None, store=None):
    archive = Path(archive)
    if not archive.exists():
        raise FileNotFoundError(f"KanjiVG archive missing: {archive}. See README.md for setup.")
    from .resources import ResourceBundle, ResourceStore, thaw
    if store is None:
        with ResourceStore(ResourceBundle(kanjivg=archive)) as owned:
            return add_stroke_order(book, archive, cancel=cancel, store=owned)
    if archive.resolve() != store.bundle.kanjivg:
        raise ValueError("KanjiVG path disagrees with resource store")
    metadata = thaw(store.kanjivg_metadata(cancel=cancel))
    missing = []
    glyphs = store.kanjivg_glyphs([k["character"] for k in book["kanji"]], cancel=cancel)
    for k in book["kanji"]:
        check_cancelled(cancel)
        record = glyphs[k["character"]]
        k["stroke_order"] = thaw(record["stroke_order"]) if record else None
        if not record:
            missing.append(k["id"])
    checksum = store.pin(archive, cancel=cancel)["sha256"]
    book["build_inputs"]["kanjivg"] = dict(metadata, sha256=checksum)
    book["quality"]["missing_stroke_order_ids"] = missing
    book["attribution"].append({"name": "KanjiVG", "author": "Ulrich Apel", "license": "CC BY-SA 3.0",
                                 "url": "https://github.com/KanjiVG/kanjivg", "scope": "Stroke paths and order; cumulative diagrams adapted from original SVGs"})


def copy_svg_assets(book, archive, root, *, cancel=None, store=None):
    from .resources import ResourceBundle, ResourceStore
    if store is None:
        with ResourceStore(ResourceBundle(kanjivg=Path(archive))) as owned:
            return copy_svg_assets(book, archive, root, cancel=cancel, store=owned)
    if Path(archive).resolve() != store.bundle.kanjivg:
        raise ValueError("KanjiVG path disagrees with resource store")
    destination = root / "assets/kanjivg"
    destination.mkdir(parents=True, exist_ok=True)
    glyphs = store.kanjivg_glyphs([k["character"] for k in book["kanji"]], cancel=cancel)
    for k in book["kanji"]:
        check_cancelled(cancel)
        data = k.get("stroke_order")
        if data:
            record = glyphs[k["character"]]
            if record is None or record["stroke_order"]["source_file"] != data["source_file"]:
                raise ValueError("KanjiVG assets disagree with selected resource")
            (destination / Path(data["source_file"]).name).write_bytes(record["svg"])
    (destination / "ATTRIBUTION.txt").write_text(
        "KanjiVG: copyright Ulrich Apel.\nCreative Commons Attribution-ShareAlike 3.0\n"
        "https://creativecommons.org/licenses/by-sa/3.0/\nhttps://github.com/KanjiVG/kanjivg\n"
        "Original SVGs are unchanged. Printed cumulative stroke diagrams are adaptations of these paths.\n"
        + json.dumps(book["build_inputs"]["kanjivg"], indent=2), encoding="utf-8")
