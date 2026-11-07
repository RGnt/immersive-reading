"""Approved source-grounded kanji layout and shared print cover styling."""
import json
import re
from pathlib import Path
from reportlab.pdfgen.canvas import Canvas
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.platypus import Paragraph, Table, TableStyle, Spacer
from .build import hira
from .pdf_elements import RubyText, StrokeOrder, PracticeGrid
from .study_pdf import reading_list
from .pdf_charts import append_charts
from .jobs import check_cancelled


from .context import source_reading, compatible_reading, deck_glosses, contextual_cards

def p(text, style="body"):
    from html import escape
    styles = {
        "body": ("Helvetica", 9, 11, 2), "label": ("Helvetica", 6.5, 8, 2),
        "reading": ("Japanese", 9, 11, 1), "glyph": ("Japanese", 35, 40, 0),
        "heading": ("Japanese", 12, 17, 2), "cover": ("Japanese", 22, 30, 8),
    }
    font, size, leading, after = styles[style]
    value = escape(str(text))
    if font == "Helvetica":
        value = re.sub(r"([^\x00-\x7f]+)", r'<font name="Japanese">\1</font>', value)
    return Paragraph(value, ParagraphStyle(style, fontName=font, fontSize=size, leading=leading,
                                          spaceAfter=after, textColor=colors.black, wordWrap="CJK"))


def card_flowable(card, width):
    glyph_width, readings_width = 21*mm, 69*mm
    meaning_width = width-glyph_width-readings_width
    readings = []
    for label, values in (("ON'YOMI", card["on"]), ("KUN'YOMI", card["kun"])):
        readings += [p(label, "label"), reading_list(values, readings_width-8)]
    meaning_source = "KANJI ALIVE" if card.get("meaning_source", {}).get("name") == "Kanji alive" else "KANJIDIC2"
    source_word = [p(f"KANJI MEANINGS ({meaning_source})", "label"), p(card["meaning"] or "Meaning unavailable"), Spacer(1, 3),
                   p("CHAPTER WORD / READING", "label"), p(card["context_word"] + "【" + card["context_reading"] + "】", "reading")]
    if card.get("context_gloss_scope") != "character_dictionary":
        status = card.get("context_gloss_status", "")
        review = " / CANDIDATES" if status == "ambiguous_dictionary_candidates" else " / UNREVIEWED" if status == "dictionary_gloss_excerpt_not_sense_disambiguated" else ""
        source_word += [p("WORD GLOSS (" + card.get("context_gloss_provider", "DECK") + review + ")", "label"), p(card["context_gloss"])]
    metadata = card["meta"].replace("First appearance · ", "").split(" · ")
    left = [p(card["title"], "glyph"), p(metadata[0], "label"),
            p(" · ".join(metadata[1:]).replace("radical ", "R").replace("grade ", "G"), "label")]
    rows = [[left, readings, source_word],
            [RubyText(card["plain_example"], card["ruby"]), "", ""],
            [StrokeOrder(card["stroke_order"]) if card.get("stroke_order") else p("Stroke-order data unavailable.", "label"), "", ""],
            [PracticeGrid(), "", ""]]
    table = Table(rows, colWidths=[glyph_width, readings_width, meaning_width], hAlign="LEFT")
    table.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"),
                               ("SPAN", (0, 1), (-1, 1)), ("SPAN", (0, 2), (-1, 2)), ("SPAN", (0, 3), (-1, 3)),
                               ("BOX", (0, 0), (-1, -1), .6, colors.HexColor("#777777")),
                               ("LINEBELOW", (0, 0), (-1, 0), .3, colors.HexColor("#b5b5b5")),
                               ("LINEBELOW", (0, 2), (-1, 2), .5, colors.HexColor("#777777")),
                               ("LINEAFTER", (0, 0), (1, 0), .3, colors.HexColor("#b5b5b5")),
                               ("LEFTPADDING", (0, 0), (-1, -1), 4), ("RIGHTPADDING", (0, 0), (-1, -1), 4),
                               ("TOPPADDING", (0, 0), (-1, -1), 4), ("BOTTOMPADDING", (0, 0), (-1, -1), 4)]))
    return table


def draw_blocks(canvas, blocks, x, top, width):
    y = top
    for block in blocks:
        _, h = block.wrap(width, 10000)
        y -= h
        block.drawOn(canvas, x, y)
        y -= block.getSpaceAfter()
    return y


