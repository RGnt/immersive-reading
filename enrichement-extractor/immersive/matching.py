"""One reading/boundary policy for every vocabulary matching method."""
from bisect import bisect_right

from .japanese import hira, is_kanji, is_latin_or_digit


def _source_reading(text, rubies, start, end):
    reading, pos = "", start
    while pos < end:
        ruby = rubies.get(pos)
        if ruby:
            if ruby["end"] > end:
                return None  # A group ruby cannot be split into character readings.
            reading += ruby["reading"]
            pos = ruby["end"]
        else:
            char = text[pos]
            if is_kanji(char) or char == "々" or is_latin_or_digit(char):
                return None
            reading += char
            pos += 1
    return hira(reading)


def source_reading(chapter, start, end):
    """Return a complete source reading, or None when the EPUB lacks evidence."""
    rubies = {r["start"]: r for p in chapter["paragraphs"] for r in p["ruby"]}
    return _source_reading(chapter["text"], rubies, start, end)


def inflected_reading_compatible(actual, dictionary, conjugation):
    """Check a reading stem only when morphology establishes conjugation.

    Normalized-form/lemma identity is checked by the caller before this policy.
    This does not claim to select a semantic sense or resolve all homographs.
    """
    if "サ行変格" in conjugation and dictionary.endswith("する"):
        return actual.startswith(tuple(dictionary[:-2] + ending for ending in ("し", "せ", "さ")))
    if "サ行変格" in conjugation and dictionary.endswith("じる"):
        return actual.startswith(dictionary[:-1])
    if "カ行変格" in conjugation and dictionary.endswith("くる"):
        return actual.startswith(tuple(dictionary[:-2] + ending for ending in ("き", "こ", "く")))
    if ("一段" in conjugation and dictionary.endswith("る")) or "五段" in conjugation or (
            "形容詞" in conjugation and dictionary.endswith("い")):
        stem = dictionary[:-1]
        # Sudachi normalizes colloquial -ねえ to -ない. Restrict this alternate
        # ending to adjectives already matched by normalized identity.
        if "形容詞" in conjugation and dictionary.endswith("ない") and actual.endswith("ねえ"):
            return actual[:-2] == dictionary[:-2]
        return bool(stem) and actual.startswith(stem)
    return False


class MatchPolicy:
    def __init__(self, chapter):
        self.text = chapter["text"]
        self.rubies = {r["start"]: r for p in chapter["paragraphs"] for r in p["ruby"]}
        self.paragraphs = chapter["paragraphs"]
        self.starts = [p["start"] for p in self.paragraphs]

    def contains(self, start, end):
        if not self.paragraphs:
            return 0 <= start < end <= len(self.text)
        index = bisect_right(self.starts, start) - 1
        return index >= 0 and start < end <= self.paragraphs[index]["end"]

    def source_reading(self, start, end):
        return _source_reading(self.text, self.rubies, start, end)

    def accept(self, entry, start, end, tokens):
        """Return reading evidence for an accepted span, or None to reject it."""
        if not self.contains(start, end):
            return None
        actual = _source_reading(self.text, self.rubies, start, end)
        source = "epub" if actual is not None else "sudachi"
        if actual is None:
            # A sequence must cover the span exactly. Never borrow a larger
            # token's reading for a substring or silently skip a gap.
            pos, readings = start, []
            for token in tokens:
                if token["start"] != pos or token["end"] > end:
                    break
                original = _source_reading(self.text, self.rubies, pos, token["end"])
                readings.append(original if original is not None else token["reading"])
                if original is not None and any(is_kanji(c) or c == "々" for c in token["surface"]):
                    source = "epub_and_sudachi"
                pos = token["end"]
            if pos == end and readings:
                actual = "".join(readings)
        expected = hira(entry["reading"])
        if actual is None or not expected:
            return {"source": "unavailable", "status": "reading_unverified"}
        if actual == expected:
            return {"source": source, "status": "exact_reading", "reading": actual}
        numeric = any(t["pos"][1] == "数詞" or t["pos"][2] == "助数詞" for t in tokens)
        if numeric or (source == "sudachi" and any(is_latin_or_digit(c) for c in entry["word"] + self.text[start:end])):
            # Numeric counters and Latin spellings need pronunciation rules
            # beyond concatenating token readings. Retain the candidate with an
            # explicit diagnostic; never claim the readings were verified.
            return {"source": source, "status": "reading_unverified", "reading": actual,
                    "reason": "counter_pronunciation" if numeric else "numeral_or_latin_pronunciation"}
        changed = [t for t in tokens if (t["surface"] != t["lemma"] or t["surface"] != t["normalized"]) and t["pos"][4] != "*"]
        if self.text[start:end] != entry["word"] and changed and changed[-1]["end"] == end and inflected_reading_compatible(
                actual, expected, changed[-1]["pos"][4]):
            return {"source": source, "status": "inflected_reading", "reading": actual}
        return None
