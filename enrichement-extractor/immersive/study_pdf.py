"""Shared reading-list and compact dictionary-gloss helpers."""
import html
import json
import math
import re
from pathlib import Path
from reportlab.pdfgen.canvas import Canvas
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.platypus import Paragraph, Table, TableStyle, KeepInFrame, SimpleDocTemplate, PageBreak, Spacer

INK = colors.HexColor("#23362f")
LINE = colors.HexColor("#c5d0c3")


def paragraph(text, kind="body"):
    styles = {
        "body": ("Helvetica", 9, 12, 2), "label": ("Helvetica", 6.5, 8, 2),
        "reading": ("Japanese", 9, 11, 0), "glyph": ("Japanese", 44, 48, 0),
        "heading": ("Japanese", 14, 19, 4), "word": ("Japanese", 12, 16, 2),
    }
    font, size, leading, after = styles[kind]
    value = html.escape(str(text))
    if font == "Helvetica":
        value = re.sub(r"([^\x00-\x7f]+)", r'<font name="Japanese">\1</font>', value)
    style = ParagraphStyle(kind, fontName=font, fontSize=size, leading=leading, spaceAfter=after,
                           textColor=INK, wordWrap="CJK" if font == "Japanese" else None)
    return Paragraph(value, style)


def footer(c, page_number):
    c.setFillColor(colors.HexColor("#647267")); c.setFont("Helvetica", 6)
    c.drawString(14*mm, 10*mm, "EPUB examples / Jiten glosses / KANJIDIC2 (EDRDG, CC BY-SA 4.0)")
    c.drawString(14*mm, 6.5*mm, "Stroke diagrams: KanjiVG / Ulrich Apel / CC BY-SA 3.0 / github.com/KanjiVG/kanjivg")
    c.drawRightString(196*mm, 9*mm, str(page_number))


def reading_list(values, width):
    """Long lists flow down compact columns within the shared readings cell."""
    if not values:
        return paragraph("None listed", "label")
    columns = max(1, math.ceil(len(values)/5))
    rows = math.ceil(len(values)/columns)
    cells = [[paragraph("- " + values[col*rows+row], "reading") if col*rows+row < len(values) else ""
              for col in range(columns)] for row in range(rows)]
    table = Table(cells, colWidths=[width/columns]*columns)
    table.setStyle(TableStyle([("VALIGN", (0, 0), (-1, -1), "TOP"),
                               ("LEFTPADDING", (0, 0), (-1, -1), 0), ("RIGHTPADDING", (0, 0), (-1, -1), 3),
                               ("TOPPADDING", (0, 0), (-1, -1), 0), ("BOTTOMPADDING", (0, 0), (-1, -1), 0)]))
    return table


def gloss_excerpt(text, limit=120):
    text = re.sub(r",\s*", ", ", text)
    if len(text) <= limit:
        return text
    cut = text.rfind(",", 60, limit)
    if cut < 0:
        cut = text.rfind(" ", 60, limit)
    return text[:cut if cut > 0 else limit].rstrip(", ") + "…"


