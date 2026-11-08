"""White-background master sheets sharing the approved source/license cover."""
from io import BytesIO
from reportlab.pdfgen.canvas import Canvas
from reportlab.lib import colors
from reportlab.lib.units import mm
from reportlab.platypus import SimpleDocTemplate, Paragraph, Table, TableStyle, PageBreak, KeepInFrame, Spacer, KeepTogether
import pymupdf
from .kanji_pdf import cover, p
from .pdf_elements import RubyText
from .study_pdf import gloss_excerpt
from .pdf_charts import append_charts
from .jobs import check_cancelled


def write_master(path, book, kind, groups, chapter=None, *, cancel=None, charts=()):
    cover_buffer, body_buffer = BytesIO(), BytesIO()
    canvas = Canvas(cover_buffer, pagesize=(210*mm, 297*mm))
    count = sum(len(cards) for _, cards in groups)
    cover(canvas, book, chapter, count, kind)
    canvas.save()
    story = []
    for heading, cards in groups:
        check_cancelled(cancel)
        if not cards:
            continue
        if story and chapter is None:
            story.append(PageBreak())
        elif story:
            story.append(Spacer(1, 5*mm + 1.5*12))
        heading_paragraph = p(heading, "heading")
        heading_paragraph.keepWithNext = True
        story += [heading_paragraph, Spacer(1, 4*mm - (8 if chapter and kind == "vocabulary" else 0))]
        if kind == "vocabulary":
            rows = [[p(t, "label") for t in ("Nr.", "WORD", "READING", "MEANING")]]
            for card in cards:
                number = p(f'{card["study_number"]}  [  ]', "label")
                # Paragraph normally collapses spaces; non-breaking spaces keep
                # the recall box wide enough to mark when printed.
                number = Paragraph(f'{card["study_number"]}&nbsp;&nbsp;[&nbsp;&nbsp;&nbsp;&nbsp;]', number.style)
                rows.append([number, p(card["title"], "reading"),
                             p(card["reading"], "reading"), p(gloss_excerpt(card["meaning"]))])
            table = Table(rows, colWidths=[19*mm, 34*mm, 35*mm, 94*mm], repeatRows=1, hAlign="LEFT")
            table.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"),
                                       ("LINEBELOW", (0, 0), (-1, -1), .35, colors.HexColor("#aaaaaa")),
                                       ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5),
                                       ("TOPPADDING", (0, 0), (-1, -1), 5), ("BOTTOMPADDING", (0, 0), (-1, -1), 5)]))
            # Multi-page vocabulary lists must flow naturally across pages; KeepInFrame(shrink)
            # is reserved only for single-page 20-word sets (len <= 25) so text never shrinks into a tiny sliver.
            story.append(table if chapter or len(cards) > 25 else KeepInFrame(182*mm, 245*mm, [table], mode="shrink", hAlign="LEFT"))
        else:
            for card in cards:
                cell = [p(card["title"], "heading"), p(card["meaning"]), p(card["details"]),
                        RubyText(card["plain_example"], card.get("ruby", []))]
                table = Table([[cell]], colWidths=[182*mm], hAlign="LEFT")
                table.setStyle(TableStyle([("LINEBELOW", (0, 0), (-1, -1), .5, colors.HexColor("#777777")),
                                           ("TOPPADDING", (0, 0), (-1, -1), 6), ("BOTTOMPADDING", (0, 0), (-1, -1), 8),
                                           ("LEFTPADDING", (0, 0), (-1, -1), 4), ("RIGHTPADDING", (0, 0), (-1, -1), 4)]))
                table.spaceAfter = 4*mm
                story.append(table)
    if not story:
        story = [p("No new entries detected.")]
    def on_page(canvas, doc):
        check_cancelled(cancel)
        canvas.setFont("Helvetica", 7)
        canvas.drawRightString(196*mm, 9*mm, str(doc.page))
    SimpleDocTemplate(body_buffer, pagesize=(210*mm, 297*mm), leftMargin=14*mm, rightMargin=14*mm,
                      topMargin=14*mm, bottomMargin=17*mm).build(story, onFirstPage=on_page, onLaterPages=on_page)
    result = pymupdf.open()
    with pymupdf.open(stream=cover_buffer.getvalue(), filetype="pdf") as c:
        result.insert_pdf(c)
    with pymupdf.open(stream=body_buffer.getvalue(), filetype="pdf") as body:
        result.insert_pdf(body)
    chart_buffer = BytesIO()
    chart_canvas = Canvas(chart_buffer, pagesize=(210*mm, 297*mm))
    if append_charts(chart_canvas, charts):
        chart_canvas.save()
        with pymupdf.open(stream=chart_buffer.getvalue(), filetype="pdf") as charts:
            result.insert_pdf(charts)
    label = f'Chapter {chapter["number"]:02d} {kind.title()}' if chapter else kind.title()+" master"
    result.set_metadata({"title": book["title"]+" - "+label, "author": "Immersive Reading"})
    result.save(path, garbage=4, deflate=True)
    result.close()


def write_chapter_vocabulary(path, book, chapter, cards, metadata_dir, *, cancel=None, charts=()):
    """One chapter file, flowing through learning-set headings without page breaks."""
    import json
    groups = [(f'Ch {chapter["number"]} Set {number}',
               [card for card in cards if card["set_number"] == number])
              for number in dict.fromkeys(card["set_number"] for card in cards)]
    write_master(path, book, "vocabulary", groups, chapter=chapter, cancel=cancel, charts=charts)
    metadata_dir.mkdir(parents=True, exist_ok=True)
    (metadata_dir/(path.stem+".vocabulary.json")).write_text(json.dumps({
        "chapter_id": chapter["id"], "card_ids": [card["id"] for card in cards],
        "words": [card["title"] for card in cards]}, ensure_ascii=False, indent=2), encoding="utf-8")
