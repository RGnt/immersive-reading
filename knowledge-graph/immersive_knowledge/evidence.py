"""Locate a quote in chapter text. Positions are computed by code, never trusted from the model (spec 18, FR-1/FR-12).

Matching ignores brackets and sentence-end marks at the edges of the quote, whitespace (including the ideographic space) and compares text after NFKC normalisation, so a model that
re-flows a line or turns a half-width digit into a full-width one still verifies. Returned offsets always refer to the
**original** text. If the quote occurs more than once the **last** occurrence is used: that is the conservative choice for
spoiler safety, because the fact then becomes visible later, never earlier.
"""
import unicodedata
from dataclasses import dataclass

MIN_QUOTE_CHARS = 4
MAX_QUOTE_CHARS = 400
# Models habitually add or drop brackets and sentence-end marks at the edges of a quote ("お帰りなさい。" quoted as "「お帰りなさい」").
# Only the edges are ignored: the body of the quote must still match the text exactly.
EDGE_PUNCTUATION = "「」『』（）()［］[]【】〈〉《》“”\"'。、，．,.!?！？…‥・:：;；"


@dataclass(frozen=True)
class Located:
    start: int
    end: int
    occurrences: int


def normalize(text):
    """Normalised string plus, for each normalised character, the index of the original character it came from."""
    chars, origin = [], []
    for index, ch in enumerate(text):
        if ch.isspace():
            continue
        for piece in unicodedata.normalize("NFKC", ch):
            chars.append(piece)
            origin.append(index)
    return "".join(chars), origin


def locate(text, quote, *, min_chars=MIN_QUOTE_CHARS, normalized=None):
    """Return ``Located`` or ``None`` when the quote is absent, too short or too long.

    ``normalized`` lets a caller reuse ``normalize(text)`` for many quotes of the same chapter.
    """
    needle = normalize(quote)[0].strip(EDGE_PUNCTUATION)
    if len(needle) < min_chars or len(needle) > MAX_QUOTE_CHARS:
        return None
    haystack, origin = normalized if normalized is not None else normalize(text)
    count, last = 0, -1
    at = haystack.find(needle)
    while at >= 0:
        count += 1
        last = at
        at = haystack.find(needle, at + 1)
    if count == 0:
        return None
    start = origin[last]
    end = origin[last + len(needle) - 1] + 1
    return Located(start, end, count)


# ---------------------------------------------------------------------------------------------------------- word search
# Japanese has no spaces, so "is this name a word of its own here?" is decided by script: a name embedded in a longer run of the
# same script ("ギル" inside "ギルド", "リク" inside "リクエスト") is a different word, while a name followed by another script
# ("クマさん", "リクが") or by an honorific ("青木先生") is the name itself.
HONORIFICS = ("先生", "様", "君", "殿", "氏", "嬢", "姫", "王子", "師匠")


def char_class(c):
    if "゠" <= c <= "ヿ":
        return "katakana"
    if "぀" <= c <= "ゟ":
        return "hiragana"
    if "一" <= c <= "鿿" or "㐀" <= c <= "䶿" or c == "々":
        return "kanji"
    if c.isascii() and c.isalnum():
        return "latin"
    return "other"


def find_word(haystack, needle, start=0):
    """Index of the first occurrence of ``needle`` that is not embedded in a longer word of the same script, else -1.

    Both arguments must already be normalised (``normalize(...)[0].casefold()``).
    """
    if not needle:
        return -1
    first, last = char_class(needle[0]), char_class(needle[-1])
    at = haystack.find(needle, start)
    while at >= 0:
        end = at + len(needle)
        before_ok = at == 0 or first == "other" or char_class(haystack[at - 1]) != first
        after = haystack[end:end + 1]
        after_ok = (not after or last == "other" or char_class(after) != last
                    or (last == "kanji" and haystack[end:].startswith(HONORIFICS)))
        if before_ok and after_ok:
            return at
        at = haystack.find(needle, at + 1)
    return -1