def cover(canvas, book, chapter, count, kind="kanji"):
    x, width, top = 16*mm, 178*mm, 276*mm
    files = book["source_files"]
    description = {"kanji": "new kanji", "vocabulary": "new words", "grammar": "grammar candidates"}[kind]
    heading = f'Chapter {chapter["number"]:02d}  {chapter["title"]}' if chapter else f'{kind.title()} master'
    usage = {"kanji": "Kanji meanings come from Kanji alive, with KANJIDIC2 fallback for uncovered characters. Chapter words, readings and deck word meanings are labelled separately; a word sense is not a character definition. Japanese examples remain untranslated. Each bordered card includes practice below the stroke diagrams.",
             "vocabulary": "Study 20 new words per set in first-appearance order across the book. The final set may contain fewer words. Use the recall checkboxes while studying. Meaning columns are excerpts; full definitions and Japanese examples are available in the accompanying HTML pages and JSON data.",
             "grammar": "Patterns are grouped by first detected chapter. Each entry has a construction, an English explanation and a Japanese example with source furigana. Finite rule-based detection is not exhaustive; candidates require contextual review."}[kind]
    blocks = [p("IMMERSIVE READING", "label"), p(book["title"], "cover"),
              p(heading, "heading"),
              p(f'{count} {description} · First-appearance study material'),
              p(" · ".join(value for value in [book.get("publication", {}).get("creator", ""), book.get("publication", {}).get("publisher", ""), book.get("publication", {}).get("date", "")] if value)),
              Spacer(1, 7*mm),
              p("HOW TO USE", "label"),
              p(usage),
              p("CSV examples are used first; otherwise the first chapter occurrence is used. Story sentences remain Japanese. Original book furigana is preserved; missing furigana is generated offline with Sudachi and may need review for names or ambiguous readings. Added readings are identified in JSON."),
              Spacer(1, 6*mm), p("SOURCE MATERIAL", "label")]
    blocks += [p(f'{"EPUB" if f["file"].endswith(".epub") else "Jiten.moe Vocabulary CSV"}: {f["file"]}') for f in files]
    blocks += [p("Examples and furigana: the supplied EPUB. Vocabulary words, kana and word glosses: the CSV deck or supplied Yomitan dictionaries. Kanji meanings: Kanji alive, with KANJIDIC2 fallback. Dictionary readings: KANJIDIC2. Stroke paths: KanjiVG. Grammar: curated rules."),
               Spacer(1, 6*mm), p("COPYRIGHT / LICENSE NOTICES", "label"),
               p("Kanji alive meanings: copyright Harumi Hibino Lory & Arno Bosse, Creative Commons Attribution 4.0. https://kanjialive.com/credits/"),
               p("JMdict and KANJIDIC2: copyright James William Breen and the Electronic Dictionary Research and Development Group (EDRDG). Dictionary fields are used under Creative Commons Attribution-ShareAlike 4.0. English glosses and readings in the supplied Jiten.moe Vocabulary export retain their source attribution."),
               p("https://www.edrdg.org/edrdg/licence.html"),
               p("KanjiVG: copyright Ulrich Apel, Creative Commons Attribution-ShareAlike 3.0. The cumulative stroke diagrams are adaptations of the original SVG paths; the adapted graphics retain this license and attribution."),
               p("https://github.com/KanjiVG/kanjivg · https://creativecommons.org/licenses/by-sa/3.0/"),
               p("Source release: " + book["build_inputs"]["kanjivg"].get("tag", "cached KanjiVG archive")),
               p("Hiragana Chart and Katakana Chart: copyright Tofugu. Original chart attribution is retained on both appended chart pages."),
               p("EPUB text remains copyright its respective rights holders. Dictionary and diagram licenses do not grant redistribution rights to the book text. This pack is generated from your supplied sources for local study."),
               Spacer(1, 6*mm), p("PRINTING", "label"),
               p("A4 portrait. White paper background, no shaded cards or table rows. Kanji sheets have one separate practice row per character. Only first-appearance study entries are included.")]
    if book.get("dictionary_sources"):
        notices = "; ".join(d["index"]["title"] + " (" + d["index"]["revision"] + "): " +
                            d["index"].get("attribution", "See source notices") for d in book["dictionary_sources"])
        blocks[-3:-3] = [p("Yomitan dictionaries: " + notices[:360] + ("..." if len(notices) > 360 else "") +
                           ". Each dictionary retains its own rights. Full supplied notices are in dictionary_sources in the accompanying reader JSON.")]
    configured = book.get("build_inputs", {}).get("book_config", {}).get("print_context", {})
    supplements = [value for cid, rows in configured.items() for value in rows.values() if value.get("source") and (chapter is None or cid == chapter["id"])]
    for item in supplements:
        blocks += [p("Supplemental word gloss: " + item.get("surface", "") + " / " + item.get("definitions", "") + ". " + item["source"].get("name", "Configured source")), p(item["source"].get("url", ""))]
    bottom = draw_blocks(canvas, blocks, x, top, width)
    if bottom < 18*mm:
        raise ValueError("Cover content exceeds printable page")
    canvas.showPage()
    # Leave the reverse of the cover blank for double-sided printing.
    # Shared by chapter kanji PDFs and all three master types.
    canvas.showPage()


