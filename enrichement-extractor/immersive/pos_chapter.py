"""Chapter-level Part-of-Speech morphological tagger using Sudachi.

Analyzes tokens / words in a chapter and categorizes them into broad word types
(adjective, particle, verb, noun, adverb, suffix, conjunction, auxiliary, interjection)
and fine-grained grammatical subtypes (adjective-i, adjective-na, verb-u, verb-ru,
verb-irr, particle-single, particle-multi, suffix, adverb, etc.).
"""
from __future__ import annotations

import json
import sys
from typing import Any, Dict, List, Optional, Tuple

_SUDACHI_TOKENIZER = None


def get_sudachi_tokenizer():
    global _SUDACHI_TOKENIZER
    if _SUDACHI_TOKENIZER is None:
        try:
            from sudachipy import Dictionary
            try:
                # Upstream sudachipy recommended method
                _SUDACHI_TOKENIZER = Dictionary().tokenizer()
            except AttributeError:
                _SUDACHI_TOKENIZER = Dictionary().create()
        except Exception:
            _SUDACHI_TOKENIZER = False
    return _SUDACHI_TOKENIZER if _SUDACHI_TOKENIZER is not False else None


def classify_morpheme(surface: str, pos: Any, conj_type: str = "") -> Tuple[str, str, str]:
    """Classify a morpheme into (wordType, subType, code).

    wordType: adjective, particle, verb, noun, adverb, suffix, conjunction, auxiliary, interjection
    subType: adjective-i, adjective-na, verb-u, verb-ru, verb-irr, particle-single, particle-multi, suffix, etc.
    code: adj-i, adj-na, verb-u, verb-ru, verb-irr, suffix, prt-single, prt-multi, adverb, conj, aux, intj, noun
    """
    if isinstance(pos, str):
        pos_parts = [p.strip() for p in pos.split(",") if p.strip()]
    elif isinstance(pos, (list, tuple)):
        pos_parts = [str(p) for p in pos]
    else:
        pos_parts = [str(pos or "")]

    p0 = pos_parts[0] if len(pos_parts) > 0 else ""
    p1 = pos_parts[1] if len(pos_parts) > 1 else ""
    all_pos = ",".join(pos_parts)
    ctype = conj_type or (pos_parts[4] if len(pos_parts) > 4 else "")

    # 1. Suffixes & Prefixes (prioritize over noun / verb inflection)
    if "接尾" in all_pos or p0 == "接尾辞":
        return "suffix", "suffix", "suffix"
    if "接頭" in all_pos or p0 == "接頭辞":
        return "suffix", "prefix", "suffix"

    # 2. Particles (single-kana vs multi-kana)
    if p0.startswith("助詞"):
        is_single = len(surface) == 1
        st = "particle-single" if is_single else "particle-multi"
        cd = "prt-single" if is_single else "prt-multi"
        return "particle", st, cd

    # 3. Verbs by conjugation type (Godan / u-verb, Ichidan / ru-verb, Irregular)
    if p0.startswith("動詞"):
        if "五段" in ctype:
            return "verb", "verb-u", "verb-u"
        if "一段" in ctype:
            return "verb", "verb-ru", "verb-ru"
        if any(k in ctype for k in ("サ変", "カ変", "不規則", "サ行変格", "カ行変格")):
            return "verb", "verb-irr", "verb-irr"
        return "verb", "verb-u", "verb-u"

    # 4. Adjectives (na-adjective vs i-adjective)
    # Na-adjectives: UniDic tags as 形状詞, 形状詞可能, サ変形状詞可能; IPAdic tags as 名詞,形容動詞語幹
    if any(k in all_pos for k in ("形状詞", "形容動詞")):
        return "adjective", "adjective-na", "adj-na"
    if p0.startswith("形容詞") or "形容詞" in ctype:
        return "adjective", "adjective-i", "adj-i"

    # 5. Adverbs (UniDic: 副詞 or 名詞,普通名詞,副詞可能)
    if "副詞" in all_pos:
        return "adverb", "adverb", "adverb"

    # 6. Conjunctions
    if p0.startswith("接続詞"):
        return "conjunction", "conjunction", "conj"

    # 7. Auxiliaries
    if p0.startswith("助動詞"):
        return "auxiliary", "auxiliary", "aux"

    # 8. Interjections
    if p0.startswith("感動詞"):
        return "interjection", "interjection", "intj"

    # 9. Nouns / Pronouns
    if p0.startswith("名詞") or p0.startswith("代名詞"):
        return "noun", "noun", "noun"

    return "unknown", "unknown", "unknown"


