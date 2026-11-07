"""Preserve EPUB ruby and fill missing kanji readings using offline Sudachi."""
import re
from functools import lru_cache

KANJI = re.compile(r"[\u3400-\u9fff\uf900-\ufaff\U00020000-\U000323af々〆〇]")


def hira(text):
    return "".join(chr(ord(c)-0x60) if "ァ" <= c <= "ヶ" else c for c in text)


def analyzer():
    """Historical adapter; tokenizer state is local to the current thread."""
    from .tokenization import default_worker
    return default_worker()


def align(surface, reading, originals):
    """Align kana anchors; keep inflectional kana outside the ruby base."""
    by_start = {r[0]: r for r in originals}
    units, pos = [], 0
    while pos < len(surface):
        if pos in by_start:
            start, end, base, kana = by_start[pos]
            units.append((start, end, hira(kana), False))
            pos = end
        elif KANJI.fullmatch(surface[pos]):
            end = pos+1
            while end < len(surface) and KANJI.fullmatch(surface[end]) and end not in by_start:
                end += 1
            units.append((pos, end, None, True))
            pos = end
        else:
            units.append((pos, pos+1, hira(surface[pos]), False))
            pos += 1

    @lru_cache(maxsize=None)
    def match(index, offset):
        if index == len(units):
            return () if offset == len(reading) else None
        start, end, fixed, generated = units[index]
        if fixed is not None:
            if reading.startswith(fixed, offset):
                return match(index+1, offset+len(fixed))
            return None
        for stop in range(offset+1, len(reading)+1):
            suffix = match(index+1, stop)
            if suffix is not None:
                return ((start, end, surface[start:end], reading[offset:stop], "sudachi"),)+suffix
        return None
    return match(0, 0)


def _furigana(text, originals, worker):
    result = [(start, end, base, reading, "epub") for start, end, base, reading in originals]
    for token in worker.tokenize(text, "C"):
        surface = token.surface()
        if not KANJI.search(surface):
            continue
        start, end = token.begin(), token.end()
        overlapping = [r for r in originals if r[0] < end and start < r[1]]
        if any(r[0] < start or r[1] > end for r in overlapping):
            continue  # A source ruby can span several tokenizer units.
        reading = hira(token.reading_form())
        if not re.fullmatch(r"[ぁ-ゖーゝゞ]+", reading):
            continue  # Unknown names/characters are reported, never guessed.
        relative = [(a-start, b-start, base, kana) for a, b, base, kana in overlapping]
        aligned = align(surface, reading, relative)
        if aligned is not None:
            result.extend((a+start, b+start, base, kana, origin) for a,b,base,kana,origin in aligned)
        elif not overlapping:
            # Irregular spellings can be annotated as a whole word.
            result.append((start, end, surface, reading, "sudachi"))
    return tuple(sorted(result))


def furigana(text, originals=(), *, worker=None):
    worker = worker or analyzer()
    source = tuple(sorted((r["start"], r["end"], r["surface"], r["reading"]) for r in originals))
    return [dict(start=a, end=b, surface=base, reading=kana, source=origin)
            for a,b,base,kana,origin in worker.ruby(text, source)]


def sentence_ruby(sentence, chapter, *, worker=None):
    source = [dict(start=r["start"]-sentence["start"], end=r["end"]-sentence["start"],
                   surface=r["surface"], reading=r["reading"])
              for paragraph in chapter["paragraphs"] for r in paragraph["ruby"]
              if sentence["start"] <= r["start"] and r["end"] <= sentence["end"]]
    return furigana(sentence["text"], source, worker=worker)


def unresolved(text, ruby):
    return [dict(start=m.start(), surface=m.group()) for m in KANJI.finditer(text)
            if not any(r["start"] <= m.start() < r["end"] for r in ruby)]
