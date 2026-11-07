"""Reader/config validation without HTML, font or PDF dependencies."""
import json
from functools import lru_cache
from pathlib import Path

import jsonschema


class ValidationError(ValueError):
    def __init__(self, path, message):
        self.path, self.message = str(path), message
        super().__init__(f"{self.path}: {message}")


def require(condition, message, path="$"):
    """Production checks must remain enabled under python -O."""
    if not condition:
        raise ValidationError(path, str(message))


@lru_cache(maxsize=2)
def _validator(filename):
    schema = json.loads(Path(__file__).with_name(filename).read_text(encoding="utf-8"))
    return jsonschema.Draft202012Validator(schema)


def _schema(value, filename, path):
    error = next(_validator(filename).iter_errors(value), None)
    if error is not None:
        location = "".join(f"[{part}]" if isinstance(part, int) else f".{part}" for part in error.absolute_path)
        raise ValidationError(str(path)+location, error.message)


def validate_config(config, path="study.config.json"):
    _schema(config, "config.schema.json", path)


def load_json(path):
    path = Path(path)
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise ValidationError(path, f"Invalid JSON at line {error.lineno}, column {error.colno}: {error.msg}") from error


def _index(items, path):
    result = {}
    for number, item in enumerate(items):
        key = item.get("id")
        require(isinstance(key, str) and key, "expected a nonempty id", f"{path}[{number}].id")
        require(key not in result, f"duplicate id {key}", path)
        result[key] = item
    return result


def _span(span, text, path, *, surface=None, utf16=None, allow_empty=False):
    start, end = span.get("start"), span.get("end")
    require(type(start) is int and type(end) is int and 0 <= start <= end <= len(text) and (allow_empty or start < end),
            "expected a valid half-open span within the text", path)
    if surface is not None:
        require(text[start:end] == surface, "span does not match its text/surface", path)
    if "start_utf16" in span or "end_utf16" in span:
        require(utf16 is not None and span.get("start_utf16") == utf16[start] and span.get("end_utf16") == utf16[end],
                "UTF-16 offsets do not match the Unicode-code-point offsets", path)


def _ruby(rubies, text, path, *, generated):
    require(isinstance(rubies, list), "expected a ruby array", path)
    previous_end = 0
    for number, ruby in enumerate(rubies):
        where = f"{path}[{number}]"
        require(isinstance(ruby, dict), "expected a ruby object", where)
        require(isinstance(ruby.get("surface"), str), "missing ruby surface", where)
        _span(ruby, text, where, surface=ruby["surface"])
        require(ruby["start"] >= previous_end, "overlapping or unordered ruby spans", where)
        require(isinstance(ruby.get("reading"), str) and ruby["reading"], "missing ruby reading", where)
        if generated:
            require(ruby.get("source") in {"epub", "sudachi"}, "unknown ruby source", where)
        previous_end = ruby["end"]


