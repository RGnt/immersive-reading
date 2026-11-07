import csv
import gzip
import hashlib
import re
import importlib.metadata
from collections import defaultdict
from pathlib import Path
from xml.etree import ElementTree as ET
from sudachipy import tokenizer
from .epub import read_epub, uid
from .japanese import hira, is_kanji
from .matching import MatchPolicy
from .validation import load_json, validate_config
from .jobs import check_cancelled
from .text_index import utf16_offsets, SentenceIndex
from . import __version__


def load_decks(paths):
    entries = {}
    for path in paths:
        with path.open(encoding="utf-8-sig", newline="") as f:
            reader = csv.DictReader(f)
            if len(reader.fieldnames or []) != len(set(reader.fieldnames or [])):
                raise ValueError(f"{path}: duplicate CSV column names")
            if not {"Word", "ReadingKana", "Definitions"} <= set(reader.fieldnames or []):
                raise ValueError(f"{path}: expected Jiten columns Word, ReadingKana, Definitions")
            for number, row in enumerate(reader, 2):
                if None in row or any(value is None for value in row.values()):
                    raise ValueError(f"{path}: malformed CSV record {number}; check the column count")
                word = row["Word"].strip()
                if not word:
                    continue
                reading = row["ReadingKana"].strip()
                eid = uid("v", (row.get("JmDictWordId") or "") + ":" + word + ":" + reading)
                if eid not in entries:
                    entries[eid] = {"id": eid, "word": word, "reading": reading,
                                    "furigana": row.get("ReadingFurigana", ""), "definitions": row["Definitions"],
                                    "jmdict_id": row.get("JmDictWordId") or None,
                                    "pitch_positions": row.get("PitchPositions", ""),
                                    "deck_occurrences": row.get("Occurences", row.get("Occurrences", "")),
                                    "frequency_rank": row.get("ReadingFrequency", ""),
                                    "sources": [], "deck_records": [], "deck_examples": [], "occurrences": []}
                entry = entries[eid]
                entry["sources"].append({"file": path.name, "csv_row": number})
                entry["deck_records"].append({"file": path.name, "csv_row": number, "fields": row})
                if row.get("ExampleSentence"):
                    entry["deck_examples"].append({"text": row["ExampleSentence"].replace("**", ""),
                                                    "marked_text": row["ExampleSentence"], "source": path.name,
                                                    "csv_row": number,
                                                    "status": "source_provided_not_verified"})
    return list(entries.values())


def load_kanjidic(path):
    if not path.exists():
        raise FileNotFoundError(f"Kanji dictionary missing: {path}. See README.md for download instructions.")
    opener = gzip.open if path.suffix == ".gz" else open
    with opener(path, "rb") as f:
        root = ET.parse(f).getroot()
    result = {}
    for e in root.findall("character"):
        def texts(query):
            return [x.text for x in e.findall(query)]
        result[e.findtext("literal")] = {
            "on": texts("reading_meaning/rmgroup/reading[@r_type='ja_on']"),
            "kun": texts("reading_meaning/rmgroup/reading[@r_type='ja_kun']"),
            "meanings": [m.text for m in e.findall("reading_meaning/rmgroup/meaning") if not m.get("m_lang")],
            "radical": e.findtext("radical/rad_value[@rad_type='classical']"),
            "strokes": e.findtext("misc/stroke_count"), "grade": e.findtext("misc/grade"),
            "source": "KANJIDIC2 / EDRDG"}
    return result


def utf16_offset(text, position):
    return len(text[:position].encode("utf-16-le")) // 2


