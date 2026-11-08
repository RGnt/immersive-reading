from __future__ import annotations

import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from immersive.pipeline_runner import (
    extract_epub_chapters,
    load_stage_instance,
    run_pipeline_from_manifest,
)
from immersive.sdk.pdf import shape_bidirectional_text
from immersive.sdk.stage import (
    ChapterDocument,
    ExtractedToken,
    PipelineContext,
    PipelineStage,
    StudyItem,
)


def create_minimal_epub(path: Path, title: str = "Test Book", chapter_text: str = "これはテストです。") -> None:
    with zipfile.ZipFile(path, "w") as z:
        z.writestr("mimetype", "application/epub+zip")
        z.writestr(
            "META-INF/container.xml",
            """<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>""",
        )
        z.writestr(
            "OEBPS/content.opf",
            f"""<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="pub-id">urn:uuid:12345</dc:identifier>
    <dc:title>{title}</dc:title>
    <dc:language>ja</dc:language>
  </metadata>
  <manifest>
    <item id="chapter1" href="chap1.xhtml" media-type="application/xhtml+xml"/>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
  </manifest>
  <spine>
    <itemref idref="chapter1"/>
  </spine>
</package>""",
        )
        z.writestr(
            "OEBPS/nav.xhtml",
            """<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
  <body>
    <nav epub:type="toc">
      <ol>
        <li><a href="chap1.xhtml">Chapter 1</a></li>
      </ol>
    </nav>
  </body>
</html>""",
        )
        z.writestr(
            "OEBPS/chap1.xhtml",
            f"""<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
  <head><title>Chapter 1</title></head>
  <body>
    <p>{chapter_text}</p>
  </body>
</html>""",
        )


class PluggablePipelineTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir_obj = tempfile.TemporaryDirectory()
        self.temp_dir = Path(self.temp_dir_obj.name)

    def tearDown(self) -> None:
        self.temp_dir_obj.cleanup()

    def test_pipeline_context_and_stage_models(self) -> None:
        ctx = PipelineContext(
            book_id="book-test1",
            title="Test Title",
            language="ja",
            epub_path=self.temp_dir / "test.epub",
            output_dir=self.temp_dir / "output",
            deck_csv_path=None,
            include_a4_pdf=False,
            plugin_configs={"japanese": {"vertical": False}},
        )

        token = ExtractedToken(
            surface="本",
            lemma="本",
            reading="ほん",
            pos="Noun",
            chapter_index=0,
            sentence_text="これは本です。",
        )
        ctx.tokens.append(token)

        item = StudyItem(
            key="本",
            headword="本",
            reading="ほん",
            pos="Noun",
            book_occurrences=1,
            sample_sentences=["これは本です。"],
        )
        ctx.study_items["本"] = item

        self.assertEqual(len(ctx.tokens), 1)
        self.assertEqual(len(ctx.study_items), 1)
        self.assertEqual(ctx.study_items["本"].reading, "ほん")

    def test_shape_bidirectional_text(self) -> None:
        ltr_text = "Hello World"
        self.assertEqual(shape_bidirectional_text(ltr_text, "ltr"), ltr_text)
        # Empty text
        self.assertEqual(shape_bidirectional_text("", "rtl"), "")
        # Non-RTL with text
        self.assertEqual(shape_bidirectional_text(ltr_text, "ttb"), ltr_text)

    def test_dynamic_stage_loading(self) -> None:
        script_path = self.temp_dir / "sample_stage.py"
        script_path.write_text(
            """
from immersive.sdk.stage import PipelineStage, PipelineContext
from pathlib import Path
from typing import Any

class CustomMockStage(PipelineStage):
    def execute(self, ctx: PipelineContext, plugin_config: dict[str, Any], plugin_data_dir: Path) -> None:
        ctx.shared_state["mock_executed"] = True
""",
            encoding="utf-8",
        )

        stage = load_stage_instance(script_path, "CustomMockStage")
        self.assertIsInstance(stage, PipelineStage)

        ctx = PipelineContext(
            book_id="test",
            title="test",
            language="en",
            epub_path=self.temp_dir / "test.epub",
            output_dir=self.temp_dir,
            deck_csv_path=None,
            include_a4_pdf=False,
            plugin_configs={},
        )
        stage.execute(ctx, {}, self.temp_dir)
        self.assertTrue(ctx.shared_state.get("mock_executed"))

    def test_run_pipeline_from_manifest_end_to_end(self) -> None:
        epub_path = self.temp_dir / "test.epub"
        create_minimal_epub(epub_path, title="Sample Book", chapter_text="猫が好きです。")

        chapters = extract_epub_chapters(epub_path)
        self.assertGreaterEqual(len(chapters), 1)
        self.assertIn("猫が好きです", chapters[0].text)

        script_path = self.temp_dir / "test_stage.py"
        script_path.write_text(
            """
from immersive.sdk.stage import PipelineStage, PipelineContext, StudyItem
from pathlib import Path
from typing import Any

class WordCounterStage(PipelineStage):
    def execute(self, ctx: PipelineContext, plugin_config: dict[str, Any], plugin_data_dir: Path) -> None:
        for ch in ctx.chapters:
            if "猫" in ch.text:
                ctx.study_items["猫"] = StudyItem(
                    key="猫",
                    headword="猫",
                    reading="ねこ",
                    pos="Noun",
                    book_occurrences=1,
                    sample_sentences=[ch.text]
                )
""",
            encoding="utf-8",
        )

        output_dir = self.temp_dir / "study_output"
        manifest_path = self.temp_dir / "manifest.json"
        manifest_content = {
            "bookId": "book-cat123",
            "title": "Sample Book",
            "language": "ja",
            "epubFilePath": str(epub_path),
            "outputDirectory": str(output_dir),
            "includeA4Pdf": False,
            "pluginConfigs": {"cat_plugin": {"enabled": True}},
            "stages": [
                {
                    "pluginId": "cat_plugin",
                    "stageId": "cat.counter",
                    "order": 110,
                    "scriptFilePath": str(script_path),
                    "entryClassName": "WordCounterStage",
                    "pluginDataDir": str(self.temp_dir),
                }
            ],
        }
        manifest_path.write_text(json.dumps(manifest_content), encoding="utf-8")

        ctx = run_pipeline_from_manifest(manifest_path)
        self.assertEqual(len(ctx.study_items), 1)
        self.assertIn("猫", ctx.study_items)

        study_data_json = output_dir / "study_data.json"
        self.assertTrue(study_data_json.exists())
        study_data = json.loads(study_data_json.read_text(encoding="utf-8"))
        self.assertEqual(study_data["bookId"], "book-cat123")
        self.assertEqual(study_data["wordCount"], 1)
        self.assertEqual(study_data["items"][0]["key"], "猫")


if __name__ == "__main__":
    unittest.main()
