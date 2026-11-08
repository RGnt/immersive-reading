"""
JSON Transfer Format & Learning Material Generator for Immersive Reading.

Provides an open, self-contained JSON transfer format ('learning_material.json' / 'study_transfer.json')
containing book metadata, complete chapter-by-chapter vocabulary, kanji (with stroke diagrams & readings),
and grammar. Can be loaded into the desktop reader or used standalone to generate official PDF study sheets.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any

from .pdf_elements import pdf_session
from .master_pdf import write_master
from .kanji_pdf import write_kanji_document
from .resources import ResourceBundle, ResourceStore, thaw


def find_data_dir() -> Path | None:
    """Finds enrichement-extractor/data directory containing KanjiVG, fonts, and dictionaries."""
    candidates = [
        Path(__file__).resolve().parent.parent / "data",
        Path(__file__).resolve().parents[3] / "enrichement-extractor" / "data",
        Path("k:/immersive-reading/enrichement-extractor/data"),
        Path("enrichement-extractor/data").resolve(),
        Path("data").resolve(),
    ]
    for c in candidates:
        if (c / "kanjivg" / "source.zip").is_file():
            return c
    return None


def get_kanjivg_stroke_orders(kanji_chars: list[str], data_dir: Path | None = None) -> dict[str, Any]:
    """Retrieves stroke order SVG path data from KanjiVG source archive."""
    d_dir = data_dir or find_data_dir()
    if not d_dir:
        return {}
    kanjivg_zip = d_dir / "kanjivg" / "source.zip"
    if not kanjivg_zip.is_file():
        return {}
    try:
        bundle = ResourceBundle.from_directory(d_dir)
        with ResourceStore(bundle) as store:
            glyphs = store.kanjivg_glyphs(kanji_chars)
            return {
                ch: thaw(rec["stroke_order"])
                for ch, rec in glyphs.items()
                if rec and rec.get("stroke_order")
            }
    except Exception:
        return {}


def build_transfer_payload(
    book_id: str,
    title: str,
    language: str,
    writing_direction: str,
    chapters: list[dict[str, Any]],
    deck_csv_file: str | None = None,
    epub_file: str | None = None,
    data_dir: Path | None = None,
) -> dict[str, Any]:
    """
    Constructs a comprehensive, portable learning_material JSON payload.
    Enriches all kanji entries with KanjiVG stroke order diagrams and resolves
    context word readings and glosses.
    """
    all_kanji_chars = set()
    for ch in chapters:
        for k in ch.get("kanji", []):
            c = k.get("character")
            if c:
                all_kanji_chars.add(c)

    stroke_orders = get_kanjivg_stroke_orders(list(all_kanji_chars), data_dir)

    enriched_chapters = []
    all_vocab = []
    all_kanji = []
    all_grammar = []

    seen_vocab_keys = set()
    seen_kanji_chars = set()
    seen_grammar_ids = set()

    for ch_idx, ch in enumerate(chapters):
        ch_vocab = []
        words_map = {}
        for w_idx, w in enumerate(ch.get("vocabulary", [])):
            word_str = w.get("word") or w.get("lemma") or ""
            reading_str = w.get("reading") or ""
            meaning_str = w.get("meaning") or w.get("definition") or w.get("partOfSpeech") or ""
            vocab_item = {
                "study_number": w.get("study_number", len(all_vocab) + 1),
                "word": word_str,
                "lemma": w.get("lemma") or word_str,
                "reading": reading_str,
                "meaning": meaning_str,
                "partOfSpeech": w.get("partOfSpeech") or "Word",
                "occurrences": w.get("occurrences", 1),
                "sampleSentence": w.get("sampleSentence") or "",
                "source": w.get("source") or "Dictionary",
            }
            ch_vocab.append(vocab_item)
            words_map[word_str] = vocab_item
            if w.get("lemma"):
                words_map[w["lemma"]] = vocab_item

            v_key = (word_str, reading_str)
            if v_key not in seen_vocab_keys:
                seen_vocab_keys.add(v_key)
                all_vocab.append(vocab_item)

        ch_kanji_list = []
        for k in ch.get("kanji", []):
            char = k.get("character") or ""
            if not char:
                continue

            context_words = k.get("words", [])
            context_w = context_words[0] if context_words else char
            w_info = words_map.get(context_w, {})
            context_r = k.get("context_reading") or w_info.get("reading") or ""
            context_g = k.get("context_gloss") or w_info.get("meaning") or ""
            sample_s = k.get("sampleSentence") or w_info.get("sampleSentence") or context_w

            stroke_order = k.get("stroke_order") or stroke_orders.get(char)

            kanji_item = {
                "character": char,
                "codepointHex": k.get("codepointHex") or f"{ord(char):04X}",
                "occurrences": k.get("occurrences", 1),
                "strokeCount": k.get("strokeCount") or (len(stroke_order.get("strokes", [])) if stroke_order else 0),
                "meanings": k.get("meanings") or [],
                "onyomi": k.get("onyomi") or [],
                "kunyomi": k.get("kunyomi") or [],
                "words": context_words,
                "context_word": context_w,
                "context_reading": context_r,
                "context_gloss": context_g,
                "sampleSentence": sample_s,
                "stroke_order": stroke_order,
            }
            ch_kanji_list.append(kanji_item)
            if char not in seen_kanji_chars:
                seen_kanji_chars.add(char)
                all_kanji.append(kanji_item)

        ch_grammar_list = []
        for g in ch.get("grammar", []):
            g_id = g.get("id") or g.get("label") or ""
            g_item = {
                "id": g_id,
                "label": g.get("label") or "",
                "meaning": g.get("meaning") or "",
                "note": g.get("note") or f"Chapter {ch_idx + 1} pattern",
                "occurrences": g.get("occurrences", 1),
                "examples": g.get("examples") or [],
            }
            ch_grammar_list.append(g_item)
            if g_id not in seen_grammar_ids:
                seen_grammar_ids.add(g_id)
                all_grammar.append(g_item)

        enriched_chapters.append({
            "index": ch.get("index", ch_idx),
            "title": ch.get("title") or f"Chapter {ch_idx + 1}",
            "spineHref": ch.get("spineHref") or "",
            "vocabulary": ch_vocab,
            "kanji": ch_kanji_list,
            "grammar": ch_grammar_list,
        })

    source_files = []
    if epub_file:
        source_files.append({"file": epub_file, "type": "epub"})
    if deck_csv_file:
        source_files.append({"file": deck_csv_file, "type": "csv_deck"})

    return {
        "$schema": "https://immersive-reading.org/schemas/learning-material.v1.json",
        "version": "1.0.0",
        "book": {
            "bookId": book_id,
            "title": title,
            "language": language,
            "writingDirection": writing_direction,
            "chapterCount": len(chapters),
            "wordCount": len(all_vocab),
            "kanjiCount": len(all_kanji),
            "grammarCount": len(all_grammar),
            "sourceFiles": source_files,
        },
        "chapters": enriched_chapters,
        "master": {
            "vocabulary": all_vocab,
            "kanji": all_kanji,
            "grammar": all_grammar,
        },
    }


def generate_from_transfer_data(
    transfer_data: dict[str, Any],
    output_dir: Path,
) -> dict[str, Path]:
    """
    Renders official A4 Study Sheets (Vocabulary, Grammar, Kanji & Combined PDF)
    and canonical vocabulary-sets.json directly from learning material transfer data.
    """
    import pymupdf
    import shutil

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    pdf_out_dir = output_dir / "pdf" / transfer_data["book"]["bookId"]
    pdf_out_dir.mkdir(parents=True, exist_ok=True)

    book = transfer_data["book"]
    book_id = book["bookId"]
    title = book["title"]
    chapters = transfer_data.get("chapters", [])

    book_meta = {
        "title": title,
        "source_files": [{"file": sf.get("file", "")} for sf in book.get("sourceFiles", [])],
        "publication": {"creator": "Immersive Reading", "publisher": "", "date": ""},
        "build_inputs": {"kanjivg": {"tag": "KanjiVG 2022"}},
        "chapters": [
            {"id": f"chapter-{ch.get('index', idx)+1:02d}", "number": ch.get('index', idx) + 1, "title": ch.get("title", f"Chapter {idx + 1}")}
            for idx, ch in enumerate(chapters)
        ],
    }

    generated_files = {}

    with pdf_session(None):
        # 1. Vocabulary Master Sheet
        vocab_groups = []
        study_num = 1
        for idx, ch in enumerate(chapters):
            words = ch.get("vocabulary", [])
            cards = []
            for w in words:
                cards.append({
                    "study_number": study_num,
                    "title": w.get("word") or w.get("lemma", ""),
                    "reading": w.get("reading", "") or "",
                    "meaning": w.get("meaning") or w.get("definition") or "",
                })
                study_num += 1
            if cards:
                heading = f"{idx + 1:02d}  {ch.get('title', f'Chapter {idx + 1}')}"
                vocab_groups.append((heading, cards))

        vocab_pdf = output_dir / "vocabulary-master.pdf"
        if vocab_groups:
            write_master(vocab_pdf, book_meta, "vocabulary", vocab_groups)
            generated_files["vocabulary_master"] = vocab_pdf
            try:
                shutil.copy2(vocab_pdf, pdf_out_dir / "vocabulary-master.pdf")
            except Exception:
                pass

        # 2. Grammar Master Sheet
        grammar_groups = []
        for idx, ch in enumerate(chapters):
            grammar_list = ch.get("grammar", [])
            cards = []
            for g in grammar_list:
                ex = g.get("examples", [""])[0] if g.get("examples") else ""
                cards.append({
                    "title": g.get("label", ""),
                    "meaning": g.get("meaning", ""),
                    "details": g.get("note", f"Pattern in Chapter {idx + 1}"),
                    "plain_example": ex,
                    "ruby": [],
                })
            if cards:
                heading = f"{idx + 1:02d}  {ch.get('title', f'Chapter {idx + 1}')}"
                grammar_groups.append((heading, cards))

        grammar_pdf = output_dir / "grammar-master.pdf"
        if grammar_groups:
            write_master(grammar_pdf, book_meta, "grammar", grammar_groups)
            generated_files["grammar_master"] = grammar_pdf
            try:
                shutil.copy2(grammar_pdf, pdf_out_dir / "grammar-master.pdf")
            except Exception:
                pass

        # 3. Kanji Master Sheet
        kanji_context = {}
        total_kanji_cards = 0
        for idx, ch in enumerate(chapters):
            cid = f"chapter-{ch.get('index', idx)+1:02d}"
            c_cards = []
            for k in ch.get("kanji", []):
                char = k.get("character", "")
                context_w = k.get("context_word") or char
                context_r = k.get("context_reading") or ""
                context_g = k.get("context_gloss") or ""
                plain_ex = k.get("sampleSentence") or context_w
                stroke_order = k.get("stroke_order")

                c_cards.append({
                    "id": f"kanji-{ord(char):x}",
                    "title": char,
                    "on": k.get("onyomi", []),
                    "kun": k.get("kunyomi", []),
                    "meaning": ", ".join(k.get("meanings", [])) if k.get("meanings") else f"Kanji ({char})",
                    "context_word": context_w,
                    "context_reading": context_r,
                    "context_gloss": context_g,
                    "context_gloss_scope": "chapter_word",
                    "context_gloss_status": "",
                    "context_gloss_provider": "EPUB/DECK",
                    "meta": f"U+{k.get('codepointHex', f'{ord(char):04X}')} · {k.get('occurrences', 1)} occurrences",
                    "plain_example": plain_ex,
                    "ruby": [],
                    "stroke_order": stroke_order,
                    "primary_meanings": k.get("meanings", []),
                    "meaning_source": {"name": "KANJIDIC2"},
                    "context_entry_id": f"v-{char}",
                    "context_definitions": context_g,
                    "context_surface": context_w,
                    "context_sources": [],
                    "source_reading_note": "",
                    "context_alignment": "aligned",
                })
                total_kanji_cards += 1
            kanji_context[cid] = c_cards

        kanji_pdf = output_dir / "kanji-master.pdf"
        if total_kanji_cards > 0:
            write_kanji_document(kanji_pdf, book_meta, context_by_chapter=kanji_context)
            generated_files["kanji_master"] = kanji_pdf
            try:
                shutil.copy2(kanji_pdf, pdf_out_dir / "kanji-master.pdf")
            except Exception:
                pass

        # 4. Merge into combined {book_id}_study_sheet.pdf
        merged_doc = pymupdf.open()
        for pdf_path in (kanji_pdf, vocab_pdf, grammar_pdf):
            if pdf_path.is_file():
                with pymupdf.open(str(pdf_path)) as part:
                    merged_doc.insert_pdf(part)

        combined_path = output_dir / f"{book_id}_study_sheet.pdf"
        merged_doc.save(str(combined_path), garbage=4, deflate=True)
        merged_doc.close()
        generated_files["combined_pdf"] = combined_path
        try:
            shutil.copy2(combined_path, pdf_out_dir / f"{book_id}_study_sheet.pdf")
        except Exception:
            pass

    return generated_files


def generate_from_transfer_file(transfer_path: Path, output_dir: Path | None = None) -> dict[str, Path]:
    """Loads a JSON transfer file and generates learning material PDFs."""
    transfer_path = Path(transfer_path)
    if not transfer_path.is_file():
        raise FileNotFoundError(f"Transfer file not found: {transfer_path}")

    data = json.loads(transfer_path.read_text(encoding="utf-8"))
    target_dir = Path(output_dir) if output_dir else transfer_path.parent
    return generate_from_transfer_data(data, target_dir)
