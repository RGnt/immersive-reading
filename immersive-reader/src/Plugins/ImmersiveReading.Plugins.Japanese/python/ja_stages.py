from __future__ import annotations

import contextlib
import csv
import json
import re
import sqlite3
from pathlib import Path
from typing import Any
from zipfile import ZipFile

from immersive.sdk.stage import (
    ExtractedToken,
    PipelineContext,
    PipelineStage,
    StudyItem,
)


@contextlib.contextmanager
def managed_dictionary_db(ctx: Any = None):
    """Context manager for safe, leak-free lifecycle management of dictionary SQLite connections."""
    conn = _get_dictionaries_db(ctx)
    try:
        yield conn
    finally:
        if conn:
            try:
                conn.close()
            except Exception:
                pass


# In-memory LRU / Memoization caches to prevent N+1 query overhead across chapters
_WORD_RESOLUTION_CACHE: dict[tuple[str, str, str | None, str], tuple[str, str, str]] = {}
_KANJI_DETAILS_CACHE: dict[str, dict[str, Any]] = {}


def _load_csv_deck(deck_path: Path | None) -> dict[str, dict[str, str]]:
    """Loads vocabulary deck CSV with support for Word, ReadingKana, and Definitions columns."""
    if not deck_path or not deck_path.is_file():
        return {}
    results = {}
    try:
        with open(deck_path, mode="r", encoding="utf-8-sig", errors="replace") as f:
            reader = csv.DictReader(f)
            for row in reader:
                lower_row = {k.lower().strip() if k else "": v.strip() for k, v in row.items() if k}
                word = lower_row.get("word") or lower_row.get("expression") or lower_row.get("headword") or ""
                reading = lower_row.get("readingkana") or lower_row.get("reading") or lower_row.get("kana") or ""
                definitions = lower_row.get("definitions") or lower_row.get("definition") or lower_row.get("meaning") or lower_row.get("glossary") or ""
                if word:
                    results[word] = {"reading": reading, "meaning": definitions}
                    if reading and reading not in results:
                        results[reading] = {"reading": reading, "meaning": definitions}
    except Exception:
        pass
    return results


def _get_dictionaries_db(ctx: Any = None) -> sqlite3.Connection | None:
    """Finds and connects to local dictionaries.db if available."""
    candidates = []
    if ctx and hasattr(ctx, "artifacts") and ctx.artifacts.get("dictionaries_db"):
        candidates.append(Path(ctx.artifacts["dictionaries_db"]))

    candidates.extend([
        Path("k:/immersive-reading/immersive-reader/.library-data/dictionaries.db"),
        Path(__file__).resolve().parents[4] / ".library-data" / "dictionaries.db",
        Path(__file__).resolve().parents[5] / "immersive-reader" / ".library-data" / "dictionaries.db",
        Path(__file__).resolve().parent.parent.parent.parent / "immersive-reader" / ".library-data" / "dictionaries.db",
        Path(__file__).resolve().parent.parent.parent / ".library-data" / "dictionaries.db",
        Path("immersive-reader/.library-data/dictionaries.db").resolve(),
        Path(".library-data/dictionaries.db").resolve(),
    ])
    for p in candidates:
        if p.is_file():
            try:
                conn = sqlite3.connect(str(p), timeout=5.0)
                return conn
            except Exception:
                pass
    return None


