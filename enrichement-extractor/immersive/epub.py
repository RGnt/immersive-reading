"""EPUB spine/navigation extraction. Offsets refer to ruby-free normalized text."""
import hashlib
import posixpath
import re
import unicodedata
from urllib.parse import unquote, urldefrag
from zipfile import ZipFile
from bs4 import BeautifulSoup


def uid(prefix, value):
    return prefix + "-" + hashlib.sha256(value.encode()).hexdigest()[:16]


def norm(text):
    return re.sub(r"[\s\u3000]+", "", text)


def resolve(base, href):
    path, fragment = urldefrag(unquote(href))
    return posixpath.normpath(posixpath.join(posixpath.dirname(base), path)), fragment


def read_epub(path, config=None):
    config = config or {}
    with ZipFile(path) as z:
        if "META-INF/encryption.xml" in z.namelist():
            # Fonts may be obfuscated; encrypted content is rejected by XML parsing below.
            pass
        container = BeautifulSoup(z.read("META-INF/container.xml"), "xml")
        opf_path = container.find("rootfile")["full-path"]
        opf = BeautifulSoup(z.read(opf_path), "xml")
        manifest = {i["id"]: i for i in opf.find_all("item")}
        spine = [resolve(opf_path, manifest[i["idref"]]["href"])[0]
                 for i in opf.find_all("itemref") if i.get("linear") != "no"]
        title = opf.find("title").get_text() if opf.find("title") else path.stem
        nav = next((i for i in manifest.values() if "nav" in i.get("properties", "").split()), None)
        toc = []
        if nav:
            nav_path = resolve(opf_path, nav["href"])[0]
            soup = BeautifulSoup(z.read(nav_path), "xml")
            toc_node = next((n for n in soup.find_all("nav") if "toc" in n.get("epub:type", "").split()), None)
            if toc_node:
                toc = [(a.get_text(" ", strip=True), *resolve(nav_path, a["href"])) for a in toc_node.find_all("a", href=True)]
        if not toc:
            ncx = next((i for i in manifest.values() if i.get("media-type") == "application/x-dtbncx+xml"), None)
            if ncx:
                ncx_path = resolve(opf_path, ncx["href"])[0]
                soup = BeautifulSoup(z.read(ncx_path), "xml")
                toc = [(n.find("text").get_text(), *resolve(ncx_path, n.find("content")["src"])) for n in soup.find_all("navPoint")]
        excluded = {"もくじ", "目次", "奥付", "表紙", "扉", "Contents", "Table of Contents", "Cover", "Copyright"}
        excluded.update(config.get("exclude_titles", []))
        excluded_normalized = {norm(unicodedata.normalize("NFKC", label)).casefold() for label in excluded}
        all_toc = list(dict.fromkeys(toc))
        # Fall back only when navigation supplies no usable spine entries, not
        # when a user's filters intentionally remove those entries.
        if not any(t[1] in spine for t in all_toc):
            all_toc = [(f"Section {i+1}", p, "") for i, p in enumerate(spine)]
        toc = [t for t in all_toc if t[1] in spine and norm(unicodedata.normalize("NFKC", t[0])).casefold() not in excluded_normalized]
        if config.get("include_titles"):
            toc = [t for t in toc if t[0] in config["include_titles"]]
        if not toc:
            raise ValueError(f"{path}: No chapters remain after navigation and chapter filters; check study.config.json")
        # Extract blocks once. Ruby readings remain separate, never part of the surface text.
        blocks = []
        anchors = {}
        for href in spine:
            soup = BeautifulSoup(z.read(href), "xml")
            body = soup.find("body")
            if body is None:
                continue
            anchors[(href, "")] = len(blocks)
            block_index = 0
            for el in body.descendants:
                if not getattr(el, "name", None):
                    continue
                if el.get("id"):
                    anchors.setdefault((href, el["id"]), len(blocks))
                if el.name not in ("p", "h1", "h2", "h3", "h4", "li") or el.find_parent(["p", "li"]):
                    continue
                for descendant in el.find_all(id=True):
                    anchors.setdefault((href, descendant["id"]), len(blocks))
                raw = BeautifulSoup(str(el), "xml").find(el.name)
                ruby = []
                for r in raw.find_all("ruby"):
                    bases = r.find_all("rb", recursive=False)
                    readings = r.find_all("rt", recursive=False)
                    if bases and len(bases) == len(readings):
                        pairs = [(norm(b.get_text()), norm(t.get_text())) for b, t in zip(bases, readings)]
                    else:
                        reading = "".join(t.get_text() for t in readings)
                        for t in r.find_all(["rt", "rp"]):
                            t.decompose()
                        pairs = [(norm(r.get_text()), norm(reading))]
                    # Use markers to compute precise offsets even with repeated ruby bases.
                    replacement = ""
                    for base, reading in pairs:
                        marker = f"\ue000{len(ruby)}\ue001"
                        ruby.append({"surface": base, "reading": reading, "marker": marker})
                        replacement += marker
                    r.replace_with(replacement)
                for t in raw.find_all(["rt", "rp", "script", "style"]):
                    t.decompose()
                text = norm(raw.get_text())
                for r in ruby:
                    start = text.index(r.pop("marker"))
                    marker = re.search("\ue000\\d+\ue001", text[start:]).group()
                    text = text[:start] + r["surface"] + text[start+len(marker):]
                    r.update(start=start, end=start+len(r["surface"]))
                if text:
                    blocks.append({"href": href, "element_id": el.get("id"), "paragraph_index": block_index,
                                   "text": text, "ruby": ruby, "heading": el.name.startswith("h")})
                    block_index += 1
        chapters = []
        toc = sorted(toc, key=lambda t: (spine.index(t[1]), anchors.get((t[1], t[2]), anchors.get((t[1], ""), 0))))
        ordered_toc = sorted([t for t in all_toc if t[1] in spine], key=lambda t: (spine.index(t[1]), anchors.get((t[1], t[2]), anchors.get((t[1], ""), 0))))
        for i, (label, href, fragment) in enumerate(toc):
            start = anchors.get((href, fragment), anchors[(href, "")])
            nav_position = ordered_toc.index((label, href, fragment)) if (label, href, fragment) in ordered_toc else -1
            next_nav = ordered_toc[nav_position+1] if 0 <= nav_position < len(ordered_toc)-1 else None
            end = anchors.get((next_nav[1], next_nav[2]), anchors.get((next_nav[1], ""), len(blocks))) if next_nav else len(blocks)
            if config.get("end_before_href"):
                end = min(end, anchors.get((config["end_before_href"], ""), len(blocks)))
            selected = blocks[start:end]
            text = ""
            paragraphs = []
            for block in selected:
                if block["heading"] or norm(block["text"]) == norm(label):
                    continue
                b = dict(block, start=len(text), end=len(text)+len(block["text"]))
                b["ruby"] = [dict(r, start=r["start"]+len(text), end=r["end"]+len(text)) for r in b["ruby"]]
                paragraphs.append(b)
                text += block["text"]
            cid = f"chapter-{i+1:02d}"
            sentences = []
            for paragraph in paragraphs:
                for match in re.finditer(r"[^。！？!?]+[。！？!?][」』”]*|[^。！？!?]+$", paragraph["text"]):
                    s = match.group()
                    start, end = paragraph["start"]+match.start(), paragraph["start"]+match.end()
                    sid = uid("s", href + "#" + fragment + ":" + str(start) + ":" + s)
                    sources = [{"href": paragraph["href"], "element_id": paragraph["element_id"], "paragraph_index": paragraph["paragraph_index"],
                                "start": match.start(), "end": match.end()}]
                    sentences.append({"id": sid, "chapter_id": cid, "text": s, "start": start, "end": end, "sources": sources})
            chapters.append({"id": cid, "number": i+1, "title": label, "href": href, "fragment": fragment,
                             "text": text, "paragraphs": paragraphs, "sentences": sentences})
        return {"title": title, "chapters": chapters,
                "publication": {key: opf.find(key).get_text() if opf.find(key) else "" for key in ("creator", "publisher", "date")}}
