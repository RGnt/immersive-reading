from __future__ import annotations

import unittest
from pathlib import Path

from immersive.sdk.stage import ChapterDocument, ExtractedToken, PipelineContext
from immersive.sdk.pdf import compile_master_study_pdf
from immersive.transfer import build_transfer_payload


class KanjiMasterScopeTests(unittest.TestCase):
    def test_kanji_master_deduplication_and_book_source_grounding(self):
        # Chapter 0 has "海", "本"
        # Chapter 1 repeats "海", and introduces "山"
        ch0 = ChapterDocument(index=0, title="Ch 1", spine_href="c1.xhtml", text="海と本。")
        ch1 = ChapterDocument(index=1, title="Ch 2", spine_href="c2.xhtml", text="海と山。")

        ctx = PipelineContext(
            book_id="kanji-test-book",
            title="Kanji Test Book",
            language="ja",
            epub_path=Path("dummy.epub"),
            output_dir=Path("dummy_out"),
            deck_csv_path=None,
            include_a4_pdf=False,
            plugin_configs={},
        )
        ctx.chapters = [ch0, ch1]

        # Simulate chapter kanji map where Chapter 1 repeats '海'
        ctx.artifacts["chapter_kanji"] = {
            0: [
                {"character": "海", "occurrences": 1, "words": ["海"], "codepointHex": "6D77", "meanings": ["sea"], "firstAppearance": True},
                {"character": "本", "occurrences": 1, "words": ["本"], "codepointHex": "672C", "meanings": ["book"], "firstAppearance": True},
            ],
            1: [
                {"character": "海", "occurrences": 1, "words": ["海"], "codepointHex": "6D77", "meanings": ["sea"], "firstAppearance": False},
                {"character": "山", "occurrences": 1, "words": ["山"], "codepointHex": "5C71", "meanings": ["mountain"], "firstAppearance": True},
            ]
        }

        # Build transfer payload
        chapters_payload = [
            {"index": 0, "title": "Ch 1", "kanji": ctx.artifacts["chapter_kanji"][0]},
            {"index": 1, "title": "Ch 2", "kanji": ctx.artifacts["chapter_kanji"][1]},
        ]
        payload = build_transfer_payload(
            book_id=ctx.book_id,
            title=ctx.title,
            language="ja",
            writing_direction="ltr",
            chapters=chapters_payload,
        )

        # Global kanji must only have unique kanji from the book (3 unique kanji: 海, 本, 山)
        unique_characters = [k["character"] for k in payload["master"]["kanji"]]
        self.assertEqual(len(unique_characters), 3)
        self.assertEqual(set(unique_characters), {"海", "本", "山"})

        # Chapter 1 kanji must have firstAppearance False for '海', and True for '山'
        ch1_kanji = payload["chapters"][1]["kanji"]
        umi = next(k for k in ch1_kanji if k["character"] == "海")
        yama = next(k for k in ch1_kanji if k["character"] == "山")
        self.assertFalse(umi["firstAppearance"])
        self.assertTrue(yama["firstAppearance"])


if __name__ == "__main__":
    unittest.main()