def _resolve_word_reading_and_meaning(
    lemma: str,
    surface: str,
    sudachi_reading: str | None,
    sudachi_pos: str,
    sample_sentence: str,
    csv_deck: dict[str, dict[str, str]],
    sqlite_conn: sqlite3.Connection | None,
) -> tuple[str, str, str]:
    """
    Resolves reading and meaning in strict precedence order:
    1. CSV deck (Word, ReadingKana, Definitions)
    2. Yomitan dictionaries (reading & glossary)
    3. Lastly JMdict / KANJIDIC2 / Sudachi morphology
    Uses in-memory cache to eliminate redundant SQLite queries across frequent words.
    """
    cache_key = (lemma, surface, sudachi_reading, sudachi_pos)
    if not csv_deck and cache_key in _WORD_RESOLUTION_CACHE:
        return _WORD_RESOLUTION_CACHE[cache_key]

    # Priority 1: Prefer CSV deck
    for key in (lemma, surface):
        if key in csv_deck:
            entry = csv_deck[key]
            reading = entry.get("reading") or sudachi_reading or ""
            meaning = entry.get("meaning") or ""
            if reading or meaning:
                res = (reading, meaning, "CSV Deck")
                _WORD_RESOLUTION_CACHE[cache_key] = res
                return res

    # Priority 2: Yomitan dictionaries (from dictionaries.db sources starting with yomitan-)
    if sqlite_conn:
        try:
            cur = sqlite_conn.cursor()
            cur.execute(
                """SELECT dt.reading, dt.glossary_text, ds.title
                   FROM dictionary_terms dt
                   JOIN dictionary_sources ds ON dt.dictionary_id = ds.dictionary_id
                   WHERE (dt.expression = ? OR dt.expression = ?)
                     AND ds.dictionary_id LIKE 'yomitan-%'
                   ORDER BY dt.score DESC LIMIT 1""",
                (lemma, surface),
            )
            row = cur.fetchone()
            if row:
                y_reading, y_gloss, y_title = row
                reading = y_reading or sudachi_reading or ""
                res = (reading, y_gloss or "", f"Yomitan ({y_title})")
                _WORD_RESOLUTION_CACHE[cache_key] = res
                return res
        except Exception:
            pass

    # Priority 3: Lastly use JMdict / KANJIDIC2
    if sqlite_conn:
        try:
            cur = sqlite_conn.cursor()
            cur.execute(
                """SELECT dt.reading, dt.glossary_text, ds.title
                   FROM dictionary_terms dt
                   JOIN dictionary_sources ds ON dt.dictionary_id = ds.dictionary_id
                   WHERE (dt.expression = ? OR dt.expression = ?)
                   ORDER BY dt.score DESC LIMIT 1""",
                (lemma, surface),
            )
            row = cur.fetchone()
            if row:
                jm_reading, jm_gloss, jm_title = row
                reading = jm_reading or sudachi_reading or ""
                res = (reading, jm_gloss or "", jm_title)
                _WORD_RESOLUTION_CACHE[cache_key] = res
                return res
        except Exception:
            pass

    # Morphological fallback
    fallback_meaning = sudachi_pos
    if sample_sentence:
        fallback_meaning += f": {sample_sentence}"
    res = (sudachi_reading or "", fallback_meaning, "Sudachi")
    _WORD_RESOLUTION_CACHE[cache_key] = res
    return res


def _resolve_kanji_details(char: str, sqlite_conn: sqlite3.Connection | None) -> dict[str, Any]:
    """Resolves kanji onyomi, kunyomi, primary meanings, and stroke count with memoization."""
    if char in _KANJI_DETAILS_CACHE:
        return _KANJI_DETAILS_CACHE[char]

    if sqlite_conn:
        try:
            cur = sqlite_conn.cursor()
            cur.execute(
                """SELECT on_readings_json, kun_readings_json, primary_meanings, stroke_count, grade, radical
                   FROM dictionary_kanji WHERE character = ? LIMIT 1""",
                (char,),
            )
            row = cur.fetchone()
            if row:
                on_raw, kun_raw, mean_raw, strokes, grade, radical = row
                on_list = json.loads(on_raw) if on_raw else []
                kun_list = json.loads(kun_raw) if kun_raw else []
                mean_list = json.loads(mean_raw) if mean_raw else []
                details = {
                    "character": char,
                    "onyomi": on_list,
                    "kunyomi": kun_list,
                    "meanings": mean_list,
                    "strokeCount": strokes,
                    "grade": grade,
                    "radical": radical,
                }
                _KANJI_DETAILS_CACHE[char] = details
                return details
        except Exception:
            pass

    empty_details = {
        "character": char,
        "onyomi": [],
        "kunyomi": [],
        "meanings": [],
        "strokeCount": None,
        "grade": None,
        "radical": None,
    }
    _KANJI_DETAILS_CACHE[char] = empty_details
    return empty_details


