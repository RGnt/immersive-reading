from __future__ import annotations

import re
from pathlib import Path
from typing import Any

from immersive.sdk.stage import (
    ExtractedToken,
    PipelineContext,
    PipelineStage,
    StudyItem,
)


class JapaneseSudachiTokenizerStage(PipelineStage):
    """
    Order 110: Tokenizes chapter text using SudachiPy (Mode C) or managed fallback.
    Initializes unique StudyItem records and counts book occurrences.
    """

    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        ctx.emit_progress("ja.sudachi_tokenize", "Initializing Japanese tokenizer...", 15)

        tokenizer_obj = None
        try:
            from sudachipy import Dictionary, SplitMode

            sudachi_dic = Dictionary()
            tokenizer_obj = sudachi_dic.create()
            split_mode = SplitMode.C
        except ImportError:
            pass

        ctx.writing_direction = "ttb" if plugin_config.get("verticalTypography", False) else "ltr"

        for ch_idx, chapter in enumerate(ctx.chapters):
            text = chapter.text or ""
            if not text:
                continue

            # Split into simple sentence boundaries
            sentences = [s.strip() for s in re.split(r"[。！？\n]+", text) if s.strip()]

            for sent in sentences:
                if tokenizer_obj is not None:
                    morphemes = tokenizer_obj.tokenize(sent, split_mode)
                    for m in morphemes:
                        surface = m.surface()
                        lemma = m.dictionary_form()
                        reading = m.reading_form()
                        pos_tuple = m.part_of_speech()
                        pos = pos_tuple[0] if pos_tuple else "UNK"

                        # Skip pure whitespace / symbols
                        if not surface.strip() or pos in ("補助記号", "空白"):
                            continue

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
                            },
                        )
                        ctx.tokens.append(token)

                        # Track unique study item by lemma
                        item = ctx.study_items.get(lemma)
                        if item is None:
                            item = StudyItem(
                                key=lemma,
                                headword=surface,
                                reading=reading,
                                pos=pos,
                                book_occurrences=1,
                                sample_sentences=[sent],
                            )
                            ctx.study_items[lemma] = item
                        else:
                            item.book_occurrences += 1
                            if len(item.sample_sentences) < 3 and sent not in item.sample_sentences:
                                item.sample_sentences.append(sent)
                else:
                    # Fallback word-level extraction
                    words = re.findall(r"[\u4e00-\u9faf\u3040-\u309f\u30a0-\u30ff]+", sent)
                    for w in words:
                        token = ExtractedToken(
                            surface=w,
                            lemma=w,
                            reading=None,
                            pos="Noun",
                            chapter_index=ch_idx,
                            sentence_text=sent,
                        )
                        ctx.tokens.append(token)
                        item = ctx.study_items.get(w)
                        if item is None:
                            item = StudyItem(
                                key=w,
                                headword=w,
                                reading=None,
                                pos="Word",
                                book_occurrences=1,
                                sample_sentences=[sent],
                            )
                            ctx.study_items[w] = item
                        else:
                            item.book_occurrences += 1
                            if len(item.sample_sentences) < 3 and sent not in item.sample_sentences:
                                item.sample_sentences.append(sent)


class JapaneseKanjiEnrichmentStage(PipelineStage):
    """
    Order 210: Identifies kanji in vocabulary items, tags frequency and level metadata.
    """

    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        ctx.emit_progress("ja.kanji_enrichment", "Enriching Japanese kanji metadata...", 50)

        for key, item in ctx.study_items.items():
            kanji_chars = [c for c in item.headword if "\u4e00" <= c <= "\u9faf"]
            if kanji_chars:
                item.metadata["kanji_characters"] = kanji_chars
                item.metadata["kanji_count"] = len(kanji_chars)
                if len(kanji_chars) == 1:
                    item.level_tag = "Single Kanji"
                else:
                    item.level_tag = "Compound (Jukugo)"


class JapaneseStudySectionRenderer:
    """PDF Section Renderer for Japanese Study Sheets."""

    section_id = "ja.vocabulary_table"
    title = "Core Japanese Vocabulary & Sentences"

    def build_flowables(self, ctx: PipelineContext, style_sheet: Any) -> list[Any]:
        try:
            from reportlab.platypus import Paragraph, Spacer, Table, TableStyle
            from reportlab.lib import colors
        except ImportError:
            return []

        story = []
        story.append(Paragraph("Target Vocabulary extracted from EPUB text:", style_sheet["Normal"]))
        story.append(Spacer(1, 10))

        # Take top 30 vocabulary items sorted by occurrences
        top_items = sorted(
            ctx.study_items.values(),
            key=lambda x: x.book_occurrences,
            reverse=True,
        )[:30]

        table_data = [["Headword", "Reading", "Occurrences", "Example Sentence"]]
        for it in top_items:
            reading = it.reading or ""
            example = it.sample_sentences[0] if it.sample_sentences else ""
            if len(example) > 50:
                example = example[:47] + "..."
            table_data.append([it.headword, reading, str(it.book_occurrences), example])

        t = Table(table_data, colWidths=[90, 80, 70, 240])
        t.setStyle(
            TableStyle(
                [
                    ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#2d3748")),
                    ("TEXTCOLOR", (0, 0), (-1, 0), colors.whitesmoke),
                    ("FONTNAME", (0, 0), (-1, -1), "Helvetica"),
                    ("FONTSIZE", (0, 0), (-1, -1), 9),
                    ("BOTTOMPADDING", (0, 0), (-1, 0), 6),
                    ("GRID", (0, 0), (-1, -1), 0.5, colors.HexColor("#cbd5e0")),
                ]
            )
        )
        story.append(t)
        return story


class JapanesePdfSectionsStage(PipelineStage):
    """
    Order 410: Contributes Japanese study tables to the master A4 PDF.
    """

    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        ctx.emit_progress("ja.pdf_sections", "Registering Japanese PDF section...", 80)
        ctx.pdf_sections.append(JapaneseStudySectionRenderer())

