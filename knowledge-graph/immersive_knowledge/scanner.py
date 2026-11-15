"""Hidden-name scanner: the last line of defence against a leak through free text (spec 18, section 5, layer 4).

A summary written by the model can mention a name the reader has not met yet (``"青木先生 hides behind the mask"``) even
though every structured fact is gated correctly. Before any rendered text leaves the builder it is searched for every name
that is **not yet visible** at the reader's position. Names that *are* visible are masked first, so a hidden name that is
a substring of a visible one ("先生" inside "青木先生") does not trigger a false alarm. The check is deliberately
conservative: a hit blocks the text, it is never silently cleaned. A name only counts as found where it is a word of its own (see
``evidence.find_word``): "ギル" does not match inside "ギルド", "青木" does match in "青木先生".
"""
from .evidence import find_word, normalize

MIN_NAME_CHARS = 2
MIN_READING_CHARS = 3


class SpoilerLeak(Exception):
    def __init__(self, label, hits):
        self.label, self.hits = label, tuple(hits)
        super().__init__(f"{label}: contains names that are not visible yet: {', '.join(self.hits)}")


def _key(text):
    return normalize(text)[0].casefold()


def hidden_names(store, view):
    """Raw names (any type) the reader has not met at ``view.position``.

    A name is hidden if it is not visible (no entity of the reader's knowledge carries it) **and** it has not yet occurred in the
    text the reader has read: a place called "クマ" that the model only turned into an entity in chapter 19 is no secret in
    chapter 3 when the word has been on every page. ``seen_key`` is unknown (NULL) for names whose sighting was not indexed; those
    fall back to the strict rule (hidden until the entity is visible).
    """
    visible = {_key(n["name"]) for e in view.entities.values() for n in e.names}
    hidden = {}
    for row in store.query("SELECT name, type FROM names WHERE from_key >= ? AND COALESCE(seen_key, from_key) >= ? ORDER BY id",
                           (view.position.key, view.position.key)):
        key = _key(row["name"])
        floor = MIN_READING_CHARS if row["type"] == "reading" else MIN_NAME_CHARS
        if len(key) >= floor and key not in visible:
            hidden.setdefault(key, row["name"])
    return hidden            # normalised key -> raw name (for reporting)


class ViewScanner:
    """The scanner of one view, prepared once: use it to scan many texts at the same position (story cards)."""

    def __init__(self, store, view):
        self._visible = sorted({_key(n["name"]) for e in view.entities.values() for n in e.names if _key(n["name"])}, key=len, reverse=True)
        self._hidden = hidden_names(store, view)

    def scan(self, text):
        masked = _key(text)
        for name in self._visible:
            if name in masked:
                masked = masked.replace(name, "\x00")
        return [raw for key, raw in self._hidden.items() if find_word(masked, key) >= 0]


class LazyScanner:
    """A ``ViewScanner`` that is only prepared when the first text is scanned (most boundaries scan nothing new)."""

    def __init__(self, store, view):
        self._store, self._view, self._scanner = store, view, None

    def scan(self, text):
        if self._scanner is None:
            self._scanner = ViewScanner(self._store, self._view)
        return self._scanner.scan(text)


def scan(store, view, text):
    """Hidden names found in ``text`` (empty list = clean)."""
    return ViewScanner(store, view).scan(text)


def ensure_clean(store, view, text, label="text"):
    hits = scan(store, view, text)
    if hits:
        raise SpoilerLeak(label, hits)
    return text