def _find_yomitan_grammar_rules(sqlite_conn: sqlite3.Connection | None = None) -> list[dict[str, Any]]:
    """
    Searches for bilingual (JP -> EN or JP -> FI) grammar dictionaries in Yomitan format.
    Scans dictionaries.db and any Yomitan .zip dictionary archives.
    """
    grammar_rules = []
    seen_expressions = set()

    # 1. Search in dictionaries.db for grammar dictionaries
    if sqlite_conn:
        try:
            cur = sqlite_conn.cursor()
            cur.execute(
                """SELECT ds.dictionary_id, ds.title FROM dictionary_sources ds
                   WHERE ds.title LIKE '%grammar%' OR ds.title LIKE '%bunpo%'
                      OR ds.title LIKE '%文法%' OR ds.title LIKE '%kielioppi%'
                      OR ds.title LIKE '%dojg%'"""
            )
            for dict_id, title in cur.fetchall():
                cur.execute(
                    """SELECT expression, reading, glossary_text FROM dictionary_terms
                       WHERE dictionary_id = ? ORDER BY score DESC LIMIT 500""",
                    (dict_id,),
                )
                for expr, rd, gloss in cur.fetchall():
                    if not expr or expr in seen_expressions:
                        continue
                    seen_expressions.add(expr)
                    clean_expr = re.sub(r"^[~〜\s]+", "", expr)
                    if not clean_expr:
                        continue
                    grammar_rules.append({
                        "id": f"yomitan-g-{len(grammar_rules)+1}",
                        "label": f"{expr} (【{rd}】)" if rd and rd != expr else expr,
                        "meaning": gloss or "",
                        "regex": re.escape(clean_expr),
                        "source": title,
                    })
        except Exception:
            pass

    # 2. Search Yomitan .zip files in standard folders
    dict_dirs = [
        Path(__file__).resolve().parent.parent.parent.parent / "immersive-reader" / ".library-data" / "dictionaries",
        Path(__file__).resolve().parent.parent.parent / ".library-data" / "dictionaries",
        Path("immersive-reader/.library-data/dictionaries").resolve(),
        Path("enrichement-extractor/data/yomitan").resolve(),
        Path("data/yomitan").resolve(),
    ]

    for d_dir in dict_dirs:
        if not d_dir.is_dir():
            continue
        for zip_path in d_dir.glob("*.zip"):
            try:
                with ZipFile(zip_path) as z:
                    if "index.json" not in z.namelist():
                        continue
                    index = json.loads(z.read("index.json").decode("utf-8-sig"))
                    title = index.get("title", "")
                    desc = index.get("description", "")
                    target_lang = (index.get("targetLanguage") or "").lower()
                    combo = (title + " " + desc).lower()

                    is_grammar = any(k in combo for k in ("grammar", "bunpo", "文法", "kielioppi", "dojg", "syntax"))
                    is_bilingual = target_lang in ("en", "eng", "fi", "fin", "finnish") or any(k in combo for k in ("english", "finnish", "suomi", "englanti"))

                    if is_grammar and is_bilingual:
                        for name in sorted(z.namelist()):
                            if re.match(r"^term_bank_\d+\.json$", name):
                                rows = json.loads(z.read(name).decode("utf-8-sig"))
                                for row in rows:
                                    if not isinstance(row, list) or len(row) < 6:
                                        continue
                                    expr = str(row[0])
                                    rd = str(row[1]) if len(row) > 1 and row[1] else ""
                                    glosses = row[5]
                                    meaning_text = ""
                                    if isinstance(glosses, list):
                                        meaning_text = "; ".join(str(g) for g in glosses if isinstance(g, (str, int)))
                                    elif isinstance(glosses, str):
                                        meaning_text = glosses

                                    if not expr or expr in seen_expressions:
                                        continue
                                    seen_expressions.add(expr)
                                    clean_expr = re.sub(r"^[~〜\s]+", "", expr)
                                    if not clean_expr:
                                        continue

                                    grammar_rules.append({
                                        "id": f"yomitan-zip-g-{len(grammar_rules)+1}",
                                        "label": f"{expr} (【{rd}】)" if rd and rd != expr else expr,
                                        "meaning": meaning_text[:140],
                                        "regex": re.escape(clean_expr),
                                        "source": f"{title} ({target_lang.upper() or 'Bilingual'})",
                                    })
            except Exception:
                pass

    return grammar_rules


