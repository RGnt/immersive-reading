from __future__ import annotations

import importlib.util
import json
import sys
from pathlib import Path
from typing import Any

from .epub import read_epub
from .sdk.stage import ChapterDocument, PipelineContext, PipelineStage
from .sdk.pdf import compile_master_study_pdf


def extract_epub_chapters(epub_path: Path) -> list[ChapterDocument]:
    if not epub_path.exists():
        raise FileNotFoundError(f"EPUB file not found: {epub_path}")

    book = read_epub(epub_path)
    chapters: list[ChapterDocument] = []
    for idx, ch in enumerate(book.get("chapters", [])):
        chapters.append(
            ChapterDocument(
                index=ch.get("index", idx),
                title=ch.get("title", f"Chapter {idx + 1}"),
                spine_href=ch.get("spine_href", ""),
                text=ch.get("text", ""),
            )
        )
    return chapters


def load_stage_instance(script_path: Path, class_name: str) -> PipelineStage:
    if not script_path.exists():
        raise FileNotFoundError(f"Plugin stage script not found: {script_path}")

    script_dir = str(script_path.parent.resolve())
    if script_dir not in sys.path:
        sys.path.insert(0, script_dir)

    module_name = f"ir_plugin_stage_{script_path.stem}_{abs(hash(str(script_path.resolve())))}"
    spec = importlib.util.spec_from_file_location(module_name, str(script_path.resolve()))
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not create module spec for {script_path}")

    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)

    cls = getattr(module, class_name, None)
    if cls is None:
        raise AttributeError(f"Class '{class_name}' was not found in {script_path}")

    if not issubclass(cls, PipelineStage):
        raise TypeError(f"Class '{class_name}' in {script_path} must subclass PipelineStage.")

    return cls()


def run_pipeline_from_manifest(manifest_path: Path) -> PipelineContext:
    if not manifest_path.exists():
        raise FileNotFoundError(f"Pipeline manifest not found: {manifest_path}")

    manifest: dict[str, Any] = json.loads(manifest_path.read_text(encoding="utf-8"))

    # 1. Prepend extra Python paths (shared plugin pip packages)
    for extra_path in reversed(manifest.get("extraPythonPaths", [])):
        if extra_path and str(Path(extra_path).resolve()) not in sys.path:
            sys.path.insert(0, str(Path(extra_path).resolve()))

    output_dir = Path(manifest["outputDirectory"])
    output_dir.mkdir(parents=True, exist_ok=True)

    ctx = PipelineContext(
        book_id=manifest["bookId"],
        title=manifest["title"],
        language=manifest.get("language", "ja"),
        epub_path=Path(manifest["epubFilePath"]),
        output_dir=output_dir,
        deck_csv_path=Path(manifest["deckCsvPath"]) if manifest.get("deckCsvPath") else None,
        include_a4_pdf=manifest.get("includeA4Pdf", True),
        plugin_configs=manifest.get("pluginConfigs", {}),
    )

    # 2. Built-in Phase 0: Extract EPUB chapters
    ctx.emit_progress("core.epub_extract", "Extracting EPUB chapters...", 5)
    ctx.chapters = extract_epub_chapters(ctx.epub_path)

    # 3. Execute plugin-registered stages in ascending order
    stages = sorted(manifest.get("stages", []), key=lambda s: s.get("order", 0))
    total_stages = max(len(stages), 1)

    for idx, stage_meta in enumerate(stages):
        plugin_id = stage_meta["pluginId"]
        stage_id = stage_meta["stageId"]
        script_file = Path(stage_meta["scriptFilePath"])
        class_name = stage_meta["entryClassName"]
        plugin_data_dir = Path(stage_meta.get("pluginDataDir", script_file.parent))
        plugin_cfg = ctx.plugin_configs.get(plugin_id, {})

        pct = int(10 + ((idx / total_stages) * 75))
        ctx.emit_progress(stage_id, f"Running stage {stage_id} ({class_name})...", pct)

        stage_instance = load_stage_instance(script_file, class_name)
        stage_instance.execute(ctx, plugin_cfg, plugin_data_dir)

    # 4. Write canonical study_data.json
    ctx.emit_progress("core.serialize", "Writing study pack JSON artifacts...", 90)
    study_data_path = output_dir / "study_data.json"
    study_data_payload = {
        "bookId": ctx.book_id,
        "title": ctx.title,
        "language": ctx.language,
        "writingDirection": ctx.writing_direction,
        "chapterCount": len(ctx.chapters),
        "tokenCount": len(ctx.tokens),
        "wordCount": len(ctx.study_items),
        "items": [vars(item) for item in ctx.study_items.values()],
    }
    study_data_path.write_text(
        json.dumps(study_data_payload, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    ctx.artifacts["study_data"] = study_data_path

    # Flashcards if populated
    if ctx.flashcards:
        flashcards_path = output_dir / "flashcards.json"
        flashcards_path.write_text(
            json.dumps(ctx.flashcards, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        ctx.artifacts["flashcards"] = flashcards_path

    # 5. Compile modular ReportLab PDF from contributed ctx.pdf_sections
    if ctx.include_a4_pdf and ctx.pdf_sections:
        ctx.emit_progress("core.pdf_compile", "Compiling A4 Master Study PDF...", 95)
        compile_master_study_pdf(ctx)

    ctx.emit_progress("core.complete", "Study Pack build completed.", 100)
    return ctx

