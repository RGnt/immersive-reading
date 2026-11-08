from __future__ import annotations

import os
from pathlib import Path
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from .stage import PipelineContext


def shape_bidirectional_text(text: str, direction: str = "ltr") -> str:
    """
    Helper for RTL languages (Arabic, Hebrew, Persian).
    Uses arabic_reshaper and python-bidi if installed in the plugin environment.
    """
    if direction != "rtl" or not text:
        return text
    try:
        import arabic_reshaper
        from bidi.algorithm import get_display

        reshaped = arabic_reshaper.reshape(text)
        return get_display(reshaped)
    except ImportError:
        return text


def register_cjk_font(preferred_path: Path | str | None = None) -> str:
    """
    Finds and registers a Japanese/CJK TrueType font with ReportLab pdfmetrics.
    Returns the font name to use in styles (e.g. "Japanese" or "Helvetica" fallback).
    """
    try:
        from reportlab.pdfbase import pdfmetrics
        from reportlab.pdfbase.ttfonts import TTFont
    except ImportError:
        return "Helvetica"

    if "Japanese" in pdfmetrics.getRegisteredFontNames():
        return "Japanese"

    candidates: list[tuple[Path, int]] = []
    if preferred_path:
        candidates.append((Path(preferred_path), 0))

    # 1. Project bundled fonts
    base_dir = Path(__file__).resolve().parent.parent.parent
    candidates.extend([
        (base_dir / "data" / "fonts" / "NotoSansJP-Regular.ttf", 0),
        (base_dir / "data" / "fonts" / "NotoSansJP-variable.ttf", 0),
        (base_dir.parent / "enrichement-extractor" / "data" / "fonts" / "NotoSansJP-Regular.ttf", 0),
    ])

    # 2. Windows system CJK fonts
    win_fonts = Path(os.environ.get("WINDIR", "C:\\Windows")) / "Fonts"
    if win_fonts.is_dir():
        candidates.extend([
            (win_fonts / "msgothic.ttc", 0),
            (win_fonts / "meiryo.ttc", 0),
            (win_fonts / "yumin.ttf", 0),
            (win_fonts / "YuGothR.ttc", 0),
            (win_fonts / "msmincho.ttc", 0),
        ])

    for path, sub_idx in candidates:
        if path.is_file():
            try:
                pdfmetrics.registerFont(TTFont("Japanese", str(path), subfontIndex=sub_idx))
                return "Japanese"
            except Exception:
                continue

    return "Helvetica"


def _build_pdf_file(
    target_path: Path,
    title: str,
    sections: list[Any],
    ctx: PipelineContext,
    font_name: str,
) -> Path | None:
    try:
        from reportlab.lib.pagesizes import A4
        from reportlab.lib.styles import getSampleStyleSheet
        from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak
    except ImportError:
        return None

    target_path.parent.mkdir(parents=True, exist_ok=True)
    doc = SimpleDocTemplate(
        str(target_path),
        pagesize=A4,
        leftMargin=36,
        rightMargin=36,
        topMargin=42,
        bottomMargin=42,
    )

    styles = getSampleStyleSheet()

    # Reconfigure sample styles with CJK-capable font and word wrapping
    for style_name in ("Title", "Heading1", "Heading2", "Heading3", "Normal", "BodyText"):
        if style_name in styles:
            styles[style_name].fontName = font_name
            styles[style_name].wordWrap = "CJK"

    story = []

    title_text = shape_bidirectional_text(
        f"{ctx.title} — {title}",
        ctx.writing_direction,
    )
    story.append(Paragraph(title_text, styles["Title"]))
    story.append(Spacer(1, 16))

    for idx, section in enumerate(sections):
        if idx > 0:
            story.append(PageBreak())
        sec_title = shape_bidirectional_text(section.title, ctx.writing_direction)
        story.append(Paragraph(sec_title, styles["Heading1"]))
        story.append(Spacer(1, 10))
        story.extend(section.build_flowables(ctx, styles))

    doc.build(story)
    return target_path