def _load_grammar_rules(sqlite_conn: sqlite3.Connection | None = None) -> list[dict[str, Any]]:
    """
    Loads grammar patterns: prioritizes bilingual (JP -> EN / JP -> FI) Yomitan grammar dicts if available,
    and combines with curated grammar.json patterns.
    """
    # 1. Search for bilingual Yomitan grammar dictionaries
    bilingual_rules = _find_yomitan_grammar_rules(sqlite_conn)

    # 2. Load curated rules from grammar.json
    curated_rules: list[dict[str, Any]] = []
    candidates = [
        Path(__file__).resolve().parent.parent.parent.parent / "enrichement-extractor" / "immersive" / "grammar.json",
        Path(__file__).resolve().parent.parent / "immersive" / "grammar.json",
    ]
    for c in candidates:
        if c.is_file():
            try:
                curated_rules = json.loads(c.read_text(encoding="utf-8"))
                break
            except Exception:
                pass

    if not curated_rules:
        try:
            import importlib.resources
            p = importlib.resources.files("immersive").joinpath("grammar.json")
            curated_rules = json.loads(Path(str(p)).read_text(encoding="utf-8"))
        except Exception:
            pass

    # Built-in essential Japanese grammar patterns fallback if file not readable
    if not curated_rules:
        curated_rules = [
            {"id": "particle-wa", "label": "は (topic / contrast)", "meaning": "marks the topic or contrasts alternatives", "token": "particle", "form": "は"},
            {"id": "particle-ga", "label": "が (subject / identifier)", "meaning": "marks a subject, new or focused info", "token": "particle", "form": "が"},
            {"id": "particle-wo", "label": "を (direct object / route)", "meaning": "marks a direct object or transit path", "token": "particle", "form": "を"},
            {"id": "particle-ni", "label": "に (target / location / time)", "meaning": "marks target, recipient, time, or location", "token": "particle", "form": "に"},
            {"id": "particle-de", "label": "で (location / means)", "meaning": "marks where action occurs, means or tool", "token": "particle", "form": "で"},
            {"id": "particle-no", "label": "の (noun modifier / possession)", "meaning": "links nouns; can nominalize a clause", "token": "particle", "form": "の"},
            {"id": "particle-to", "label": "と (and / quotation)", "meaning": "links nouns, accompaniment, or quotation", "token": "particle", "form": "と"},
            {"id": "particle-mo", "label": "も (also / even)", "meaning": "adds an item to a set; emphasizes extent", "token": "particle", "form": "も"},
            {"id": "particle-kara", "label": "から (source / reason)", "meaning": "from a starting point, or because ...", "regex": r"(?:る|た|だ|い|ない|ます|です)から"},
            {"id": "te-iru", "label": "～ている / ～でいる", "meaning": "ongoing action or continuous state", "regex": r"(?:て|で)(?:いる|いた|いて|いない|います|いました)"},
            {"id": "te-kuru", "label": "～てくる / ～でくる", "meaning": "movement toward speaker or emerging change", "regex": r"(?:て|で)(?:くる|きた|きて)"},
            {"id": "te-iku", "label": "～ていく / ～でいく", "meaning": "movement away or action continuing onward", "regex": r"(?:て|で)(?:いく|いった|いって)"},
            {"id": "te-shimau", "label": "～てしまう / ～でしまう", "meaning": "complete an action, or regret/unintended result", "regex": r"(?:て|で)しま(?:う|った|って)"},
            {"id": "te-oku", "label": "～ておく / ～でおく", "meaning": "do in preparation or leave in a state", "regex": r"(?:て|で)お(?:く|いた|いて)"},
            {"id": "te-miru", "label": "～てみる / ～でみる", "meaning": "try doing something to see result", "regex": r"(?:て|で)み(?:る|た|て|よう)"},
            {"id": "te-kureru", "label": "～てくれる / ～でくれる", "meaning": "do something for speaker's benefit", "regex": r"(?:て|で)くれ(?:る|た|て)"},
            {"id": "te-morau", "label": "～てもらう / ～でもらう", "meaning": "receive the favor of having someone do something", "regex": r"(?:て|で)もら(?:う|った|って|える)"},
            {"id": "tara", "label": "～たら / ～だら", "meaning": "if / when (conditional)", "regex": r"(?:った|いた|えた|した|れた|んだ|かった)ら"},
            {"id": "n-da", "label": "～んだ / ～のだ", "meaning": "explanatory emphasis or background info", "regex": r"(?:ん|の)(?:だ|です|だった|でしょ|だね|だよ)"},
            {"id": "sou-da", "label": "～そう (appearance)", "meaning": "looks / seems ...", "regex": r"(?:し|い|な|れ|け|せ|た|み)そう(?:な|に|だ|です)"},
            {"id": "rashii", "label": "～らしい", "meaning": "apparently / seems like ...", "regex": r"らし(?:い|く|かった)"},
            {"id": "kamo", "label": "～かもしれない", "meaning": "might / perhaps ...", "regex": r"かもしれ(?:ない|なかった|ません)"},
        ]

    # Prepend bilingual dictionary rules so they take priority
    return bilingual_rules + curated_rules