def draw_chapter(canvas, chapter, cards, *, cancel=None):
    width, x, top = 182*mm, 14*mm, 272*mm
    bottom, gap = 18*mm, 7*mm
    pages = []
    cursor = 0
    while cursor < len(cards):
        check_cancelled(cancel)
        packed, used = [], 0
        for card in cards[cursor:cursor+4]:
            flow = card_flowable(card, width)
            _, height = flow.wrapOn(canvas, width, 10000)
            required = height + (gap if packed else 0)
            if packed and used+required > top-bottom:
                break
            if not packed and height > top-bottom:
                raise ValueError(f'Card {card["title"]} does not fit a page')
            packed.append((card, flow, height))
            used += required
        heading = f'{chapter["number"]:02d}  {chapter["title"]}'
        draw_blocks(canvas, [p(heading, "heading")], x, 286*mm, width)
        y = top
        entries = []
        for card, flow, height in packed:
            y -= height
            flow.drawOn(canvas, x, y)
            entries.append({"id": card["id"], "character": card["title"], "context_word": card["context_word"],
                            "context_reading": card["context_reading"], "context_gloss": card["context_gloss"],
                            "source_deck_rows": card["context_sources"], "alignment": card["context_alignment"],
                            "bbox": [x, y, x+width, y+height],
                            "practice_bbox": [x+4, y+4, x+width-4, y+4+14*mm],
                            "outer_border": True})
            y -= gap
        canvas.setFillColor(colors.black);canvas.setFont("Helvetica", 7)
        canvas.drawRightString(196*mm, 9*mm, str(canvas.getPageNumber()-2))
        pages.append({"pdf_page": canvas.getPageNumber(), "chapter_id": chapter["id"], "entries": entries, "practice_rows": len(packed)})
        cursor += len(packed)
        canvas.showPage()
    return pages


def write_kanji_document(path, book, chapter=None, metadata_dir=None, context_by_chapter=None, *, cancel=None, charts=()):
    chapters = [chapter] if chapter else book["chapters"]
    groups = [(ch, context_by_chapter[ch["id"]] if context_by_chapter else contextual_cards(book, ch)) for ch in chapters]
    cards = [card for _, items in groups for card in items]
    canvas = Canvas(str(path), pagesize=(210*mm, 297*mm))
    canvas.setTitle(f'{book["title"]} - ' + (f'Chapter {chapter["number"]:02d}' if chapter else "Kanji master"))
    canvas.setAuthor("Immersive Reading")
    cover(canvas, book, chapter, len(cards))
    pages = []
    for ch, items in groups:
        check_cancelled(cancel)
        pages.extend(draw_chapter(canvas, ch, items, cancel=cancel))
    appendices = append_charts(canvas, charts)
    canvas.save()
    manifest = {"layout": "kanji-with-cover-and-card-practice", "total_entries": len(cards), "cover_pages": 1, "blank_pages": 1, "pages": pages, "appendices": appendices}
    metadata_dir = metadata_dir or path.parent
    metadata_dir.mkdir(parents=True, exist_ok=True)
    (metadata_dir / (path.stem+".layout.json")).write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    (metadata_dir / (path.stem+".sources.json")).write_text(json.dumps([{k: card[k] for k in ("id", "title", "primary_meanings", "meaning_source", "context_entry_id", "context_word", "context_reading", "context_gloss", "context_definitions", "context_surface", "context_sources", "source_reading_note")} for card in cards], ensure_ascii=False, indent=2), encoding="utf-8")
    return manifest