def process_chapter_tokens(tokens: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Process a list of tokens and return their POS classifications."""
    results = []
    tok_nlp = get_sudachi_tokenizer()

    for item in tokens:
        tid = item.get("id", item.get("tokenId", 0))
        surface = item.get("surface", "")
        pos = item.get("pos", item.get("partOfSpeech", ""))
        pos_sub = item.get("posSub", item.get("partOfSpeechSub", ""))
        conj_type = item.get("conjType", item.get("conjugationType", ""))

        # If POS is missing or empty, but we have Sudachi and surface text, analyze it
        if (not pos or pos == "*") and tok_nlp and surface:
            try:
                from sudachipy import SplitMode
                morphemes = tok_nlp.tokenize(surface, SplitMode.C)
                if morphemes:
                    m = morphemes[0]
                    pos = m.part_of_speech()
                    conj_type = pos[4] if len(pos) > 4 else ""
            except Exception:
                pass

        if isinstance(pos, str) and pos_sub:
            pos_tuple = (pos, pos_sub)
        else:
            pos_tuple = pos

        wt, st, cd = classify_morpheme(surface, pos_tuple, conj_type)

        classes = []
        if wt != "unknown":
            classes.append(wt)
        if st != "unknown" and st != wt:
            classes.append(st)
        if cd != "unknown":
            legacy = f"ir-pos-{cd}"
            if legacy not in classes:
                classes.append(legacy)

        results.append({
            "id": tid,
            "wordType": wt,
            "subType": st,
            "code": cd,
            "classes": classes,
        })

    return results


def tokenize_chapter_paragraphs(paragraphs: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    """Tokenize each paragraph using Sudachi and categorize every morpheme into word types."""
    tok_nlp = get_sudachi_tokenizer()
    results = []

    for p in paragraphs:
        pid = p.get("id", p.get("pid", 0))
        text = p.get("text", "")
        if not text:
            results.append({"id": pid, "morphemes": []})
            continue

        morpheme_list = []
        if tok_nlp:
            try:
                from sudachipy import SplitMode
                morphemes = tok_nlp.tokenize(text, SplitMode.C)
                for i, m in enumerate(morphemes):
                    surface = m.surface()
                    pos = m.part_of_speech()
                    conj_type = pos[4] if len(pos) > 4 else ""
                    wt, st, cd = classify_morpheme(surface, pos, conj_type)

                    # If this is a verbal noun followed by a suru auxiliary/verb (e.g. 勉強する, 散歩した)
                    if (
                        wt == "noun"
                        and any("サ変" in p for p in pos)
                        and i + 1 < len(morphemes)
                    ):
                        next_m = morphemes[i + 1]
                        next_pos = next_m.part_of_speech()
                        next_surf = next_m.surface()
                        if (
                            any("サ行変格" in p or "サ変" in p for p in next_pos)
                            or next_surf in ("する", "し", "して", "した", "すれ", "せよ", "しよう", "される", "された", "させる", "させ")
                        ):
                            wt, st, cd = "verb", "verb-irr", "verb-irr"

                    classes = []
                    if wt != "unknown":
                        classes.append(wt)
                    if st != "unknown" and st != wt:
                        classes.append(st)
                    if cd != "unknown":
                        legacy = f"ir-pos-{cd}"
                        if legacy not in classes:
                            classes.append(legacy)

                    morpheme_list.append({
                        "surface": surface,
                        "start": m.begin(),
                        "end": m.end(),
                        "lemma": m.dictionary_form(),
                        "reading": m.reading_form(),
                        "wordType": wt,
                        "subType": st,
                        "code": cd,
                        "classes": classes,
                    })
            except Exception:
                morpheme_list = []

        if not morpheme_list and text:
            # Fallback when Sudachi is unavailable
            import re
            for match in re.finditer(r"[\u4e00-\u9faf]+|[\u3040-\u309f]+|[\u30a0-\u30ff]+|[a-zA-Z0-9]+|[^\s]", text):
                s = match.group(0)
                wt, st, cd = classify_morpheme(s, "")
                classes = [c for c in (wt, st, f"ir-pos-{cd}") if c != "unknown"]
                morpheme_list.append({
                    "surface": s,
                    "start": match.start(),
                    "end": match.end(),
                    "lemma": s,
                    "reading": s,
                    "wordType": wt,
                    "subType": st,
                    "code": cd,
                    "classes": classes,
                })

        results.append({
            "id": pid,
            "morphemes": morpheme_list,
        })

    return results


def process_payload(payload: Dict[str, Any]) -> Dict[str, Any]:
    book_id = payload.get("bookId", "")
    chapter_href = payload.get("chapterHref", "")
    result: Dict[str, Any] = {
        "bookId": book_id,
        "chapterHref": chapter_href,
    }
    if "paragraphs" in payload:
        result["paragraphs"] = tokenize_chapter_paragraphs(payload["paragraphs"])
    if "tokens" in payload:
        result["classifications"] = process_chapter_tokens(payload["tokens"])
    return result


def main(argv: Optional[List[str]] = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")

    input_text = ""
    if argv and len(argv) > 1 and argv[1] != "-":
        with open(argv[1], "r", encoding="utf-8") as f:
            input_text = f.read()
    else:
        input_text = sys.stdin.read()

    if not input_text.strip():
        sys.stderr.write("Error: Empty JSON payload.\n")
        return 1

    if input_text.startswith("\ufeff"):
        input_text = input_text[1:]

    try:
        data = json.loads(input_text)
    except json.JSONDecodeError as err:
        sys.stderr.write(f"Error: Invalid JSON payload: {err}\n")
        return 1

    result = process_payload(data)
    json.dump(result, sys.stdout, ensure_ascii=False)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

