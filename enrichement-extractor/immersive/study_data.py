"""Plain study-card data, shared indexes and first-appearance grouping.

This module never imports HTML/PDF renderers or generates markup.
"""
from dataclasses import dataclass, field
from .examples import select_example, selection_span
from .furigana import furigana, sentence_ruby
from .jobs import check_cancelled


@dataclass
class StudyIndexes:
    sentences: dict
    vocabulary: dict
    kanji: dict
    groups: dict
    grammar_by_chapter: dict
    example_rubies: dict = field(default_factory=dict)

    @classmethod
    def from_book(cls, book):
        order = {c["id"]: i for i, c in enumerate(book["chapters"])}
        grammar = {cid: [] for cid in order}
        firsts = [(g, min(g["occurrences"], key=lambda o: (order[o["chapter_id"]], o["start"], o["end"])))
                  for g in book["grammar"]]
        for entry, occurrence in sorted(firsts, key=lambda pair: (
                order[pair[1]["chapter_id"]], pair[1]["start"], pair[1]["end"], pair[0]["id"])):
            grammar[occurrence["chapter_id"]].append((entry, occurrence))
        examples = {example_key(s): s["ruby"] for v in book["vocabulary"] for s in v.get("selected_examples", {}).values() if "ruby" in s}
        return cls(sentence_index(book), {v["id"]: v for v in book["vocabulary"]},
                   {k["id"]: k for k in book["kanji"]}, grouped_vocabulary(book), grammar, examples)


@dataclass
class StudyData:
    book_id: str
    indexes: StudyIndexes
    vocabulary: dict
    kanji: dict
    grammar: dict
    learning_sets: list
    context: dict | None

    def learning_set_records(self):
        return [{"id": g["id"], "number": g["number"], "size": len(g["cards"]),
                 "words": [{"card_id": c["id"], "word": c["title"], "reading": c["reading"],
                            "chapter_id": c["chapter_id"], "study_number": c["study_number"]} for c in g["cards"]]}
                for g in self.learning_sets]


def prepare_study_data(book, *, include_context=True, cancel=None, worker=None):
    """Prepare once per book; consumers must treat the result as read-only.

    The book is not modified. Indexes reference source records rather than
    copying the full enrichment bundle. Rebuild this model if its inputs change.
    """
    if worker is None:
        from .resources import ResourceBundle, resource_worker
        with resource_worker(ResourceBundle()) as active:
            return prepare_study_data(book, include_context=include_context, cancel=cancel, worker=active)
    worker.check()
    indexes = StudyIndexes.from_book(book)
    vocabulary, kanji, grammar = {}, {}, {}
    for chapter in book["chapters"]:
        check_cancelled(cancel)
        cid = chapter["id"]
        vocabulary[cid] = vocab_cards(book, chapter, indexes=indexes, worker=worker)
        kanji[cid] = kanji_cards(book, chapter, indexes=indexes, worker=worker)
        grammar[cid] = grammar_cards(book, chapter, indexes=indexes, worker=worker)
    sets = vocabulary_learning_sets(book, indexes=indexes, cards_by_chapter=vocabulary)
    numbered = {c["id"]: c for group in sets for c in group["cards"]}
    vocabulary = {cid: [numbered[c["id"]] for c in cards] for cid, cards in vocabulary.items()}
    context = None
    if include_context:
        from .context import contextual_cards
        context = {ch["id"]: contextual_cards(book, ch, indexes=indexes, cards=kanji[ch["id"]], cancel=cancel, worker=worker) for ch in book["chapters"]}
    return StudyData(book["id"], indexes, vocabulary, kanji, grammar, sets, context)


def study_reader_data(book, study):
    """Return an export view with study metadata without mutating enrichment."""
    if study.book_id != book["id"]:
        raise ValueError("Study data belongs to another book")
    if study.context is None:
        raise ValueError("Study export requires preparation with include_context=True")
    notes = [{"kanji": c["title"], "chapter_id": cid, "note": c["source_reading_note"]}
             for cid, cards in study.context.items() for c in cards if c.get("source_reading_note")]
    return dict(book, learning_sets=study.learning_set_records(), quality=dict(book["quality"], context_source_notes=notes))


def example_highlight(sentence, occurrence):
    """A display-only span relative to plain_example; None for no highlight."""
    if sentence is None or occurrence is None:
        return None
    return {"start": occurrence["start"]-sentence["start"], "end": occurrence["end"]-sentence["start"]}


def sentence_index(book):
    return {s["id"]: (s, c) for c in book["chapters"] for s in c["sentences"]}


