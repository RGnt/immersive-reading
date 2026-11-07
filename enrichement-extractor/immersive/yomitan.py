"""Offline Yomitan format-3 ZIP ingestion; no extraction or HTML execution.

Dictionary candidates remain separate from reviewed/contextual meanings.
The original glossary JSON is data, never trusted markup for a renderer.
"""
import hashlib
import json
import math
import re
from collections import defaultdict
from pathlib import Path, PurePosixPath
from zipfile import ZipFile, BadZipFile

from .epub import uid
from .japanese import hira
from .jobs import check_cancelled

BANK = re.compile(r"(term|kanji|term_meta|kanji_meta|tag)_bank_([1-9][0-9]*)\.json\Z")
MAX_JSON_BYTES = 128 * 1024 * 1024
MAX_TOTAL_JSON_BYTES = 2 * 1024 * 1024 * 1024
NOTICE_NAMES = {name + suffix for name in ("license", "licence", "copyright", "attribution", "copying", "notice", "readme")
                for suffix in ("", ".txt", ".md")}


def dictionary_paths(resources, paths=None):
    """Explicit paths override discovery; an empty list disables dictionaries."""
    selected = sorted((Path(resources) / "yomitan").glob("*.zip")) if paths is None else paths
    return list(dict.fromkeys(Path(p).resolve() for p in selected))


def _require(condition, message, where):
    if not condition:
        raise ValueError(f"{where}: {message}")


def _number(value):
    return type(value) is int or (type(value) is float and math.isfinite(value))


def _strings(value):
    return isinstance(value, list) and all(isinstance(v, str) for v in value)


def _invalid_number(value):
    raise ValueError(f"Invalid JSON number {value}")


def _safe_name(name):
    p = PurePosixPath(name)
    return bool(name) and not p.is_absolute() and not any(x in {"..", "."} for x in name.split("/")) and "\\" not in name and ":" not in name


def glossary_text(value, *, structured=False):
    """Make a plain-text preview, preserving inline joins and block boundaries."""
    if isinstance(value, str):
        return value
    if isinstance(value, list):
        return "".join(glossary_text(v, structured=structured) for v in value)
    if not isinstance(value, dict):
        raise ValueError("Glossary content must be text, an object or an array")
    if not structured:
        kind = value.get("type")
        if kind == "text" and isinstance(value.get("text"), str):
            return value["text"]
        if kind == "structured-content" and "content" in value:
            return glossary_text(value["content"], structured=True)
        if kind == "image" and isinstance(value.get("path"), str):
            return str(value.get("alt") or value.get("description") or "[dictionary image]")
        raise ValueError("Unsupported or malformed glossary type")
    tag = value.get("tag")
    if not isinstance(tag, str):
        raise ValueError("Structured glossary node needs a tag")
    if tag == "br":
        return "\n"
    if tag == "img":
        return str(value.get("alt") or value.get("description") or "[dictionary image]")
    text = glossary_text(value.get("content", ""), structured=True)
    if tag in {"div", "li", "tr", "ol", "ul", "table", "details", "summary"}:
        return "\n" + text + "\n"
    if tag in {"td", "th"}:
        return text + "\t"
    return text


def _frequency(value):
    return isinstance(value, str) or _number(value) or (
        isinstance(value, dict) and _number(value.get("value")) and
        ("displayValue" not in value or isinstance(value["displayValue"], str)))


def _metadata(row, where, *, kanji=False):
    _require(len(row) == 3 and isinstance(row[1], str), "expected a three-field metadata record", where)
    mode, value = row[1:]
    if mode == "freq":
        if not kanji and isinstance(value, dict) and "reading" in value:
            _require(isinstance(value["reading"], str) and _frequency(value.get("frequency")), "invalid reading-specific frequency", where)
        else:
            _require(_frequency(value), "invalid frequency", where)
    elif not kanji and mode in {"pitch", "ipa"}:
        key = "pitches" if mode == "pitch" else "transcriptions"
        _require(isinstance(value, dict) and isinstance(value.get("reading"), str) and isinstance(value.get(key), list), "invalid pronunciation metadata", where)
        for item in value[key]:
            _require(isinstance(item, dict), "invalid pronunciation candidate", where)
            if mode == "pitch":
                position = item.get("position")
                _require((type(position) is int and position >= 0) or (isinstance(position, str) and re.fullmatch("[HL]+", position)), "invalid pitch position", where)
            else:
                _require(isinstance(item.get("ipa"), str), "invalid IPA transcription", where)
    else:
        raise ValueError(f"{where}: unsupported metadata mode {mode!r}")