def build_data(folder, kanjidic_path, *, cancel=None, yomitan_paths=None, worker=None):
    from .resources import resource_worker, ResourceBundle, thaw
    if worker is None:
        bundle = ResourceBundle.from_directory(Path(kanjidic_path).parent, kanjidic_path=kanjidic_path, yomitan_paths=yomitan_paths or [])
        with resource_worker(bundle) as active:
            return build_data(folder, kanjidic_path, cancel=cancel, yomitan_paths=active.store.bundle.yomitan, worker=active)
    worker.check()
    store = worker.store
    check_cancelled(cancel)
    epubs = sorted(folder.glob("*.epub"))
    decks = sorted(folder.glob("*.csv"))
    if len(epubs) != 1 or (not decks and not yomitan_paths):
        raise ValueError(f"{folder}: expected exactly one EPUB and at least one CSV deck or Yomitan dictionary")
    config_path = folder / "study.config.json"
    config = load_json(config_path) if config_path.exists() else {}
    validate_config(config, config_path)
    book = read_epub(epubs[0], config)
    book["id"] = uid("book", hashlib.sha256(epubs[0].read_bytes()).hexdigest())
    from .provenance import read_ledger, source_descriptor
    ledger = read_ledger(folder)
    book["source_files"] = [source_descriptor(p, ledger=ledger) for p in [epubs[0], *decks]]
    book["schema_version"] = "1.0.0"
    book["build_inputs"] = {
        "tool_version": __version__,
        "packages": {name: importlib.metadata.version(name) for name in ("sudachipy", "sudachidict_core")},
        "kanjidic_sha256": store.pin(store.bundle.kanjidic, cancel=cancel)["sha256"],
        "grammar_rules_sha256": store.pin(store.bundle.grammar, cancel=cancel)["sha256"],
        "book_config": config,
        "resources": store.reader_manifest(cancel=cancel),
    }
    book["language"] = "ja"
    book["gloss_language"] = "en"
    book["offset_convention"] = {"unit": "Unicode code points", "end": "exclusive",
                                 "text": "chapter.text: paragraph text concatenated; whitespace and ruby readings removed",
                                 "browser_offsets": "Each token and vocabulary occurrence also includes start_utf16/end_utf16."}
    entries = load_decks(decks)
    tok = worker
    dictionaries = []
    if yomitan_paths:
        from .yomitan import load_dictionary, vocabulary_entries
        if decks:
            forms = {form for e in entries for form in [e["word"], *config.get("aliases", {}).get(e["word"], [])]}
            term_filter = forms.__contains__
        else:
            forms = set()
            normalized_paragraphs = []
            for chapter in book["chapters"]:
                for paragraph in chapter["paragraphs"]:
                    check_cancelled(cancel)
                    for mode in (tokenizer.Tokenizer.SplitMode.A, tokenizer.Tokenizer.SplitMode.C):
                        parts = list(tok.tokenize(paragraph["text"], mode))
                        for token in parts:
                            forms.update((token.surface(), token.dictionary_form(), token.normalized_form()))
                        normalized_paragraphs += ["".join(t.dictionary_form() for t in parts),
                                                  "".join(t.normalized_form() for t in parts)]
            text = "\0".join([c["text"] for c in book["chapters"]] + normalized_paragraphs)
            forms.update(config.get("aliases", {}))
            def term_filter(word):
                # Keep literal phrases as well as inflected-token lemmas. The
                # shared aligner below still enforces reading/token boundaries.
                return word in forms or word in text
        characters = {char for c in book["chapters"] for char in c["text"] if is_kanji(char)}
        dictionaries = [load_dictionary(p, term_filter=term_filter, kanji_filter=characters, cancel=cancel) for p in yomitan_paths]
        if len({d["source"]["id"] for d in dictionaries}) != len(dictionaries):
            raise ValueError("Duplicate Yomitan archive contents supplied under different filenames; select each dictionary once")
        if not decks:
            entries = vocabulary_entries(dictionaries)
            if not any(d["source"]["bank_counts"].get("term") for d in dictionaries):
                raise ValueError("Without a CSV deck, supply a Yomitan term dictionary; metadata/kanji-only dictionaries cannot supply vocabulary")
    for entry in entries:
        check_cancelled(cancel)
        entry["context_gloss"] = config.get("context_glosses", {}).get(entry["word"])
        entry["aliases"] = config.get("aliases", {}).get(entry["word"], [])
    by_form = defaultdict(list)
    for entry in entries:
        check_cancelled(cancel)
        by_form[entry["word"]].append(entry)
        for alias in entry["aliases"]:
            by_form[alias].append(entry)
    phrase_patterns = defaultdict(list)
    for entry in entries:
        check_cancelled(cancel)
        parts = list(tok.tokenize(entry["word"], tokenizer.Tokenizer.SplitMode.A))
        signature = tuple(t.normalized_form() for t in parts)
        if signature and not any(t.is_oov() for t in parts):
            phrase_patterns[signature[0]].append((signature, entry))
    sentence_map = {}
    grammar_rules = store.grammar(cancel=cancel)
    grammar = {}
    kanji = {}
    kd = store.kanjidic(cancel=cancel)
    for chapter in book["chapters"]:
        check_cancelled(cancel)
        text = chapter["text"]
        utf16 = utf16_offsets(text)
        sentence_index = SentenceIndex(chapter["sentences"])
        for s in chapter["sentences"]:
            sentence_map[s["id"]] = s
        def sentence_at(start):
            return sentence_index.at(start)
        tokens = []
        all_spans = {}
        for paragraph in chapter["paragraphs"]:
            check_cancelled(cancel)
            offset = paragraph["start"]
            for mode in (tokenizer.Tokenizer.SplitMode.A, tokenizer.Tokenizer.SplitMode.C):
                for t in tok.tokenize(paragraph["text"], mode):
                    start, end = offset+t.begin(), offset+t.end()
                    token = {"surface": t.surface(), "lemma": t.dictionary_form(), "normalized": t.normalized_form(),
                             "reading": hira(t.reading_form()), "pos": list(t.part_of_speech()),
                             "start": start, "end": end, "oov": t.is_oov()}
                    all_spans[(start, end)] = token
                    if mode == tokenizer.Tokenizer.SplitMode.A:
                        token["start_utf16"] = utf16[start]
                        token["end_utf16"] = utf16[end]
                        tokens.append(token)
        found = defaultdict(dict)
        policy = MatchPolicy(chapter)
        reading_evidence = {}
        def accept(entry, start, end, method, matched_tokens):
            evidence = policy.accept(entry, start, end, matched_tokens)
            if evidence is not None:
                found[entry["id"]].setdefault((start, end), method)
                reading_evidence[(entry["id"], start, end)] = evidence
        for t in all_spans.values():
            for form in {t["surface"], t["lemma"], t["normalized"]}:
                for entry in by_form.get(form, []):
                    accept(entry, t["start"], t["end"], "morphology", [t])
        # Token sequences also match conjugated phrases, e.g. 気になった -> 気になる.
        signature = [t["normalized"] for t in tokens]
        for i, key in enumerate(signature):
            for pattern, entry in phrase_patterns.get(key, []):
                n = len(pattern)
                if tuple(signature[i:i+n]) == pattern:
                    accept(entry, tokens[i]["start"], tokens[i+n-1]["end"], "normalized_token_sequence", tokens[i:i+n])
        starts = {t["start"] for t in all_spans.values()}
        ends = {t["end"] for t in all_spans.values()}
        # Phrases must align to token boundaries; never accept a raw substring inside another word.
        for entry in entries:
            check_cancelled(cancel)
            if len(entry["word"]) < 2:
                continue
            for m in re.finditer(re.escape(entry["word"]), text):
                if m.start() in starts and m.end() in ends:
                    token_here = all_spans.get((m.start(), m.end()))
                    parts = [token_here] if token_here else [t for t in tokens if m.start() <= t["start"] and t["end"] <= m.end()]
                    accept(entry, m.start(), m.end(), "token_boundary_phrase", parts)
        chapter_vocab = []
        for entry in entries:
            check_cancelled(cancel)
            spans = found.get(entry["id"], {})
            if not spans:
                continue
            chapter_vocab.append(entry["id"])
            for (start, end), method in sorted(spans.items()):
                s = sentence_at(start)
                entry["occurrences"].append({"chapter_id": chapter["id"], "start": start, "end": end,
                                              "start_utf16": utf16[start], "end_utf16": utf16[end],
                                              "surface": text[start:end], "sentence_id": s["id"] if s else None,
                                              "method": method, "reading_match": reading_evidence[(entry["id"], start, end)]})
        for t in tokens:
            t["vocabulary_ids"] = [eid for eid, spans in found.items() if (t["start"], t["end"]) in spans]
        chapter["tokens"] = tokens
        chapter["vocabulary_ids"] = sorted(chapter_vocab, key=lambda eid: min(found[eid]))
        chapter_kanji = []
        for position, c in enumerate(text):
            if not is_kanji(c):
                continue
            kid = f"kanji-{ord(c):x}"
            if kid not in kanji:
                kanji[kid] = {"id": kid, "character": c, "first_chapter_id": chapter["id"],
                              "dictionary": thaw(kd.get(c)), "occurrences": [], "vocabulary_ids": []}
            k = kanji[kid]
            if kid not in chapter_kanji:
                chapter_kanji.append(kid)
            s = sentence_at(position)
            k["occurrences"].append({"chapter_id": chapter["id"], "start": position, "end": position+1,
                                      "sentence_id": s["id"] if s else None})
        chapter["kanji_ids"] = chapter_kanji
        for rule in grammar_rules:
            matches = []
            if rule.get("token") == "particle":
                matches = [(t["start"], t["end"]) for t in tokens if t["surface"] == rule["form"] and t["pos"][0] == "助詞"]
            elif rule.get("token") == "desire":
                matches = [(t["start"], t["end"]) for t in tokens if t["lemma"] == "たい" and t["pos"][0] == "助動詞"]
            else:
                matches = [(p["start"]+m.start(), p["start"]+m.end())
                           for p in chapter["paragraphs"] for m in re.finditer(rule["regex"], p["text"])]
            for start, end in matches:
                if not policy.contains(start, end):
                    continue
                s = sentence_at(start)
                if not s:
                    continue
                if rule["id"] not in grammar:
                    grammar[rule["id"]] = {k: thaw(v) for k, v in rule.items() if k not in ("regex", "token")}
                    grammar[rule["id"]].update(first_chapter_id=chapter["id"], status="rule_based_candidate", occurrences=[])
                grammar[rule["id"]]["occurrences"].append({"chapter_id": chapter["id"], "sentence_id": s["id"],
                                                            "start": start, "end": end, "surface": text[start:end]})
        chapter["new_grammar_ids"] = [g["id"] for g in grammar.values() if g["first_chapter_id"] == chapter["id"]]
    for entry in entries:
        check_cancelled(cancel)
        entry["first_chapter_id"] = entry["occurrences"][0]["chapter_id"] if entry["occurrences"] else None
        entry["status"] = "matched" if entry["occurrences"] else "unmatched_review"
        entry["example_sentence_ids"] = list(dict.fromkeys(o["sentence_id"] for o in entry["occurrences"] if o["sentence_id"]))
        for example in entry["deck_examples"]:
            needle = re.sub(r"[\s\u3000]+", "", example["text"])
            example["verified_locations"] = []
            for chapter in book["chapters"]:
                check_cancelled(cancel)
                position = chapter["text"].find(needle)
                if needle and position >= 0:
                    example["verified_locations"].append({"chapter_id": chapter["id"], "start": position, "end": position+len(needle)})
            if example["verified_locations"]:
                example["status"] = "text_verified_against_epub_not_sense_disambiguated"
        for k in kanji.values():
            if k["character"] in entry["word"] and entry["occurrences"]:
                k["vocabulary_ids"].append(entry["id"])
    if not decks:
        # Do not ship the entire dictionary or candidates rejected by alignment.
        entries = [entry for entry in entries if entry["occurrences"]]
    book["vocabulary"] = entries
    book["kanji"] = list(kanji.values())
    book["grammar"] = list(grammar.values())
    book["grammar_coverage"] = {"method": "curated finite rules; candidates require contextual review",
                                "rules_available": len(grammar_rules), "exhaustive": False,
                                "first_occurrence": "first detected occurrence in included chapters, not a claim of pedagogical introduction"}
    book["translation_policy"] = "Japanese examples only; English word/phrase glosses from deck. No story sentence translations."
    book["attribution"] = [
        {"name": "User-supplied EPUB", "scope": "Japanese text, chapter titles and ruby readings"},
        {"name": "Jiten CSV / JMdict", "url": "https://jiten.moe", "scope": "Vocabulary readings, English glosses, pitch fields and source examples"},
        {"name": "KANJIDIC2 / EDRDG", "url": "https://www.edrdg.org/wiki/KANJIDIC_Project.html", "license": "CC BY-SA 4.0", "scope": "Kanji dictionary fields"},
        {"name": "SudachiPy / SudachiDict-core", "scope": "Machine-generated segmentation, lemmas and token readings"}
    ]
    book["quality"] = {"vocabulary_total": len(entries), "vocabulary_matched": sum(bool(e["occurrences"]) for e in entries),
                        "gloss_sense_review": "Dictionary senses are retained as supplied; matches do not prove the intended sense or proper-name interpretation.",
                        "unmatched_vocabulary_ids": [e["id"] for e in entries if not e["occurrences"]],
                        "missing_kanji_dictionary_ids": [k["id"] for k in kanji.values() if not k["dictionary"]],
                        "empty_chapter_ids": [c["id"] for c in book["chapters"] if not c["text"]]}
    from .examples import select_example
    from .examples import selection_span
    from .furigana import furigana, sentence_ruby, unresolved
    missing_readings = []
    for chapter in book["chapters"]:
        check_cancelled(cancel)
        for sentence in chapter["sentences"]:
            sentence["display_ruby"] = sentence_ruby(sentence, chapter, worker=worker)
            for item in unresolved(sentence["text"], sentence["display_ruby"]):
                missing_readings.append(dict(item, chapter_id=chapter["id"], sentence_id=sentence["id"]))
    for entry in entries:
        check_cancelled(cancel)
        entry["selected_examples"] = {cid: select_example(book, next(c for c in book["chapters"] if c["id"] == cid), entry)
                                      for cid in dict.fromkeys(o["chapter_id"] for o in entry["occurrences"])}
        for example in entry["selected_examples"].values():
            span, chapter = selection_span(book, example)
            example["ruby"] = sentence_ruby(span, chapter, worker=worker) if span else furigana(example["text"], worker=worker)
    book["quality"]["unresolved_furigana"] = missing_readings
    book["furigana_policy"] = {"priority": "Original EPUB ruby, then offline Sudachi readings for missing kanji",
                               "generated_source": "SudachiPy / SudachiDict-core", "review": "Generated readings can be ambiguous, particularly names.",
                               "offsets": "display_ruby and example ruby offsets are relative Unicode code points; paragraph ruby remains original EPUB data."}
    from .kanji_meanings import add_primary_meanings
    add_primary_meanings(book, loaded=store.primary_meanings(cancel=cancel))
    if dictionaries:
        from .yomitan import attach_dictionaries
        attach_dictionaries(book, dictionaries)
        if not decks:
            languages = {d["source"]["index"].get("targetLanguage", "und") for d in dictionaries if d["source"]["bank_counts"].get("term")}
            book["gloss_language"] = next(iter(languages)) if len(languages) == 1 else "mul"
            book["translation_policy"] = "Japanese examples only; word/phrase definitions from supplied Yomitan dictionaries. No story sentence translations."
            book["attribution"] = [a for a in book["attribution"] if a["name"] != "Jiten CSV / JMdict"]
    from .ambiguity import annotate_ambiguity
    annotate_ambiguity(book)
    return book