def source_label(s):
    groups = {}
    for p in s["sources"]:
        groups.setdefault(p["href"], []).append(p["paragraph_index"]+1)
    return " · ".join(f'{href} · p. {min(ns)}' + (f'-{max(ns)}' if min(ns) != max(ns) else '') for href, ns in groups.items())


def example_ruby(s, chapter, *, worker=None):
    if "display_ruby" in s:
        return s["display_ruby"]
    # CSV excerpts often reuse a span of a saved sentence. Preserve readings
    # made with the enrichment worker's dictionaries rather than reanalyze it.
    containers = [sentence for sentence in chapter.get("sentences", [])
                  if sentence["start"] <= s["start"] and s["end"] <= sentence["end"] and "display_ruby" in sentence]
    if containers:
        sentence = containers[0]
        delta = sentence["start"] - s["start"]
        return [dict(r, start=r["start"]+delta, end=r["end"]+delta) for r in sentence["display_ruby"]
                if 0 <= r["start"]+delta < r["end"]+delta <= len(s["text"])]
    return sentence_ruby(s, chapter, worker=worker)


def example_key(selection):
    return tuple(selection.get(key) for key in ("text", "source", "source_file", "csv_row", "chapter_id", "start", "end"))


def selected_example_ruby(book, selection, indexes, *, worker=None):
    ruby = indexes.example_rubies.get(example_key(selection)) if indexes is not None else None
    if ruby is not None:
        return ruby
    span, chapter = selection_span(book, selection)
    return example_ruby(span, chapter, worker=worker) if span else furigana(selection["text"], worker=worker)


def grouped_vocabulary(book):
    """One study card per spelling; preserve each dictionary record in reader JSON."""
    groups = {}
    order = {c["id"]: i for i, c in enumerate(book["chapters"])}
    for entry in book["vocabulary"]:
        groups.setdefault(entry["word"], []).append(entry)
    combined = {}
    for word, members in groups.items():
        # Rejected homographs remain searchable in reader data, but cannot
        # contribute a reading/gloss to a matched spelling's study card.
        members = [v for v in members if v["occurrences"]] or members
        occurrences = sorted((o for v in members for o in v["occurrences"]),
                             key=lambda o: (order[o["chapter_id"]], o["start"], o["end"]))
        owner = occurrences[0]["chapter_id"] if occurrences else None
        representative = next((v for v in members if any(o["chapter_id"] == owner for o in v["occurrences"])), members[0])
        entry = dict(representative)
        entry.update(first_chapter_id=owner,
                     reading=" / ".join(dict.fromkeys(v["reading"] for v in members if v["reading"])),
                     definitions=" / ".join(dict.fromkeys(v.get("context_gloss") or v["definitions"] for v in members)),
                     context_gloss=None,
                     deck_examples=[e for v in members for e in v["deck_examples"]],
                     occurrences=list({(o["chapter_id"], o["start"], o["end"]): o for o in occurrences}.values()),
                     dictionary_entries=list({(d["dictionary_id"], d["bank"], d["row"]): d for v in members for d in v.get("dictionary_entries", [])}.values()),
                     dictionary_metadata=list({(d["dictionary_id"], d["bank"], d["row"]): d for v in members for d in v.get("dictionary_metadata", [])}.values()))
        entry["definition_candidates"] = list({d["id"]: d for v in members for d in v.get("definition_candidates", [])}.values())
        combined[word] = entry
    return combined