def load_dictionary(path, *, term_filter=None, kanji_filter=None, cancel=None):
    """Read one format-3 dictionary, optionally retaining only selected entries.

    Banks are processed one at a time. Image/audio/CSS files are not loaded;
    glossary references remain in the original JSON for future adapters.
    """
    path = Path(path)
    terms, kanji, term_meta, kanji_meta, tags = [], [], [], [], {}
    counts = defaultdict(int)
    try:
        with path.open("rb") as stream:
            digest = hashlib.sha256()
            while chunk := stream.read(1024 * 1024):
                check_cancelled(cancel)
                digest.update(chunk)
            checksum = digest.hexdigest()
            stream.seek(0)
            with ZipFile(stream) as archive:
                infos = archive.infolist()
                names = [info.filename for info in infos]
                _require(len(names) == len(set(names)), "duplicate archive member", path)
                _require(all(_safe_name(n.rstrip('/')) for n in names), "unsafe archive member path", path)
                _require("index.json" in names, "missing root index.json", path)
                json_bytes = sum(i.file_size for i in infos if i.filename.endswith(".json"))
                _require(json_bytes <= MAX_TOTAL_JSON_BYTES, "dictionary JSON exceeds 2 GiB limit", path)

                def read_json(name):
                    check_cancelled(cancel)
                    _require(archive.getinfo(name).file_size <= MAX_JSON_BYTES, "JSON member exceeds 128 MiB limit", f"{path}!{name}")
                    try:
                        return json.loads(archive.read(name).decode("utf-8-sig"), parse_constant=_invalid_number)
                    except (UnicodeError, ValueError, RecursionError) as error:
                        raise ValueError(f"{path}!{name}: invalid JSON: {error}") from error

                index = read_json("index.json")
                _require(isinstance(index, dict), "index must be an object", path)
                _require(all(isinstance(index.get(k), str) and index[k] for k in ("title", "revision")), "index needs title and revision", path)
                _require(all(isinstance(index[k], str) for k in ("author", "description", "attribution", "sourceLanguage", "targetLanguage", "url") if k in index),
                         "dictionary index text/language fields must be strings", path)
                version = index.get("format", index.get("version"))
                _require(type(version) is int and version == 3, f"unsupported Yomitan format {version!r}; expected 3", path)
                _require("version" not in index or index["version"] == version, "conflicting format/version", path)
                did = "yomitan-" + checksum
                for name in sorted(names, key=lambda n: (BANK.fullmatch(n).group(1), int(BANK.fullmatch(n).group(2))) if BANK.fullmatch(n) else ("", 0)):
                    match = BANK.fullmatch(name)
                    if not match:
                        continue
                    kind = match[1]
                    rows = read_json(name)
                    _require(isinstance(rows, list), "bank must be an array", f"{path}!{name}")
                    for number, row in enumerate(rows, 1):
                        check_cancelled(cancel)
                        where = f"{path}!{name}[{number}]"
                        _require(isinstance(row, list) and row and isinstance(row[0], str) and row[0], "invalid bank record", where)
                        counts[kind] += 1
                        origin = {"dictionary_id": did, "dictionary_title": index["title"],
                                  "revision": index["revision"], "bank": name, "row": number}
                        if kind == "term":
                            _require(len(row) == 8 and isinstance(row[1], str) and (row[2] is None or isinstance(row[2], str)) and isinstance(row[3], str) and _number(row[4]) and isinstance(row[5], list) and type(row[6]) is int and isinstance(row[7], str), "invalid format-3 term record", where)
                            # Validate previewability even for discarded rows.
                            try:
                                _require(all(isinstance(g, (str, dict)) for g in row[5]), "invalid glossary definition", where)
                                text = [glossary_text(g).strip() for g in row[5]]
                            except (ValueError, RecursionError) as error:
                                raise ValueError(f"{where}: {error}") from error
                            if term_filter is None or term_filter(row[0]):
                                terms.append(dict(origin, expression=row[0], reading=row[1] or row[0],
                                                  definition_tags=(row[2] or "").split(), rules=row[3].split(),
                                                  score=row[4], glossary=row[5], glossary_text=text,
                                                  sequence=row[6], term_tags=row[7].split(), status="dictionary_candidate"))
                        elif kind == "kanji":
                            _require(len(row) == 6 and all(isinstance(v, str) for v in row[:4]) and _strings(row[4]) and isinstance(row[5], dict) and all(isinstance(v, str) for v in row[5].values()), "invalid kanji record", where)
                            if kanji_filter is None or row[0] in kanji_filter:
                                kanji.append(dict(origin, character=row[0], on=row[1].split(), kun=row[2].split(), tags=row[3].split(), meanings=row[4], stats=row[5]))
                        elif kind in {"term_meta", "kanji_meta"}:
                            _metadata(row, where, kanji=kind == "kanji_meta")
                            keep = (term_filter is None or term_filter(row[0])) if kind == "term_meta" else (kanji_filter is None or row[0] in kanji_filter)
                            if keep:
                                (term_meta if kind == "term_meta" else kanji_meta).append(dict(origin, expression=row[0], mode=row[1], data=row[2]))
                        else:
                            _require(len(row) == 5 and isinstance(row[1], str) and _number(row[2]) and isinstance(row[3], str) and _number(row[4]), "invalid tag record", where)
                            _require(row[0] not in tags, "duplicate tag", where)
                            tags[row[0]] = {"category": row[1], "order": row[2], "notes": row[3], "score": row[4]}
                _require(counts, "no supported dictionary banks found", path)
                notices = {}
                for name in names:
                    if PurePosixPath(name).name.lower() in NOTICE_NAMES:
                        _require(archive.getinfo(name).file_size <= 1024 * 1024, "notice exceeds 1 MiB limit", path)
                        notices[name] = archive.read(name).decode("utf-8-sig", errors="replace")
            stream.seek(0)
            after = hashlib.sha256()
            while chunk := stream.read(1024 * 1024):
                check_cancelled(cancel)
                after.update(chunk)
            _require(after.hexdigest() == checksum, "dictionary changed during import", path)
    except (BadZipFile, RuntimeError) as error:
        # Cancellation is a RuntimeError too; preserve its type.
        from .jobs import CancelledError
        if isinstance(error, CancelledError):
            raise
        raise ValueError(f"{path}: unreadable dictionary ZIP: {error}") from error
    return {"source": {"id": did, "file": path.name, "sha256": checksum, "index": index,
                        "notices": notices, "bank_counts": dict(counts), "tags": tags,
                        "media_policy": "References retained as untrusted JSON; media/CSS not extracted or rendered."},
            "terms": terms, "kanji": kanji, "term_meta": term_meta, "kanji_meta": kanji_meta}


