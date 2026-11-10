from __future__ import annotations

import sqlite3
import tempfile
import unittest
from pathlib import Path
import sys

# Ensure plugin python path is importable
ja_path = Path(__file__).resolve().parents[2] / "immersive-reader" / "src" / "Plugins" / "ImmersiveReading.Plugins.Japanese" / "python"
if str(ja_path) not in sys.path:
    sys.path.insert(0, str(ja_path))

import ja_stages
from immersive.sdk.stage import ChapterDocument, PipelineContext


class JaStagesRefactoringTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.db_path = Path(self.temp_dir.name) / "dictionaries.db"

        # Create dummy dictionaries db schema
        conn = sqlite3.connect(str(self.db_path))
        cur = conn.cursor()
        cur.execute("""
            CREATE TABLE dictionary_sources (
                dictionary_id TEXT PRIMARY KEY,
                title TEXT,
                target_lang TEXT
            )
        """)
        cur.execute("""
            CREATE TABLE dictionary_terms (
                dictionary_id TEXT,
                expression TEXT,
                reading TEXT,
                glossary_text TEXT,
                score INTEGER
            )
        """)
        cur.execute("""
            CREATE TABLE dictionary_kanji (
                character TEXT PRIMARY KEY,
                on_readings_json TEXT,
                kun_readings_json TEXT,
                primary_meanings TEXT,
                stroke_count INTEGER,
                grade INTEGER,
                radical TEXT
            )
        """)
        cur.execute(
            "INSERT INTO dictionary_sources VALUES ('yomitan-jmdict', 'JMdict (English)', 'en')"
        )
        cur.execute(
            "INSERT INTO dictionary_terms VALUES ('yomitan-jmdict', '猫', 'ねこ', 'cat', 100)"
        )
        cur.execute(
            "INSERT INTO dictionary_kanji VALUES ('猫', '[\"ビョウ\"]', '[\"ねこ\"]', '[\"cat\"]', 11, 8, '犬')"
        )
        conn.commit()
        conn.close()

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    def _make_ctx(self) -> PipelineContext:
        return PipelineContext(
            book_id="test",
            title="Test",
            language="ja",
            epub_path=Path("dummy.epub"),
            output_dir=Path(self.temp_dir.name),
            deck_csv_path=None,
            include_a4_pdf=False,
            plugin_configs={},
            artifacts={"dictionaries_db": str(self.db_path)},
        )

    def test_managed_dictionary_db_context_manager(self) -> None:
        ctx = self._make_ctx()
        # Context manager should yield connection and safely close it
        with ja_stages.managed_dictionary_db(ctx) as conn:
            self.assertIsNotNone(conn)
            cur = conn.cursor()
            cur.execute("SELECT expression FROM dictionary_terms WHERE reading = 'ねこ'")
            self.assertEqual(cur.fetchone()[0], "猫")

    def test_resolve_word_memoization(self) -> None:
        ctx = self._make_ctx()
        with ja_stages.managed_dictionary_db(ctx) as conn:
            # First lookup
            r1, m1, s1 = ja_stages._resolve_word_reading_and_meaning(
                "猫", "猫", "ネコ", "名詞", "猫がいる。", {}, conn
            )
            self.assertEqual(r1, "ねこ")
            self.assertEqual(m1, "cat")

            # Second lookup of identical word should yield exact same result (using memoization)
            r2, m2, s2 = ja_stages._resolve_word_reading_and_meaning(
                "猫", "猫", "ネコ", "名詞", "別の文言脈。", {}, conn
            )
            self.assertEqual(r2, "ねこ")
            self.assertEqual(m2, "cat")

    def test_kanji_details_memoization(self) -> None:
        ctx = self._make_ctx()
        with ja_stages.managed_dictionary_db(ctx) as conn:
            info1 = ja_stages._resolve_kanji_details("猫", conn)
            self.assertEqual(info1["character"], "猫")
            self.assertEqual(info1["meanings"], ["cat"])
            self.assertEqual(info1["strokeCount"], 11)

            info2 = ja_stages._resolve_kanji_details("猫", conn)
            self.assertEqual(info1, info2)

    def test_pdf_section_renderer_template_method(self) -> None:
        # Base renderer template should exist and be subclassed by Vocabulary, Kanji, Grammar renderers
        self.assertTrue(issubclass(ja_stages.JapaneseVocabularySectionRenderer, ja_stages.JapanesePdfSectionRendererBase))
        self.assertTrue(issubclass(ja_stages.JapaneseKanjiSectionRenderer, ja_stages.JapanesePdfSectionRendererBase))
        self.assertTrue(issubclass(ja_stages.JapaneseGrammarSectionRenderer, ja_stages.JapanesePdfSectionRendererBase))


if __name__ == "__main__":
    unittest.main()