def validate_reader(book):
    """Raise ValidationError for invalid data; return nonfatal diagnostics.

    This checks normalized reader coordinates only, not original-DOM mapping.
    Incomplete linguistic coverage is reported, not treated as invalid data.
    """
    _schema(book, "reader.schema.json", "$")
    _dictionary_references(book)
    inputs = book.get("build_inputs", {})
    resources = inputs.get("resources")
    if resources is not None:
        for key, expected in (("kanjidic", inputs.get("kanjidic_sha256")), ("grammar", inputs.get("grammar_rules_sha256")),
                              ("kanji_alive_data", inputs.get("kanji_meanings", {}).get("sha256"))):
            require(resources.get(key, {}).get("sha256") == expected and expected is not None,
                    "resource fingerprint disagrees with build inputs", "$.build_inputs.resources."+key)
    chapters = _index(book["chapters"], "$.chapters")
    vocabulary = _index(book["vocabulary"], "$.vocabulary")
    kanji = _index(book["kanji"], "$.kanji")
    grammar = _index(book["grammar"], "$.grammar")
    sentences, offsets = {}, {}
    order = {cid: number for number, cid in enumerate(chapters)}
    for number, chapter in enumerate(chapters.values()):
        where, text = f"$.chapters[{number}]", chapter["text"]
        require(chapter["number"] == number+1, "chapter numbers must follow reading order", where)
        utf16 = [0]
        for char in text:
            utf16.append(utf16[-1] + (2 if ord(char) > 0xffff else 1))
        offsets[chapter["id"]] = utf16
        require("".join(p["text"] for p in chapter["paragraphs"]) == text, "paragraphs do not reconstruct chapter text", where)
        end = 0
        for i, paragraph in enumerate(chapter["paragraphs"]):
            pwhere = f"{where}.paragraphs[{i}]"
            _span(paragraph, text, pwhere, surface=paragraph["text"])
            require(paragraph["start"] == end, "paragraph offsets must be contiguous", pwhere)
            end = paragraph["end"]
            _ruby(paragraph["ruby"], text, pwhere+".ruby", generated=False)
            require(all(paragraph["start"] <= r["start"] < r["end"] <= paragraph["end"] for r in paragraph["ruby"]),
                    "ruby is outside its paragraph", pwhere)
        local = _index(chapter["sentences"], where+".sentences")
        require(not sentences.keys() & local.keys(), "sentence ids must be unique across chapters", where)
        for sid, sentence in local.items():
            swhere = f"{where}.sentences[{sid}]"
            require(sentence["chapter_id"] == chapter["id"], "sentence refers to another chapter", swhere)
            require(isinstance(sentence.get("text"), str), "missing sentence text", swhere)
            _span(sentence, text, swhere, surface=sentence["text"])
            require("translation" not in sentence, "story sentence translations are not supported", swhere)
            _ruby(sentence.get("display_ruby", []), sentence["text"], swhere+".display_ruby", generated=True)
            paragraphs = {(p["href"], p["paragraph_index"]): p for p in chapter["paragraphs"]}
            source_text = []
            for source in sentence["sources"]:
                paragraph = paragraphs.get((source.get("href"), source.get("paragraph_index")))
                require(paragraph is not None, "unknown sentence source paragraph", swhere+".sources")
                _span(source, paragraph["text"], swhere+".sources")
                source_text.append(paragraph["text"][source["start"]:source["end"]])
            require("".join(source_text) == sentence["text"], "source ranges do not reconstruct sentence text", swhere+".sources")
        sentences.update(local)
        for field, known in (("vocabulary_ids", vocabulary), ("kanji_ids", kanji), ("new_grammar_ids", grammar)):
            require(set(chapter[field]) <= known.keys(), f"unknown reference in {field}", where)
        for i, token in enumerate(chapter["tokens"]):
            twhere = f"{where}.tokens[{i}]"
            _span(token, text, twhere, surface=token["surface"], utf16=utf16, allow_empty=token["surface"] == "")
            require(set(token["vocabulary_ids"]) <= vocabulary.keys(), "unknown token vocabulary id", twhere)
    for field, items in (("vocabulary", vocabulary), ("kanji", kanji), ("grammar", grammar)):
        for item in items.values():
            where = f"$.{field}[{item['id']}]"
            occurrences = item["occurrences"]
            for i, occurrence in enumerate(occurrences):
                owhere = f"{where}.occurrences[{i}]"
                cid = occurrence["chapter_id"]
                require(cid in chapters, "unknown occurrence chapter", owhere)
                _span(occurrence, chapters[cid]["text"], owhere,
                      surface=occurrence.get("surface", item.get("character")), utf16=offsets[cid])
                sid = occurrence["sentence_id"]
                if sid is not None:
                    require(sid in sentences, "unknown occurrence sentence", owhere)
                    sentence = sentences[sid]
                    require(sentence["chapter_id"] == cid and sentence["start"] <= occurrence["start"] < sentence["end"],
                            "occurrence sentence does not contain its start", owhere)
            first = min((o["chapter_id"] for o in occurrences), key=order.__getitem__) if occurrences else None
            require(item["first_chapter_id"] == first, "first_chapter_id does not match reading order", where)
            if field == "vocabulary":
                require(item["status"] == ("matched" if occurrences else "unmatched_review"), "status disagrees with occurrences", where)
                for cid, selection in item.get("selected_examples", {}).items():
                    ewhere = where+f".selected_examples[{cid}]"
                    require(cid in chapters, "unknown example selection chapter", ewhere)
                    require(isinstance(selection.get("text"), str), "missing example text", ewhere)
                    if selection.get("start") is not None:
                        selected_cid = selection.get("chapter_id")
                        require(selected_cid in chapters, "unknown example source chapter", ewhere)
                        _span(selection, chapters[selected_cid]["text"], ewhere, surface=selection["text"])
                    else:
                        require(selection.get("end") is None and selection.get("chapter_id") is None,
                                "unaligned example must not claim a source location", ewhere)
                    _ruby(selection.get("ruby", []), selection["text"], ewhere+".ruby", generated=True)
            if field == "kanji":
                require(set(item["vocabulary_ids"]) <= vocabulary.keys(), "unknown related vocabulary id", where)
                dictionary = item.get("dictionary") or {}
                require(isinstance(dictionary.get("primary_meanings"), list) and isinstance(dictionary.get("meaning_source"), dict),
                        "missing primary meaning provenance", where+".dictionary")
    for cid, chapter in chapters.items():
        for field, items in (("vocabulary_ids", vocabulary), ("kanji_ids", kanji)):
            expected = {item["id"] for item in items.values() if any(o["chapter_id"] == cid for o in item["occurrences"])}
            require(set(chapter[field]) == expected, "chapter references disagree with occurrences", f"$.chapters[{cid}].{field}")
    quality = book["quality"]
    matched = sum(bool(v["occurrences"]) for v in vocabulary.values())
    require(quality["vocabulary_total"] == len(vocabulary) and quality["vocabulary_matched"] == matched,
            "quality vocabulary counts disagree with data", "$.quality")
    require(set(quality["unmatched_vocabulary_ids"]) == {v["id"] for v in vocabulary.values() if not v["occurrences"]},
            "unmatched vocabulary list disagrees with data", "$.quality")
    diagnostics = []
    for code, count in (("unmatched_vocabulary", len(vocabulary)-matched),
                        ("unresolved_furigana", len(quality.get("unresolved_furigana", []))),
                        ("empty_analysis_tokens", sum(t["surface"] == "" for c in chapters.values() for t in c["tokens"])),
                        ("missing_kanji_dictionary", len(quality["missing_kanji_dictionary_ids"]))):
        if count:
            diagnostics.append({"severity": "warning", "code": code, "count": count})
    for code in ("ambiguous_reading_groups", "unverified_reading_occurrences", "unresolved_sense_entries"):
        if quality.get(code):
            diagnostics.append({"severity": "warning", "code": code, "count": quality[code]})
    _ambiguity_references(book, vocabulary, chapters)
    return diagnostics