class JapaneseSudachiTokenizerStage(PipelineStage):
    """
    Order 110: Tokenizes chapter text using SudachiPy (Mode C) or managed fallback.
    Applies strict dictionary precedence:
    1. CSV deck
    2. Yomitan dictionaries
    3. Lastly JMdict / KANJIDIC2
    """

    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        ctx.emit_progress("ja.sudachi_tokenize", "Initializing Japanese tokenizer & dictionary resolver...", 15)

        tokenizer_obj = None
        try:
            from sudachipy import Dictionary, SplitMode

            sudachi_dic = Dictionary()
            tokenizer_obj = sudachi_dic.create()
            split_mode = SplitMode.C
        except ImportError:
            pass

        ctx.writing_direction = "ttb" if plugin_config.get("verticalTypography", False) else "ltr"

        # 1. Load CSV deck if provided (Priority 1)
        csv_deck = _load_csv_deck(ctx.deck_csv_path)

        chapter_words_map: dict[int, list[dict[str, Any]]] = {}

        # 2. Connect to dictionaries.db safely using context manager
        with managed_dictionary_db(ctx) as db_conn:
            for ch_idx, chapter in enumerate(ctx.chapters):
                text = chapter.text or ""
                chapter_words_map[ch_idx] = []
                if not text:
                    continue

                sentences = [s.strip() for s in re.split(r"[。！？\n]+", text) if s.strip()]
                ch_word_counts: dict[str, int] = {}
                ch_word_items: dict[str, dict[str, Any]] = {}

                for sent in sentences:
                    if tokenizer_obj is not None:
                        morphemes = tokenizer_obj.tokenize(sent, split_mode)
                        for m in morphemes:
                            surface = m.surface()
                            lemma = m.dictionary_form()
                            raw_reading = m.reading_form()
                            pos_tuple = m.part_of_speech()
                            pos = pos_tuple[0] if pos_tuple else "UNK"

                            if not surface.strip() or pos in ("補助記号", "空白"):
                                continue

                            # Resolve reading and definition according to priority:
                            # 1. CSV deck -> 2. Yomitan -> 3. JMdict / KANJIDIC2
                            reading, meaning, source_tag = _resolve_word_reading_and_meaning(
                                lemma, surface, raw_reading, pos, sent, csv_deck, db_conn
                            )

                            token = ExtractedToken(
                                surface=surface,
                                lemma=lemma,
                                reading=reading,
                                pos=pos,
                                chapter_index=ch_idx,
                                sentence_text=sent,
                                features={
                                    "pos_full": list(pos_tuple),
                                    "reading": reading,
                                    "source": source_tag,
                                },
                            )
                            ctx.tokens.append(token)

                            # Global study item
                            item = ctx.study_items.get(lemma)
                            if item is None:
                                item = StudyItem(
                                    key=lemma,
                                    headword=surface,
                                    reading=reading,
                                    pos=meaning or pos,
                                    book_occurrences=1,
                                    sample_sentences=[sent],
                                )
                                item.metadata["chapters"] = [ch_idx]
                                item.metadata["chapter_occurrences"] = {str(ch_idx): 1}
                                item.metadata["source"] = source_tag
                                item.metadata["meaning"] = meaning
                                ctx.study_items[lemma] = item
                            else:
                                item.book_occurrences += 1
                                ch_set = item.metadata.setdefault("chapters", [])
                                if ch_idx not in ch_set:
                                    ch_set.append(ch_idx)
                                ch_occ = item.metadata.setdefault("chapter_occurrences", {})
                                ch_occ[str(ch_idx)] = ch_occ.get(str(ch_idx), 0) + 1
                                if len(item.sample_sentences) < 3 and sent not in item.sample_sentences:
                                    item.sample_sentences.append(sent)

                            # Chapter tracking
                            ch_word_counts[lemma] = ch_word_counts.get(lemma, 0) + 1
                            if lemma not in ch_word_items:
                                ch_word_items[lemma] = {
                                    "lemma": lemma,
                                    "word": surface,
                                    "reading": reading,
                                    "partOfSpeech": pos,
                                    "definition": meaning,
                                    "source": source_tag,
                                    "sampleSentence": sent,
                                }
                    else:
                        words = re.findall(r"[\u4e00-\u9faf\u3040-\u309f\u30a0-\u30ff]+", sent)
                        for w in words:
                            reading, meaning, source_tag = _resolve_word_reading_and_meaning(
                                w, w, None, "Word", sent, csv_deck, db_conn
                            )
                            token = ExtractedToken(
                                surface=w,
                                lemma=w,
                                reading=reading,
                                pos="Word",
                                chapter_index=ch_idx,
                                sentence_text=sent,
                            )
                            ctx.tokens.append(token)
                            item = ctx.study_items.get(w)
                            if item is None:
                                item = StudyItem(
                                    key=w,
                                    headword=w,
                                    reading=reading,
                                    pos=meaning or "Word",
                                    book_occurrences=1,
                                    sample_sentences=[sent],
                                )
                                item.metadata["chapters"] = [ch_idx]
                                item.metadata["chapter_occurrences"] = {str(ch_idx): 1}
                                item.metadata["source"] = source_tag
                                item.metadata["meaning"] = meaning
                                ctx.study_items[w] = item
                            else:
                                item.book_occurrences += 1
                                ch_set = item.metadata.setdefault("chapters", [])
                                if ch_idx not in ch_set:
                                    ch_set.append(ch_idx)
                                ch_occ = item.metadata.setdefault("chapter_occurrences", {})
                                ch_occ[str(ch_idx)] = ch_occ.get(str(ch_idx), 0) + 1
                                if len(item.sample_sentences) < 3 and sent not in item.sample_sentences:
                                    item.sample_sentences.append(sent)

                            ch_word_counts[w] = ch_word_counts.get(w, 0) + 1
                            if w not in ch_word_items:
                                ch_word_items[w] = {
                                    "lemma": w,
                                    "word": w,
                                    "reading": reading,
                                    "partOfSpeech": "Word",
                                    "definition": meaning,
                                    "source": source_tag,
                                    "sampleSentence": sent,
                                }

                # Build chapter word list sorted by occurrence count
                ch_list = []
                for lemma, info in sorted(ch_word_items.items(), key=lambda kv: ch_word_counts.get(kv[0], 0), reverse=True):
                    ch_list.append({
                        "lemma": info["lemma"],
                        "word": info["word"],
                        "reading": info["reading"],
                        "partOfSpeech": info["partOfSpeech"],
                        "definition": info["definition"],
                        "source": info["source"],
                        "occurrences": ch_word_counts.get(lemma, 1),
                        "sampleSentence": info["sampleSentence"],
                    })
                chapter_words_map[ch_idx] = ch_list

        ctx.artifacts["chapter_words"] = chapter_words_map


