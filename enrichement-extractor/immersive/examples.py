"""Deterministic CSV-first examples, with a first-chapter-occurrence fallback."""
import re
from .epub import norm


def select_example(book, chapter, entry, occurrences=None):
    occurrences = occurrences if occurrences is not None else entry.get("occurrences", [])
    local = sorted((o for o in occurrences if o["chapter_id"] == chapter["id"] and o.get("sentence_id")),
                   key=lambda o: (o["start"], o["end"]))
    for example in entry.get("deck_examples", []):
        raw = example.get("text", "").replace("**", "").strip()
        if not raw:
            continue
        needle = norm(raw)
        # Prefer a matching location in this chapter, then anywhere in this book.
        for context in [chapter] + [c for c in book["chapters"] if c["id"] != chapter["id"]]:
            start = context["text"].find(needle)
            if start >= 0:
                return {"text": needle, "source": "csv", "source_file": example.get("source"),
                        "csv_row": example.get("csv_row"), "chapter_id": context["id"],
                        "start": start, "end": start+len(needle), "status": "text_verified_against_epub"}
        # A supplied CSV example is still source material even if alignment fails.
        return {"text": raw, "source": "csv", "source_file": example.get("source"),
                "csv_row": example.get("csv_row"), "chapter_id": None,
                "start": None, "end": None, "status": "source_provided_not_aligned"}
    if local:
        first = local[0]
        sentence = next(s for s in chapter["sentences"] if s["id"] == first["sentence_id"])
        return {"text": sentence["text"], "source": "epub_first_occurrence", "source_file": chapter["href"],
                "chapter_id": chapter["id"], "sentence_id": sentence["id"], "start": sentence["start"],
                "end": sentence["end"], "status": "first_occurrence_in_chapter"}
    return {"text": "", "source": "unavailable", "chapter_id": None, "start": None, "end": None,
            "status": "no_csv_example_or_aligned_occurrence"}


def selection_span(book, selection):
    """A ruby-compatible span, which may be a CSV excerpt of a longer sentence."""
    if selection.get("start") is None:
        return None, None
    chapter = next(c for c in book["chapters"] if c["id"] == selection["chapter_id"])
    span = {"text": selection["text"], "start": selection["start"], "end": selection["end"], "sources": []}
    return span, chapter
