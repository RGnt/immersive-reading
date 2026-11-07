"""Build navigation from every completed HTML book, including earlier builds."""
import json
import os
import tempfile
from html import escape
from pathlib import Path


def library_page(entries):
    links = ''.join(f'<li><a href="{escape(href)}">{escape(title)}</a></li>' for title, href in entries)
    return '<!doctype html><html lang="en"><meta charset="utf-8"><title>Reading library</title><style>body{max-width:960px;margin:60px auto;padding:20px;font:18px/1.7 system-ui}a{color:#27694c}</style><h1>Immersive reading library</h1><ul>' + links + '</ul></html>'


def write_library_index(output):
    output = Path(output)
    from .publication import read_manifest, repair_views
    if read_manifest(output) is not None:
        repair_views(output)
        return output / "html/index.html"
    html = output / "html"
    html.mkdir(parents=True, exist_ok=True)
    links = []
    for page in sorted(html.glob("book-*/index.html")):
        book_id = page.parent.name
        data = output / "data" / book_id / "reader.json"
        title = json.loads(data.read_text(encoding="utf-8"))["title"] if data.exists() else book_id
        links.append((title, book_id+"/index.html"))
    content = library_page(links)
    # Readers see either complete old navigation or complete new navigation.
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=html, suffix=".tmp", mode="w", encoding="utf-8", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(content)
        os.replace(temporary, html / "index.html")
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)
    return html / "index.html"