class JapaneseKanjiEnrichmentStage(PipelineStage):
    """
    Order 210: Identifies kanji in vocabulary items and chapter text, resolving ON/KUN
    and meanings from KANJIDIC2 / Kanji alive in dictionaries.db.
    """

    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        ctx.emit_progress("ja.kanji_enrichment", "Enriching Japanese kanji metadata from KANJIDIC2...", 45)

        # 1. Global vocabulary kanji metadata
        for key, item in ctx.study_items.items():
            kanji_chars = [c for c in item.headword if "\u4e00" <= c <= "\u9faf"]
            if kanji_chars:
                item.metadata["kanji_characters"] = kanji_chars
                item.metadata["kanji_count"] = len(kanji_chars)
                if len(kanji_chars) == 1:
                    item.level_tag = "Single Kanji"
                else:
                    item.level_tag = "Compound (Jukugo)"

        # 2. Extract per-chapter kanji distribution with dictionary definitions (Source-grounded to book text)
        seen_kanji: set[str] = set()
        chapter_kanji_map: dict[int, list[dict[str, Any]]] = {}

        with managed_dictionary_db(ctx) as db_conn:
            for ch_idx, chapter in enumerate(ctx.chapters):
                text = chapter.text or ""
                kanji_counts: dict[str, int] = {}
                kanji_words: dict[str, list[str]] = {}

                # Strict source grounding: only kanji that actually appear in this chapter's text
                for token in ctx.tokens:
                    if token.chapter_index == ch_idx:
                        for char in token.surface:
                            if "\u4e00" <= char <= "\u9faf":
                                kanji_counts[char] = kanji_counts.get(char, 0) + 1
                                w_list = kanji_words.setdefault(char, [])
                                if token.surface not in w_list and len(w_list) < 4:
                                    w_list.append(token.surface)

                if not kanji_counts and text:
                    for char in text:
                        if "\u4e00" <= char <= "\u9faf":
                            kanji_counts[char] = kanji_counts.get(char, 0) + 1

                kanji_list = []
                for char, count in sorted(kanji_counts.items(), key=lambda kv: kv[1], reverse=True):
                    is_first = char not in seen_kanji
                    if is_first:
                        seen_kanji.add(char)

                    info = _resolve_kanji_details(char, db_conn)
                    kanji_list.append({
                        "character": char,
                        "occurrences": count,
                        "words": kanji_words.get(char, []),
                        "codepointHex": f"{ord(char):04X}",
                        "onyomi": info.get("onyomi", []),
                        "kunyomi": info.get("kunyomi", []),
                        "meanings": info.get("meanings", []),
                        "strokeCount": info.get("strokeCount"),
                        "grade": info.get("grade"),
                        "firstAppearance": is_first,
                    })
                chapter_kanji_map[ch_idx] = kanji_list

        ctx.artifacts["chapter_kanji"] = chapter_kanji_map


