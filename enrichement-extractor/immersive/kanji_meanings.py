"""Exact-character, attributed learner meanings; no vocabulary-sense guessing."""
import csv
import hashlib
import json
from collections import Counter
from pathlib import Path


def split_meanings(text):
    parts, start, depth = [], 0, 0
    for index, char in enumerate(text):
        if char == "(":
            depth += 1
        elif char == ")":
            depth = max(0, depth-1)
        elif char in ",;" and depth == 0:
            parts.append(text[start:index].strip())
            start = index+1
    return [part for part in parts+[text[start:].strip()] if part]


def load_primary_meanings(resource):
    resource = Path(resource)
    source = json.loads((resource/"source.json").read_text(encoding="utf-8"))
    data = resource/"ka_data.csv"
    digest = hashlib.sha256(data.read_bytes()).hexdigest()
    if digest != source["sha256"]:
        raise ValueError(f"{data}: source checksum mismatch")
    with data.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if not {"kanji", "kmeaning"} <= set(reader.fieldnames or []):
            raise ValueError(f"{data}: expected kanji and kmeaning fields")
        meanings = {}
        for row in reader:
            character = row["kanji"].strip()
            if len(character) != 1 or character in meanings or not row["kmeaning"].strip():
                raise ValueError(f"{data}: invalid or duplicate kanji {character!r}")
            meanings[character] = split_meanings(row["kmeaning"])
    return meanings, source, digest


def add_primary_meanings(book, resource=None, *, loaded=None):
    from .resources import thaw
    meanings, source, digest = loaded if loaded is not None else load_primary_meanings(resource)
    source = thaw(source)
    counts = Counter()
    for kanji in book["kanji"]:
        dictionary = kanji["dictionary"] or {}
        if kanji["character"] in meanings:
            primary = list(meanings[kanji["character"]])
            provenance = {"name": "Kanji alive", "license": "CC BY 4.0", "url": source["url"],
                          "copyright": source["copyright"], "commit": source["commit"]}
        else:
            # KANJIDIC2 does not rank senses: retain its complete English list.
            primary = dictionary.get("meanings", [])
            provenance = {"name": "KANJIDIC2 / EDRDG", "license": "CC BY-SA 4.0",
                          "url": "https://www.edrdg.org/enamdict/kanjidic.html", "fallback": True,
                          "note": "All supplied English meanings; source does not rank primary senses."}
        dictionary.update(primary_meanings=primary, meaning_source=provenance)
        kanji["dictionary"] = dictionary
        counts[provenance["name"] if primary else "unavailable"] += 1
    book["build_inputs"]["kanji_meanings"] = dict(source, sha256=digest,
        policy="Kanji alive kmeaning preferred; complete KANJIDIC2 English meanings for uncovered characters. Exact character match only.")
    book["quality"]["kanji_meaning_sources"] = dict(counts)
    attribution = {"name": "Kanji alive", "url": "https://kanjialive.com/credits/", "license": "CC BY 4.0",
                   "copyright": source["copyright"], "scope": "Primary kanji meanings (kmeaning); no external example sentences or mnemonics used"}
    if not any(item["name"] == "Kanji alive" for item in book["attribution"]):
        book["attribution"].append(attribution)