def _ambiguity_references(book, vocabulary, chapters):
    spans = {eid: {(o["chapter_id"], o["start"], o["end"]) for o in entry["occurrences"]} for eid, entry in vocabulary.items()}
    for entry in vocabulary.values():
        candidates = _index(entry.get("definition_candidates", []), "$.definition_candidates")
        csv_sources = {(r["file"], r["csv_row"]): r["fields"].get("Definitions", "").strip() for r in entry.get("deck_records", [])}
        dictionary_sources = {(r["dictionary_id"], r["bank"], r["row"]): r for r in entry.get("dictionary_entries", [])}
        for candidate in candidates.values():
            source = candidate["source"]
            if source["kind"] == "csv":
                require(csv_sources.get((source.get("file"), source.get("csv_row"))) == candidate["text"],
                        "definition disagrees with source CSV", "$.definition_candidates")
            else:
                record = dictionary_sources.get((source.get("dictionary_id"), source.get("bank"), source.get("row")))
                index = source.get("glossary_index")
                require(record is not None and type(index) is int and 0 <= index < len(record["glossary_text"]) and record["glossary_text"][index] == candidate["text"],
                        "definition disagrees with source dictionary", "$.definition_candidates")
        selection = entry.get("sense_selection")
        if selection is not None:
            require(set(selection["selected_candidate_ids"]) <= candidates.keys(), "unknown selected definition", "$.sense_selection")
            if selection["status"] == "reviewed_override":
                require(isinstance(selection.get("reviewed_text"), str) and selection["reviewed_text"] and selection.get("source"),
                        "reviewed sense requires explicit text and provenance", "$.sense_selection")
                require(selection["reviewed_text"] == book.get("build_inputs", {}).get("book_config", {}).get("context_glosses", {}).get(entry["word"]),
                        "reviewed sense must match explicit configuration", "$.sense_selection")
                source = selection["source"]
                require(source.get("kind") == "book_config" and source.get("field") == "context_glosses" and source.get("word") == entry["word"],
                        "reviewed sense has invalid configuration provenance", "$.sense_selection")
                require(all(candidates[cid]["text"] == selection["reviewed_text"] for cid in selection["selected_candidate_ids"]),
                        "selected definition disagrees with reviewed text", "$.sense_selection")
            else:
                require(not selection["selected_candidate_ids"] and selection.get("reviewed_text") is None,
                        "unresolved senses cannot claim a selected meaning", "$.sense_selection")
        for occurrence in entry["occurrences"]:
            if "candidate_vocabulary_ids" not in occurrence:
                continue
            ids = occurrence["candidate_vocabulary_ids"]
            require(entry["id"] in ids and set(ids) <= vocabulary.keys(), "unknown occurrence candidate", "$.occurrences.candidate_vocabulary_ids")
            for eid in ids:
                require((occurrence["chapter_id"], occurrence["start"], occurrence["end"]) in spans[eid],
                        "candidate does not share occurrence span", "$.occurrences.candidate_vocabulary_ids")
    for group in book.get("ambiguities", []):
        require(group["chapter_id"] in chapters and set(group["candidate_vocabulary_ids"]) <= vocabulary.keys(), "unknown ambiguity reference", "$.ambiguities")
        _span(group, chapters[group["chapter_id"]]["text"], "$.ambiguities")
        require(group["selected_vocabulary_id"] is None, "reading ambiguity cannot claim an automatic selection", "$.ambiguities")