class JapaneseGrammarDetectionStage(PipelineStage):
    """
    Order 310: Detects Japanese grammar constructions per chapter.
    Prioritizes bilingual (JP -> EN or JP -> FI) Yomitan grammar dictionaries when available,
    supplemented by curated grammar rules.
    """

    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        ctx.emit_progress("ja.grammar", "Scanning for bilingual Yomitan & curated grammar rules...", 65)

        chapter_grammar_map: dict[int, list[dict[str, Any]]] = {}

        with managed_dictionary_db(ctx) as db_conn:
            rules = _load_grammar_rules(db_conn)

            compiled_rules: list[tuple[dict[str, Any], Any]] = []
            for r in rules:
                if "regex" in r:
                    try:
                        compiled_rules.append((r, re.compile(r["regex"])))
                    except Exception:
                        pass

            for ch_idx, chapter in enumerate(ctx.chapters):
                text = chapter.text or ""
                if not text:
                    chapter_grammar_map[ch_idx] = []
                    continue

                sentences = [s.strip() for s in re.split(r"[。！？\n]+", text) if s.strip()]
                rule_counts: dict[str, int] = {}
                rule_examples: dict[str, list[str]] = {}

                for sent in sentences:
                    for rule_meta, rx in compiled_rules:
                        rule_id = rule_meta["id"]
                        if rx.search(sent):
                            rule_counts[rule_id] = rule_counts.get(rule_id, 0) + 1
                            ex_list = rule_examples.setdefault(rule_id, [])
                            if sent not in ex_list and len(ex_list) < 2:
                                ex_list.append(sent)

                g_list = []
                for rule_meta, _ in compiled_rules:
                    rule_id = rule_meta["id"]
                    count = rule_counts.get(rule_id, 0)
                    if count > 0:
                        g_list.append({
                            "id": rule_id,
                            "label": rule_meta.get("label", rule_id),
                            "meaning": rule_meta.get("meaning", ""),
                            "occurrences": count,
                            "examples": rule_examples.get(rule_id, []),
                            "note": rule_meta.get("note", ""),
                            "source": rule_meta.get("source", "Grammar Rule"),
                        })

                g_list.sort(key=lambda g: g["occurrences"], reverse=True)
                chapter_grammar_map[ch_idx] = g_list

        ctx.artifacts["chapter_grammar"] = chapter_grammar_map


# =========================================================================
# PDF SECTION RENDERERS (Template Method Pattern, grouped by chapter)
# =========================================================================

class JapanesePdfSectionRendererBase:
    """Base class applying Template Method pattern for Japanese PDF section renderers."""

    section_id: str = ""
    kind: str = ""
    title: str = ""

    def build_flowables(self, ctx: PipelineContext, style_sheet: Any) -> list[Any]:
        try:
            from reportlab.platypus import Paragraph, Spacer, Table, TableStyle, PageBreak
            from reportlab.lib import colors
        except ImportError:
            return []

        story = []
        for ch_idx, chapter in enumerate(ctx.chapters):
            items = self.get_chapter_items(ctx, ch_idx)
            if not items:
                continue

            if story:
                story.append(PageBreak())

            ch_title = chapter.title or f"Chapter {ch_idx + 1}"
            header_text = self.format_chapter_header(ch_idx, ch_title, len(items))
            story.append(Paragraph(header_text, style_sheet["Heading2"]))
            story.append(Spacer(1, 8))

            table_data = [self.get_table_headers()]
            for idx, item in enumerate(items):
                table_data.append(self.format_row(idx + 1, item))

            t = Table(table_data, colWidths=self.get_col_widths())
            t.setStyle(
                TableStyle(
                    [
                        ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor(self.get_header_color())),
                        ("TEXTCOLOR", (0, 0), (-1, 0), colors.whitesmoke),
                        ("FONTNAME", (0, 0), (-1, -1), "Japanese"),
                        ("FONTSIZE", (0, 0), (-1, -1), self.get_font_size()),
                        ("BOTTOMPADDING", (0, 0), (-1, 0), 4),
                        ("TOPPADDING", (0, 0), (-1, -1), 3),
                        ("BOTTOMPADDING", (0, 0), (-1, -1), 3),
                        ("GRID", (0, 0), (-1, -1), 0.5, colors.HexColor("#cbd5e1")),
                    ]
                )
            )
            story.append(t)
            story.append(Spacer(1, 14))

        return story

    def get_chapter_items(self, ctx: PipelineContext, ch_idx: int) -> list[Any]:
        return []

    def format_chapter_header(self, ch_idx: int, title: str, count: int) -> str:
        return f"Chapter {ch_idx + 1} — {title} ({count} Items)"

    def get_table_headers(self) -> list[str]:
        return []

    def format_row(self, row_num: int, item: Any) -> list[str]:
        return []

    def get_col_widths(self) -> list[int]:
        return []

    def get_header_color(self) -> str:
        return "#1e293b"

    def get_font_size(self) -> float:
        return 8.0


class JapaneseVocabularySectionRenderer(JapanesePdfSectionRendererBase):
    """PDF Section Renderer for Vocabulary Master Sheet, organized by chapter."""

    section_id = "ja.vocabulary_master"
    kind = "vocabulary"
    title = "Vocabulary Master Sheet by Chapter"

    def get_chapter_items(self, ctx: PipelineContext, ch_idx: int) -> list[Any]:
        chapter_words = ctx.artifacts.get("chapter_words", {})
        return chapter_words.get(ch_idx, [])

    def format_chapter_header(self, ch_idx: int, title: str, count: int) -> str:
        return f"Chapter {ch_idx + 1} — {title} ({count} Words)"

    def get_table_headers(self) -> list[str]:
        return ["Nr.", "Headword", "Reading", "PoS", "Count", "Definition / Context"]

    def format_row(self, row_num: int, item: Any) -> list[str]:
        meaning = item.get("definition", "") or item.get("partOfSpeech", "")
        if len(meaning) > 42:
            meaning = meaning[:39] + "..."
        return [
            str(row_num),
            item.get("word", ""),
            item.get("reading", ""),
            item.get("partOfSpeech", ""),
            str(item.get("occurrences", 1)),
            meaning,
        ]

    def get_col_widths(self) -> list[int]:
        return [24, 76, 76, 45, 36, 220]

    def get_header_color(self) -> str:
        return "#1e293b"

    def get_font_size(self) -> float:
        return 8.0


