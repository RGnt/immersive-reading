from __future__ import annotations

import json
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Protocol


@dataclass
class ChapterDocument:
    index: int
    title: str
    spine_href: str
    text: str


@dataclass
class ExtractedToken:
    surface: str
    lemma: str
    reading: str | None = None
    pos: str = "UNK"
    chapter_index: int = 0
    sentence_text: str = ""
    features: dict[str, Any] = field(default_factory=dict)


@dataclass
class StudyItem:
    key: str                                     # Unique lemma or root key
    headword: str                                # Display headword
    reading: str | None = None                   # Kana, IPA, or vocalized Tashkeel
    pos: str = ""
    glosses: list[str] = field(default_factory=list)
    frequency_rank: int | None = None
    level_tag: str | None = None                 # e.g., "JLPT N2", "CEFR B2", "Form III"
    book_occurrences: int = 0
    sample_sentences: list[str] = field(default_factory=list)
    metadata: dict[str, Any] = field(default_factory=dict)


class PdfSectionRenderer(Protocol):
    """Protocol implemented by stage-contributed PDF sections (Order 400-499)."""
    section_id: str
    title: str

    def build_flowables(self, ctx: PipelineContext, style_sheet: Any) -> list[Any]:
        ...


@dataclass
class PipelineContext:
    book_id: str
    title: str
    language: str
    epub_path: Path
    output_dir: Path
    deck_csv_path: Path | None
    include_a4_pdf: bool
    plugin_configs: dict[str, dict[str, Any]]
    writing_direction: str = "ltr"               # "ltr" | "rtl" | "ttb"

    # Mutable state populated progressively across stages:
    chapters: list[ChapterDocument] = field(default_factory=list)
    tokens: list[ExtractedToken] = field(default_factory=list)
    study_items: dict[str, StudyItem] = field(default_factory=dict)
    flashcards: list[dict[str, Any]] = field(default_factory=list)
    pdf_sections: list[PdfSectionRenderer] = field(default_factory=list)
    artifacts: dict[str, Path] = field(default_factory=dict)
    shared_state: dict[str, Any] = field(default_factory=dict)

    def emit_progress(self, stage_id: str, message: str, percent: int | None = None) -> None:
        payload = {
            "event": "stage_progress",
            "stageId": stage_id,
            "message": message,
            "percent": percent,
        }
        print(json.dumps(payload, ensure_ascii=False), flush=True)


class PipelineStage(ABC):
    """Base class that all plugin Python stages must inherit from."""

    @abstractmethod
    def execute(
        self,
        ctx: PipelineContext,
        plugin_config: dict[str, Any],
        plugin_data_dir: Path,
    ) -> None:
        """Executes the stage, mutating `ctx` in-place."""
        pass