def vocabulary_learning_sets(book, size=20, *, indexes=None, cards_by_chapter=None):
    if size < 1:
        raise ValueError("Learning-set size must be positive")
    indexes = indexes or StudyIndexes.from_book(book)
    cards = []
    for chapter in book["chapters"]:
        for card in (cards_by_chapter[chapter["id"]] if cards_by_chapter is not None else vocab_cards(book, chapter, indexes=indexes)):
            cards.append(dict(card, chapter_id=chapter["id"], chapter_title=chapter["title"]))
    for i, card in enumerate(cards):
        card.update(study_number=i+1, set_number=i//size+1)
    return [{"id": f"set-{i//size+1:03d}", "number": i//size+1, "cards": cards[i:i+size]}
            for i in range(0, len(cards), size)]


def vocab_cards(book, chapter, *, indexes=None, worker=None):
    indexes = indexes or StudyIndexes.from_book(book)
    entries = indexes.vocabulary
    groups = indexes.groups
    cards = []
    seen = set()
    first_positions = {}
    for eid in chapter["vocabulary_ids"]:
        v = groups[entries[eid]["word"]]
        if v["first_chapter_id"] != chapter["id"] or v["word"] in seen:
            continue
        seen.add(v["word"])
        occurrences = [o for o in v["occurrences"] if o["chapter_id"] == chapter["id"]]
        first_positions[v["id"]] = min((o["start"], o["end"]) for o in occurrences)
        selection = select_example(book, chapter, v, occurrences)
        s, c = selection_span(book, selection)
        highlight = next((o for o in v["occurrences"] if s and o["chapter_id"] == c["id"] and s["start"] <= o["start"] and o["end"] <= s["end"]), None)
        plain = selection["text"]
        source = ("CSV · " if selection["source"] == "csv" else "First chapter occurrence · ") + str(selection.get("source_file") or "")
        cards.append({"id": v["id"], "title": v["word"], "reading": v["reading"], "meaning": v["definitions"],
                      "meta": f'First appearance · {len(occurrences)} matched occurrence(s)',
                      "example_highlight": example_highlight(s, highlight), "plain_example": plain, "source": source,
                      "kind": "vocabulary", "details": "", "ruby": selected_example_ruby(book, selection, indexes, worker=worker),
                      "example_provenance": selection,
                      "dictionary_entries": v.get("dictionary_entries", []),
                      "dictionary_metadata": v.get("dictionary_metadata", [])})
        cards[-1].update(definition_candidates=v.get("definition_candidates", []), sense_selection=v.get("sense_selection"))
    return sorted(cards, key=lambda card: (*first_positions[card["id"]], card["id"]))


def kanji_cards(book, chapter, *, indexes=None, worker=None):
    indexes = indexes or StudyIndexes.from_book(book)
    kanji = indexes.kanji
    vocab = indexes.vocabulary
    index = indexes.sentences
    cards = []
    for kid in chapter["kanji_ids"]:
        k = kanji[kid]
        if k["first_chapter_id"] != chapter["id"]:
            continue
        d = k["dictionary"] or {}
        occurrence = next((o for o in k["occurrences"] if o["chapter_id"] == chapter["id"] and o["sentence_id"]), None)
        plain = source = ""
        if occurrence:
            s, c = index[occurrence["sentence_id"]]
            plain, source = s["text"], source_label(s)
        related = [vocab[e] for e in k["vocabulary_ids"] if e in chapter["vocabulary_ids"]][:4]
        compounds = " / ".join(f'{v["word"]}【{v["reading"]}】' for v in related)
        current = next((f'{r["surface"]}【{r["reading"]}】' for para in chapter["paragraphs"] for r in para["ruby"]
                        if occurrence and r["start"] <= occurrence["start"] < r["end"]), "")
        cards.append({"id": kid, "kind": "kanji", "title": k["character"],
                      "reading": "音 " + "・".join(d.get("on", [])) + "　訓 " + "・".join(d.get("kun", [])),
                      "meaning": "; ".join(d.get("primary_meanings", d.get("meanings", []))),
                      "primary_meanings": d.get("primary_meanings", d.get("meanings", [])),
                      "meaning_source": d.get("meaning_source", {"name": "KANJIDIC2 / EDRDG"}),
                      "meta": f'First appearance · {d.get("strokes", "?")} strokes · radical {d.get("radical", "?")} · grade {d.get("grade") or "unlisted"}',
                      "details": "Meaning source: " + d.get("meaning_source", {}).get("name", "KANJIDIC2 / EDRDG") + ". " + compounds,
                      "example_highlight": example_highlight(s, occurrence) if occurrence else None, "plain_example": plain, "source": source,
                      "on": d.get("on", []), "kun": d.get("kun", []), "current_reading": current,
                      "stroke_order": k.get("stroke_order"),
                      "dictionary_entries": k.get("dictionary_entries", []),
                      "dictionary_metadata": k.get("dictionary_metadata", []),
                      "ruby": example_ruby(s, c, worker=worker) if occurrence else []})
    return cards


def grammar_cards(book, chapter, *, indexes=None, worker=None):
    indexes = indexes or StudyIndexes.from_book(book)
    index = indexes.sentences
    result = []
    for g, o in indexes.grammar_by_chapter[chapter["id"]]:
        s, c = index[o["sentence_id"]]
        result.append({"id": g["id"], "kind": "grammar", "title": g["label"], "reading": "",
                       "meaning": g["meaning"], "details": g["note"],
                       "meta": f'First detected here · {len(g["occurrences"])} candidate(s) in book · review in context',
                       "example_highlight": example_highlight(s, o), "plain_example": s["text"], "source": source_label(s), "ruby": example_ruby(s, c, worker=worker)})
    return result
