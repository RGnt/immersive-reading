"""Story positions: (volume, chapter, offset), ordered lexicographically (spec 18, section 3).

A position is stored in the database as a single integer ``key`` so that "is this fact visible at P?" is one comparison
in SQL and in Python: ``key = volume * 10**12 + chapter * 10**6 + offset``.
"""
from dataclasses import dataclass

VOLUME_SCALE = 10 ** 12
CHAPTER_SCALE = 10 ** 6


@dataclass(frozen=True, order=True)
class StoryPosition:
    volume: int
    chapter: int
    offset: int = 0

    def __post_init__(self):
        if not (0 <= self.volume < CHAPTER_SCALE and 0 <= self.chapter < CHAPTER_SCALE and 0 <= self.offset < CHAPTER_SCALE):
            raise ValueError(f"position out of range: {self.volume}:{self.chapter}:{self.offset}")

    @property
    def key(self):
        return self.volume * VOLUME_SCALE + self.chapter * CHAPTER_SCALE + self.offset

    @classmethod
    def from_key(cls, key):
        volume, rest = divmod(int(key), VOLUME_SCALE)
        chapter, offset = divmod(rest, CHAPTER_SCALE)
        return cls(volume, chapter, offset)

    @classmethod
    def parse(cls, text):
        """``"1:2"`` or ``"1:2:350"`` (volume:chapter[:offset])."""
        parts = str(text).strip().split(":")
        if len(parts) not in (2, 3) or not all(p.strip().lstrip("-").isdigit() for p in parts):
            raise ValueError(f"invalid position {text!r}, expected VOLUME:CHAPTER[:OFFSET]")
        return cls(*(int(p) for p in parts))

    def __str__(self):
        return f"{self.volume}:{self.chapter}" if self.offset == 0 else f"{self.volume}:{self.chapter}:{self.offset}"

    def chapter_start(self):
        return StoryPosition(self.volume, self.chapter, 0)


def chapter_key(volume, chapter):
    """Key of the start of a chapter; also the ``src_key`` of every row created while processing that chapter."""
    return StoryPosition(volume, chapter, 0).key


def as_of(volume, chapter):
    """"Everything established strictly before the start of this chapter" (spec 18, section 3)."""
    return StoryPosition(volume, chapter, 0)
