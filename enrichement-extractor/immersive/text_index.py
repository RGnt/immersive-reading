"""Reusable indexes over normalized text; unrelated to original-DOM mapping."""
from bisect import bisect_right


def utf16_offsets(text):
    offsets = [0]
    for character in text:
        offsets.append(offsets[-1] + (2 if ord(character) > 0xFFFF else 1))
    return offsets


class SentenceIndex:
    def __init__(self, sentences):
        self.sentences = sentences
        self.starts = [s["start"] for s in sentences]

    def at(self, position):
        index = bisect_right(self.starts, position) - 1
        if index >= 0 and position < self.sentences[index]["end"]:
            return self.sentences[index]
        return None