def compile_master_study_pdf(ctx: PipelineContext) -> Path | None:
    """
    Compiles master study sheets using enrichement-extractor's approved
    cover, 4-column vocabulary layout with recall check boxes, grammar cards
    with RubyText furigana, and kanji cards with practice grids.
    """
    if not ctx.include_a4_pdf or not ctx.pdf_sections:
        return None

    try:
        from reportlab.lib.pagesizes import A4
    except ImportError as exc:
        ctx.emit_progress("core.pdf_warning", f"ReportLab is not installed or available: {exc}")
        return None

    font_name = register_cjk_font()
    combined_pdf_path = ctx.output_dir / f"{ctx.book_id}_study_sheet.pdf"

    try:
        import pymupdf
        from ..pdf_elements import pdf_session
        from ..master_pdf import write_master
        from ..kanji_pdf import write_kanji_document
        from ..transfer import build_transfer_payload, get_kanjivg_stroke_orders, find_data_dir

        book_meta = {
            "title": ctx.title,
            "source_files": [{"file": ctx.epub_path.name}] + ([{"file": ctx.deck_csv_path.name}] if ctx.deck_csv_path else []),
            "publication": {"creator": "Immersive Reading", "publisher": "", "date": ""},
            "build_inputs": {"kanjivg": {"tag": "KanjiVG 2022"}},
            "chapters": [
                {"id": f"chapter-{idx+1:02d}", "number": idx + 1, "title": ch.title}
                for idx, ch in enumerate(ctx.chapters)
            ],
        }

        ch_words_map = ctx.artifacts.get("chapter_words", {})
        ch_kanji_map = ctx.artifacts.get("chapter_kanji", {})
        ch_grammar_map = ctx.artifacts.get("chapter_grammar", {})

        # 0. Batch-resolve stroke orders for all kanji
        data_dir = find_data_dir()
        all_kanji_chars = [
            k["character"]
            for ch_kanji in ch_kanji_map.values()
            for k in ch_kanji
            if k.get("character")
        ]
        stroke_orders = get_kanjivg_stroke_orders(all_kanji_chars, data_dir)

        # Build word lookup dictionary per chapter
        words_by_chapter: dict[int, dict[str, dict[str, Any]]] = {}
        for c_idx, ch_words in ch_words_map.items():
            w_dict = {}
            for w in ch_words:
                if w.get("word"):
                    w_dict[w["word"]] = w
                if w.get("lemma"):
                    w_dict[w["lemma"]] = w
            words_by_chapter[c_idx] = w_dict

        with pdf_session(None):
            # 1. Vocabulary Master Sheet (4-column with recall checkboxes & official cover)
            vocab_groups = []
            study_num = 1
            for idx, ch in enumerate(ctx.chapters):
                words = ch_words_map.get(idx, [])
                cards = []
                for w in words:
                    meaning = w.get("meaning") or w.get("definition") or w.get("partOfSpeech", "")
                    cards.append({
                        "study_number": study_num,
                        "title": w.get("word") or w.get("lemma", ""),
                        "reading": w.get("reading", "") or "",
                        "meaning": meaning,
                    })
                    study_num += 1
                if cards:
                    heading = f"{idx + 1:02d}  {ch.title}"
                    vocab_groups.append((heading, cards))

            vocab_pdf = ctx.output_dir / "vocabulary-master.pdf"
            if vocab_groups:
                write_master(vocab_pdf, book_meta, "vocabulary", vocab_groups)
                ctx.artifacts["vocabulary_master_pdf"] = vocab_pdf

            # 2. Grammar Master Sheet (Pattern cards with RubyText & official cover)
            grammar_groups = []
            for idx, ch in enumerate(ctx.chapters):
                grammar_list = ch_grammar_map.get(idx, [])
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
                    heading = f"{idx + 1:02d}  {ch.title}"
                    grammar_groups.append((heading, cards))

            grammar_pdf = ctx.output_dir / "grammar-master.pdf"
            if grammar_groups:
                write_master(grammar_pdf, book_meta, "grammar", grammar_groups)
                ctx.artifacts["grammar_master_pdf"] = grammar_pdf

            # 3. Kanji Master Sheet (card_flowable with 35pt glyphs, ON/KUN, meanings, practice grid)
            # Strict invariant: Only kanji from the book and chapter, first-appearance only (no duplicates)
            kanji_context = {}
            total_kanji_cards = 0
            seen_master_kanji = set()
            for idx, ch in enumerate(ctx.chapters):
                cid = f"chapter-{idx+1:02d}"
                kanji_list = ch_kanji_map.get(idx, [])
                cards = []
                c_words = words_by_chapter.get(idx, {})
                for k in kanji_list:
                    char = k.get("character", "")
                    if not char or char in seen_master_kanji:
                        continue
                    seen_master_kanji.add(char)

                    context_words = k.get("words", [])
                    context_w = context_words[0] if context_words else char
                    w_info = c_words.get(context_w, {})
                    context_r = k.get("context_reading") or w_info.get("reading") or ""
                    context_g = k.get("context_gloss") or w_info.get("meaning") or w_info.get("definition") or f"Occurrences: {k.get('occurrences', 1)}"
                    plain_ex = k.get("sampleSentence") or w_info.get("sampleSentence") or context_w
                    stroke_data = stroke_orders.get(char)

                    cards.append({
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
                        "stroke_order": stroke_data,
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
                kanji_context[cid] = cards

            kanji_pdf = ctx.output_dir / "kanji-master.pdf"
            if total_kanji_cards > 0:
                write_kanji_document(kanji_pdf, book_meta, context_by_chapter=kanji_context)
                ctx.artifacts["kanji_master_pdf"] = kanji_pdf

            # 4. Master Study Sheet: merge all generated master sheets using PyMuPDF
            merged = pymupdf.open()
            for doc_path in (kanji_pdf, vocab_pdf, grammar_pdf):
                if doc_path.is_file():
                    with pymupdf.open(str(doc_path)) as part:
                        merged.insert_pdf(part)

            if len(merged) > 0:
                merged.set_metadata({"title": f"{ctx.title} - Master Study Pack", "author": "Immersive Reading"})
                merged.save(str(combined_pdf_path), garbage=4, deflate=True)
                merged.close()
                ctx.artifacts["master_pdf"] = combined_pdf_path
            else:
                _build_pdf_file(
                    combined_pdf_path,
                    f"Master Study Pack ({ctx.language.upper()})",
                    ctx.pdf_sections,
                    ctx,
                    font_name,
                )
                ctx.artifacts["master_pdf"] = combined_pdf_path

    except Exception as exc:
        ctx.emit_progress("core.pdf_warning", f"Template PDF compilation error, using flowable fallback: {exc}")
        _build_pdf_file(
            combined_pdf_path,
            f"Master Study Pack ({ctx.language.upper()})",
            ctx.pdf_sections,
            ctx,
            font_name,
        )
        ctx.artifacts["master_pdf"] = combined_pdf_path

    # Replicate to legacy pdf dir (output/pdf/{bookId}/)
    try:
        if ctx.output_dir.parent.name == "data":
            legacy_pdf_dir = ctx.output_dir.parent.parent / "pdf" / ctx.book_id
            legacy_pdf_dir.mkdir(parents=True, exist_ok=True)
            for f in ctx.output_dir.glob("*.pdf"):
                dest = legacy_pdf_dir / f.name
                dest.write_bytes(f.read_bytes())
    except Exception:
        pass

    return combined_pdf_path
