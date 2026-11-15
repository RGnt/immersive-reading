"""Optional grammar notes that ship next to a deck (``deck-grammar.json``).

The file is produced by the immersive-reader *Deck Builder* plugin with the help of an LLM. This package never calls a
model and never touches the network: it reads the file as plain data (invariants 1 and 4 in AGENTS.md) and keeps only
what can be verified against the EPUB text. A grammar point survives only if at least one of its example sentences
occurs verbatim in a chapter and its ``surface`` occurs inside that sentence; everything else is reported in
``book["deck_grammar"]["dropped"]`` and never reaches the study material. Story sentences are not translated: the
file contributes English only as pattern meaning / explanation.

Contract: specs/16-deck-llm-enrichment-spec.md.
"""
import hashlib
import json
import re
from pathlib import Path
from .text_index import SentenceIndex, utf16_offsets

FILE_NAME = "deck-grammar.json"
SCHEMA = "immersive-deck-grammar/1"
STATUS = "deck_llm_candidate"
_ID = re.compile(r"^dg-[0-9a-f]{6,40}$")
_SPACE = re.compile(r"[\s　]+")


def path_for(folder):
    return Path(folder) / FILE_NAME


def load(path):
    """Read and structurally validate the file. Raises ``ValueError`` (never ``assert``) on a malformed file."""
    path = Path(path)
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as error:
        raise ValueError(f"{path}: not valid JSON ({error})") from error
    if not isinstance(data, dict) or data.get("schema") != SCHEMA:
        raise ValueError(f"{path}: expected schema {SCHEMA!r}")
    points = data.get("grammar")
    if not isinstance(points, list):
        raise ValueError(f"{path}: 'grammar' must be a list")
    seen = set()
    for number, point in enumerate(points):
        where = f"{path}: grammar[{number}]"
        if not isinstance(point, dict):
            raise ValueError(f"{where} must be an object")
        for key in ("id", "pattern", "meaning", "explanation"):
            if not isinstance(point.get(key), str):
                raise ValueError(f"{where}.{key} must be a string")
        if not _ID.match(point["id"]) or point["id"] in seen:
            raise ValueError(f"{where}.id must be a unique 'dg-<hex>' id")
        if not point["pattern"].strip():
            raise ValueError(f"{where}.pattern must not be empty")
        seen.add(point["id"])
        examples = point.get("examples", [])
        if not isinstance(examples, list) or not all(
                isinstance(e, dict) and isinstance(e.get("text"), str) and isinstance(e.get("surface"), str) for e in examples):
            raise ValueError(f"{where}.examples must be a list of {{text, surface}} objects")
    notes = data.get("function_word_notes", {})
    if not isinstance(notes, dict) or not all(isinstance(v, list) for v in notes.values()):
        raise ValueError(f"{path}: 'function_word_notes' must map a word to a list of uses")
    return data


def provenance(path, data):
    raw = Path(path).read_bytes()
    generator = data.get("generator") if isinstance(data.get("generator"), dict) else {}
    return {"file": Path(path).name, "sha256": hashlib.sha256(raw).hexdigest(), "schema": data["schema"],
            "status": data.get("status", ""), "model": generator.get("model", ""),
            "generated_at": generator.get("generated_at", "")}


def merge(book, data):
    """Return ``(grammar_entries, dropped)`` for the points whose examples verify against the chapters."""
    order = {c["id"]: i for i, c in enumerate(book["chapters"])}
    lookup = {c["id"]: (SentenceIndex(c["sentences"]), utf16_offsets(c["text"])) for c in book["chapters"]}
    generator = data.get("generator") if isinstance(data.get("generator"), dict) else {}
    entries, dropped = [], []
    for point in data["grammar"]:
        occurrences = []
        for example in point.get("examples", []):
            needle, surface = _SPACE.sub("", example["text"]), _SPACE.sub("", example["surface"])
            if not needle or not surface or surface not in needle:
                continue
            for chapter in book["chapters"]:
                text = chapter["text"]
                at = text.find(needle)
                if at < 0:
                    continue
                start = text.find(surface, at, at + len(needle))
                sentence = lookup[chapter["id"]][0].at(start) if start >= 0 else None
                if sentence is None:
                    continue
                end = start + len(surface)
                utf16 = lookup[chapter["id"]][1]
                occurrences.append({"chapter_id": chapter["id"], "sentence_id": sentence["id"], "start": start, "end": end,
                                    "surface": text[start:end], "start_utf16": utf16[start], "end_utf16": utf16[end]})
                break
        occurrences = list({(o["chapter_id"], o["start"], o["end"]): o for o in occurrences}.values())
        if not occurrences:
            dropped.append({"id": point["id"], "pattern": point["pattern"], "reason": "no example found verbatim in the book text"})
            continue
        occurrences.sort(key=lambda o: (order[o["chapter_id"]], o["start"], o["end"]))
        entries.append({"id": point["id"], "label": point["pattern"].strip(), "meaning": point["meaning"].strip(),
                        "note": point["explanation"].strip(), "category": point.get("category") or "grammar",
                        "first_chapter_id": occurrences[0]["chapter_id"], "status": STATUS, "source": FILE_NAME,
                        "model": generator.get("model", ""), "occurrences": occurrences})
    return entries, dropped


def function_notes(data):
    """``{word: "function: note ; function: note"}``, used when the deck CSV carries no ``UsageNote``."""
    result = {}
    for word, uses in data.get("function_word_notes", {}).items():
        parts = []
        for use in uses:
            if not isinstance(use, dict) or not isinstance(use.get("function"), str) or not use["function"].strip():
                continue
            note = use.get("note") if isinstance(use.get("note"), str) else ""
            parts.append(f'{use["function"].strip()}: {note.strip()}' if note.strip() else use["function"].strip())
        if parts:
            result[word] = " ; ".join(parts)
    return result
