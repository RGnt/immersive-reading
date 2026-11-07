"""Shared character helpers for enrichment and source-reading checks."""


def hira(text):
    return "".join(chr(ord(c)-0x60) if "ァ" <= c <= "ヶ" else c for c in text)


def is_kanji(char):
    return "\u3400" <= char <= "\u9fff" or "\U00020000" <= char <= "\U000323af"


def is_latin_or_digit(char):
    return (char.isascii() and char.isalnum()) or "０" <= char <= "９" or "Ａ" <= char <= "Ｚ" or "ａ" <= char <= "ｚ"
