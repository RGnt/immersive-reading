"""Automatic source-grounded kanji context; optional declarative book corrections."""
import re
from .japanese import hira
from .examples import select_example, selection_span
from .matching import MatchPolicy, source_reading
from .jobs import check_cancelled


def compatible_reading(actual, dictionary):
    dictionary = hira(dictionary)
    if actual == dictionary:
        return True
    if not actual or not dictionary.endswith(tuple("るうくぐすつぬぶむい")):
        return False
    if dictionary.endswith("する"):
        return actual.startswith(dictionary[:-2]+"し")
    if dictionary == "くる":
        return actual.startswith("き")
    return actual.startswith(dictionary[:-1])


def deck_glosses(value):
    parts, start, depth = [], 0, 0
    for i, char in enumerate(value):
        if char == "(": depth += 1
        elif char == ")": depth = max(0, depth-1)
        elif char == "," and depth == 0:
            parts.append(value[start:i].strip()); start = i+1
    return parts+[value[start:].strip()]


def contextual_cards(book, chapter, *, indexes=None, cards=None, cancel=None, worker=None):
    from .study_data import kanji_cards, example_highlight, selected_example_ruby
    if cards is None:
        cards = kanji_cards(book, chapter, indexes=indexes, worker=worker)
    policy = MatchPolicy(chapter)
    result = []
    config = book.get("build_inputs", {}).get("book_config", {}).get("print_context", {}).get(chapter["id"], {})
    for card in cards:
        check_cancelled(cancel)
        char, choices = card["title"], []
        override = config.get(char, {})
        for entry in book["vocabulary"]:
            for o in entry["occurrences"]:
                if o["chapter_id"] != chapter["id"] or char not in o["surface"]:
                    continue
                actual = policy.source_reading(o["start"], o["end"])
                if actual == hira(entry["reading"]) or (o["surface"] != entry["word"] and compatible_reading(actual, entry["reading"])):
                    rank = (entry["word"] != char, o["surface"] != entry["word"], o["start"], len(entry["word"]))
                    choices.append((rank, entry, o, "aligned_deck_word"))
                elif actual is None and entry["reading"] and not any(
                        r["start"] < o["end"] and o["start"] < r["end"]
                        for paragraph in chapter["paragraphs"] for r in paragraph["ruby"]):
                    # The aligner already checked token boundaries and readings.
                    # Books without ruby can use the deck reading without
                    # pretending it was explicitly supplied by the EPUB.
                    label = "Yomitan dictionary" if entry.get("vocabulary_source") == "yomitan" else "CSV"
                    adjusted = dict(entry, source_reading_note=f"Reading from {label}; EPUB does not supply furigana for this aligned word.")
                    rank = (entry["word"] != char, o["surface"] != entry["word"], o["start"], len(entry["word"]))
                    choices.append((rank, adjusted, o, "aligned_deck_word_csv_reading"))
        # Deterministic exact spelling/ruby matching handles items inside compounds
        # that the tokenizer treats as one larger word. No model or network call.
        if not choices or override:
            for entry in book["vocabulary"]:
                surface = override.get("surface", entry["word"]) if override.get("word") == entry["word"] else entry["word"]
                if not surface or char not in surface:
                    continue
                for match in re.finditer(re.escape(surface), chapter["text"]):
                    if not policy.contains(match.start(), match.end()):
                        continue
                    actual = policy.source_reading(match.start(), match.end())
                    if not actual:
                        continue
                    same = actual == hira(entry["reading"]) or (surface != entry["word"] and compatible_reading(actual, entry["reading"]))
                    # Do not reintroduce a rejected homograph by silently
                    # changing its reading while retaining its dictionary gloss.
                    # A reviewed word override may explicitly authorize it.
                    if not same and override.get("word") != entry["word"]:
                        continue
                    if not same and surface != entry["word"]:
                        continue
                    if not same and not any(t["start"] == match.start() and t["end"] == match.end() for t in chapter.get("tokens", [])):
                        continue
                    adjusted = entry
                    if not same:
                        adjusted = dict(entry, reading=actual, source_reading_note=f'Book ruby {actual}; CSV has {entry["reading"]}. Book reading retained.')
                    sentence = next(s for s in chapter["sentences"] if s["start"] <= match.start() < s["end"])
                    o = {"chapter_id": chapter["id"], "start": match.start(), "end": match.end(), "sentence_id": sentence["id"]}
                    choices.append(((entry["word"] != char, not same, match.start(), len(surface)), adjusted, o, "exact_surface_and_book_ruby"))
        preferred = [c for c in choices if c[1]["word"] == override.get("word")]
        if preferred:
            choices = preferred
        note = ""
        scope = "chapter_word"
        retained = []
        context_selection = {"status": "character_fallback", "selected_entry_id": None,
                             "display_entry_id": None, "candidate_entry_ids": []}
        if choices and not (override.get("definitions") and override.get("source")):
            reviewed_choices = [c for c in choices if override.get("gloss") and override["gloss"] in (c[1].get("context_gloss") or c[1]["definitions"])]
            _, entry, occurrence, method = min(reviewed_choices or choices, key=lambda c:(c[0], c[1]["id"]))
            same_span = [c for c in choices if c[2]["start"] == occurrence["start"] and c[2]["end"] == occurrence["end"]]
            unique = {c[1]["id"]: c for c in same_span}
            retained = [{"entry_id": candidate["id"], "word": candidate["word"], "reading": candidate["reading"],
                         "definitions": candidate["definitions"], "start": o["start"], "end": o["end"],
                         "reading_evidence": o.get("reading_match"), "method": alignment,
                         "definition_candidates": candidate.get("definition_candidates", [])}
                        for _, candidate, o, alignment in sorted(unique.values(), key=lambda c:c[1]["id"])]
            context_selection = {"status": "ambiguous" if len(retained) > 1 else "display_candidate",
                                 "selected_entry_id": None, "display_entry_id": entry["id"],
                                 "candidate_entry_ids": sorted(unique),
                                 "reason": "Study example ordering is a display heuristic, not contextual sense resolution."}
            senses = deck_glosses(entry.get("context_gloss") or entry["definitions"])
            gloss = override.get("gloss", senses[0])
            if gloss not in (entry.get("context_gloss") or entry["definitions"]):
                gloss = senses[0]; note = "Configured gloss was not present in the deck; source gloss retained."
            note = entry.get("source_reading_note", note)
            gloss_status = "reviewed_override" if override.get("gloss") == gloss or entry.get("context_gloss") else "dictionary_gloss_excerpt_not_sense_disambiguated"
            selection = select_example(book, chapter, entry, entry.get("occurrences", [])+[occurrence])
            word, reading, eid, definitions, sources = entry["word"], entry["reading"], entry["id"], entry["definitions"], entry["sources"]
            if len(retained) > 1 and not override.get("gloss") and not entry.get("context_gloss"):
                word = " / ".join(dict.fromkeys(c["word"] for c in retained))
                reading = " / ".join(dict.fromkeys(c["reading"] for c in retained))
                definitions = "\n".join(dict.fromkeys(c["definitions"] for c in retained))
                sources = [source for _, candidate, _, _ in unique.values() for source in candidate["sources"]]
                gloss = " / ".join(dict.fromkeys(deck_glosses(c["definitions"])[0] for c in retained))
                gloss_status, eid = "ambiguous_dictionary_candidates", None
            elif gloss_status == "reviewed_override":
                context_selection["status"] = "reviewed_gloss"
                # A word-level reviewed gloss still does not resolve competing
                # readings/entries at this occurrence.
                reviewed_ids = {c[1]["id"] for c in reviewed_choices}
                context_selection["selected_entry_id"] = entry["id"] if len(retained) == 1 or len(reviewed_ids) == 1 else None
        else:
            # Missing deck context never blocks a new book. A general character
            # meaning is labelled explicitly instead of inventing a word gloss.
            k = indexes.kanji[card["id"]] if indexes is not None else next(k for k in book["kanji"] if k["id"] == card["id"])
            occurrence = next(o for o in k["occurrences"] if o["chapter_id"] == chapter["id"])
            occurrence = dict(occurrence)
            ruby = next((r for p in chapter["paragraphs"] for r in p["ruby"] if r["start"] <= occurrence["start"] < r["end"]), None)
            word, reading = (ruby["surface"], ruby["reading"]) if ruby else (char, "Reading not supplied")
            definitions = "; ".join((k.get("dictionary") or {}).get("meanings", [])) or "Meaning not supplied"
            gloss = deck_glosses(definitions)[0]
            sources = [{"name":"KANJIDIC2", "reading_source":"EPUB ruby" if ruby else "unavailable"}]
            eid, method, scope = None, "character_dictionary_fallback", "character_dictionary"
            gloss_status = "general_character_meaning"
            note = "No aligned CSV word. Meaning is a general character meaning, not a contextual word translation."
            if override.get("definitions") and override.get("source"):
                definitions = gloss = override["definitions"]
                word, reading = override.get("surface", word), source_reading(chapter, chapter["text"].index(override["surface"]), chapter["text"].index(override["surface"])+len(override["surface"])) if override.get("surface") else reading
                sources, scope, method = [override["source"]], "chapter_word", "configured_supplement"
                gloss_status = "reviewed_supplement"
                context_selection["status"] = "reviewed_supplement"
                note = "Supplementary word gloss from configured source; not in CSV."
            selection = select_example(book, chapter, {"deck_examples":[],"occurrences":[occurrence]})
        span, example_chapter = selection_span(book, selection)
        highlight = occurrence if span and example_chapter["id"] == chapter["id"] and span["start"] <= occurrence["start"] < occurrence["end"] <= span["end"] else None
        result.append(dict(card, context_word=word, context_reading=reading, context_gloss=gloss,
                           context_gloss_scope=scope, context_entry_id=eid, context_definitions=definitions,
                           context_gloss_status=gloss_status,
                           context_candidates=retained, context_selection=context_selection,
                           context_alignment=method, context_sources=sources,
                           context_surface=chapter["text"][occurrence["start"]:occurrence["end"]], source_reading_note=note,
                           plain_example=selection["text"], ruby=selected_example_ruby(book, selection, indexes, worker=worker),
                           example_highlight=example_highlight(span, highlight),
                           context_gloss_provider="YOMITAN" if book.get("dictionary_sources") and not any(f["file"].endswith(".csv") for f in book["source_files"]) else "DECK",
                           example_provenance=selection))
    return result