def _dictionary_references(book):
    """Validate optional imported candidates independently of display formats."""
    from .japanese import hira
    sources = _index(book.get("dictionary_sources", []), "$.dictionary_sources")
    for source in sources.values():
        require(source["id"] == "yomitan-" + source["sha256"], "dictionary identity disagrees with checksum", "$.dictionary_sources")
    expected = [{"file": d["file"], "sha256": d["sha256"]} for d in sources.values()]
    require(book.get("build_inputs", {}).get("yomitan", []) == expected, "dictionary input fingerprints disagree with sources", "$.build_inputs.yomitan")
    for field in ("vocabulary", "kanji"):
        for item in book[field]:
            where = f"$.{field}[{item['id']}]"
            if "definition_dictionary_id" in item:
                require(item["definition_dictionary_id"] in sources, "unknown definition dictionary", where)
            for key in ("dictionary_entries", "dictionary_metadata"):
                for record in item.get(key, []):
                    source = sources.get(record["dictionary_id"])
                    require(source is not None, "unknown dictionary reference", where+"."+key)
                    require(record["dictionary_title"] == source["index"]["title"] and record["revision"] == source["index"]["revision"],
                            "dictionary title/revision disagrees with source", where+"."+key)
                    prefix = ("term" if field == "vocabulary" else "kanji") + ("_meta" if key == "dictionary_metadata" else "")
                    require(record["bank"].startswith(prefix+"_bank_"), "wrong dictionary bank kind", where+"."+key)
                    if field == "vocabulary":
                        require(record.get("expression") in {item["word"], *item.get("aliases", [])}, "dictionary expression disagrees with word", where+"."+key)
                        if key == "dictionary_entries":
                            require(isinstance(record.get("reading"), str) and (not item["reading"] or hira(record["reading"]) == hira(item["reading"])),
                                    "dictionary reading disagrees with word", where+"."+key)
                            require(isinstance(record.get("glossary"), list) and isinstance(record.get("glossary_text"), list), "missing dictionary glossary", where+"."+key)
                    else:
                        require(record.get("character", record.get("expression")) == item["character"], "dictionary kanji disagrees with character", where+"."+key)
