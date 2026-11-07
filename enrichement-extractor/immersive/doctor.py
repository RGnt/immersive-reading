"""Capability-specific checks over the same resources used by applications."""
import importlib
import importlib.metadata
import json
import sys
from pathlib import Path
from zipfile import ZipFile

from .resources import resource_worker
from .validation import require


def check(root=None, *, resources=None, reader_only=False, capabilities=None, yomitan_paths=None):
    selected = set(capabilities) if capabilities is not None else {"reader"} if reader_only else {"reader", "html", "pdf", "cards"}
    require(not (selected - {"reader", "html", "pdf", "cards"}), "Unknown resource capability")
    selected.add("reader")
    resources = resources if resources is not None else Path(root or ".") / "data"
    modules = {"beautifulsoup4": "bs4", "lxml": "lxml.etree", "sudachipy": "sudachipy",
               "sudachidict_core": "sudachidict_core", "jsonschema": "jsonschema"}
    if "pdf" in selected:
        from .capabilities import require_print
        require_print()
        modules.update({"reportlab": "reportlab", "pymupdf": "pymupdf", "fonttools": "fontTools"})
    versions = {}
    for distribution, module in modules.items():
        importlib.import_module(module)
        versions[distribution] = importlib.metadata.version(distribution)
    with resource_worker(resources, yomitan_paths=yomitan_paths) as worker:
        store, bundle = worker.store, worker.store.bundle
        store.reader_manifest()
        store.kanjidic()
        store.primary_meanings()
        store.grammar()
        require(worker.tokenize("日本語"), "Morphology dictionary unavailable")
        if selected & {"html", "pdf"}:
            store.print_manifest(pdf="pdf" in selected)
            with ZipFile(bundle.kanjivg) as archive:
                require(any(n.endswith(".svg") for n in archive.namelist()), "KanjiVG has no SVG glyphs")
        if "pdf" in selected:
            from .pdf_elements import register_font
            register_font(bundle.font)
            from PIL import Image
            for chart in bundle.charts:
                with Image.open(chart) as image:
                    image.verify()
        from .yomitan import load_dictionary
        dictionaries = [load_dictionary(path, term_filter=lambda _: False, kanji_filter=set())["source"] for path in bundle.yomitan]
        result = {"python": sys.version.split()[0], "executable": sys.executable,
                  "root": str(root or bundle.root or ""), "packages": versions,
                  "resources": str(bundle.root) if bundle.root else None,
                  "capability": "reader" if selected == {"reader"} else "study", "capabilities": sorted(selected),
                  "offline_resources": "ok", "language_model_required": False,
                  "yomitan_dictionaries": [{"file": d["file"], "title": d["index"]["title"], "revision": d["index"]["revision"],
                                             "sha256": d["sha256"], "bank_counts": d["bank_counts"]} for d in dictionaries]}
    print(json.dumps(result, ensure_ascii=True, indent=2))
    return result