class JapaneseKanjiSectionRenderer(JapanesePdfSectionRendererBase):
    """PDF Section Renderer for Kanji Master Sheet, organized by chapter."""

    section_id = "ja.kanji_master"
    kind = "kanji"
    title = "Kanji Master Sheet by Chapter"

    def __init__(self) -> None:
        self._seen_renderer_kanji: set[str] = set()

    def build_flowables(self, ctx: PipelineContext, style_sheet: Any) -> list[Any]:
        # Reset seen set per compilation
        self._seen_renderer_kanji = set()
        return super().build_flowables(ctx, style_sheet)

    def get_chapter_items(self, ctx: PipelineContext, ch_idx: int) -> list[Any]:
        chapter_kanji = ctx.artifacts.get("chapter_kanji", {})
        kanji_list = chapter_kanji.get(ch_idx, [])
        new_kanji = [
            k for k in kanji_list
            if k.get("character") and (k.get("firstAppearance", False) or k["character"] not in self._seen_renderer_kanji)
        ]
        for k in new_kanji:
            self._seen_renderer_kanji.add(k["character"])
        return new_kanji

    def format_chapter_header(self, ch_idx: int, title: str, count: int) -> str:
        return f"Chapter {ch_idx + 1} — {title} ({count} New Kanji)"

    def get_table_headers(self) -> list[str]:
        return ["Nr.", "Kanji", "Meanings", "On / Kun", "Count", "In Chapter Words"]

    def format_row(self, row_num: int, item: Any) -> list[str]:
        words_str = "、 ".join(item.get("words", []))
        meanings_str = ", ".join(item.get("meanings", [])) if item.get("meanings") else f"U+{item.get('codepointHex', '')}"
        readings_str = "/".join((item.get("onyomi", [])[:2] + item.get("kunyomi", [])[:2]))
        return [
            str(row_num),
            item.get("character", ""),
            meanings_str[:35],
            readings_str[:25],
            str(item.get("occurrences", 1)),
            words_str[:30],
        ]

    def get_col_widths(self) -> list[int]:
        return [24, 45, 130, 95, 36, 140]

    def get_header_color(self) -> str:
        return "#0f766e"

    def get_font_size(self) -> float:
        return 8.5


class JapaneseGrammarSectionRenderer(JapanesePdfSectionRendererBase):
    """PDF Section Renderer for Grammar Master Sheet, organized by chapter."""

    section_id = "ja.grammar_master"
    kind = "grammar"
    title = "Grammar Master Sheet by Chapter"

    def get_chapter_items(self, ctx: PipelineContext, ch_idx: int) -> list[Any]:
        chapter_grammar = ctx.artifacts.get("chapter_grammar", {})
        return chapter_grammar.get(ch_idx, [])

    def format_chapter_header(self, ch_idx: int, title: str, count: int) -> str:
        return f"Chapter {ch_idx + 1} — {title} ({count} Patterns Detected)"

    def get_table_headers(self) -> list[str]:
        return ["Pattern", "Function & Meaning", "Count", "Sample Sentence from Chapter"]

    def format_row(self, row_num: int, item: Any) -> list[str]:
        example = item.get("examples", [""])[0] if item.get("examples") else ""
        if len(example) > 40:
            example = example[:37] + "..."
        return [
            item.get("label", ""),
            item.get("meaning", ""),
            str(item.get("occurrences", 1)),
            example,
        ]

    def get_col_widths(self) -> list[int]:
        return [110, 140, 42, 185]

    def get_header_color(self) -> str:
        return "#4338ca"

    def get_font_size(self) -> float:
        return 8.5


class JapanesePdfSectionsStage(PipelineStage):
    """
    Order 410: Contributes chapter-organized Vocabulary, Kanji, and Grammar PDF sections to the master A4 PDF.
    """

    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        ctx.emit_progress("ja.pdf_sections", "Registering chapter-based Japanese PDF sections...", 80)
        ctx.pdf_sections.append(JapaneseVocabularySectionRenderer())
        ctx.pdf_sections.append(JapaneseKanjiSectionRenderer())
        ctx.pdf_sections.append(JapaneseGrammarSectionRenderer())