def vocabulary_entries(dictionaries):
    """One vocabulary entry per spelling/reading; first dictionary supplies glosses."""
    entries = {}
    for dictionary in dictionaries:
        for term in dictionary["terms"]:
            key = term["expression"], hira(term["reading"])
            if key not in entries:
                entries[key] = {"id": uid("v", "yomitan:" + key[0] + ":" + key[1]),
                                "word": key[0], "reading": term["reading"], "furigana": "", "definitions": "",
                                "jmdict_id": None, "pitch_positions": "", "deck_occurrences": "",
                                "frequency_rank": "", "sources": [], "deck_records": [],
                                "deck_examples": [], "occurrences": [], "vocabulary_source": "yomitan",
                                "definition_dictionary_id": term["dictionary_id"], "dictionary_glosses": []}
            entry = entries[key]
            entry["sources"].append({"dictionary_id": term["dictionary_id"], "bank": term["bank"], "row": term["row"]})
            if entry["definition_dictionary_id"] == term["dictionary_id"]:
                entry["dictionary_glosses"].extend(term["glossary_text"])
    for entry in entries.values():
        entry["definitions"] = "\n".join(dict.fromkeys(t for t in entry.pop("dictionary_glosses") if t))
    return list(entries.values())


def attach_dictionaries(book, dictionaries):
    terms, meta, kanji, kanji_meta = (defaultdict(list) for _ in range(4))
    for dictionary in dictionaries:
        for key, target, field in (("terms", terms, "expression"), ("term_meta", meta, "expression"),
                                   ("kanji", kanji, "character"), ("kanji_meta", kanji_meta, "expression")):
            for item in dictionary[key]:
                target[item[field]].append(item)
    for entry in book["vocabulary"]:
        forms = {entry["word"], *entry.get("aliases", [])}
        reading = hira(entry["reading"])
        entry["dictionary_entries"] = [t for form in sorted(forms) for t in terms[form]
                                       if not reading or hira(t["reading"]) == reading]
        entry["dictionary_metadata"] = [m for form in sorted(forms) for m in meta[form]
                                        if not isinstance(m["data"], dict) or "reading" not in m["data"]
                                        or not reading or hira(m["data"]["reading"]) == reading]
    for entry in book["kanji"]:
        entry["dictionary_entries"] = kanji[entry["character"]]
        entry["dictionary_metadata"] = kanji_meta[entry["character"]]
    book["dictionary_sources"] = [d["source"] for d in dictionaries]
    book["build_inputs"]["yomitan"] = [{"file": d["source"]["file"], "sha256": d["source"]["sha256"]} for d in dictionaries]
    for dictionary in dictionaries:
        source = dictionary["source"]
        index = source["index"]
        book["attribution"].append({"name": index["title"], "scope": "Yomitan dictionary candidates and metadata",
                                    "dictionary_id": source["id"], "revision": index["revision"],
                                    "attribution": index.get("attribution", "Not supplied in dictionary index"),
                                    "url": index.get("url", ""), "notices": source["notices"]})
