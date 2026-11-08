from __future__ import annotations

from pathlib import Path
from typing import TYPE_CHECKING

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


def compile_master_study_pdf(ctx: PipelineContext) -> Path | None:
    if not ctx.include_a4_pdf or not ctx.pdf_sections:
        return None

    try:
        from reportlab.lib.pagesizes import A4
        from reportlab.lib.styles import getSampleStyleSheet
        from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, PageBreak
    except ImportError as exc:
        ctx.emit_progress("core.pdf_warning", f"ReportLab is not installed or available: {exc}")
        return None

    pdf_path = ctx.output_dir / f"{ctx.book_id}_study_sheet.pdf"
    doc = SimpleDocTemplate(
        str(pdf_path),
        pagesize=A4,
        leftMargin=36,
        rightMargin=36,
        topMargin=42,
        bottomMargin=42,
    )

    styles = getSampleStyleSheet()
    story = []

    title_text = shape_bidirectional_text(
        f"{ctx.title} — Study Pack ({ctx.language.upper()})",
        ctx.writing_direction,
    )
    story.append(Paragraph(title_text, styles["Title"]))
    story.append(Spacer(1, 16))

    for idx, section in enumerate(ctx.pdf_sections):
        if idx > 0:
            story.append(PageBreak())
        sec_title = shape_bidirectional_text(section.title, ctx.writing_direction)
        story.append(Paragraph(sec_title, styles["Heading1"]))
        story.append(Spacer(1, 10))
        story.extend(section.build_flowables(ctx, styles))

    doc.build(story)
    ctx.artifacts["master_pdf"] = pdf_path
    return pdf_path
