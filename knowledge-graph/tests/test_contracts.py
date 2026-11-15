"""Contracts shared with the C# app (immersive-reader/src/Plugins/ImmersiveReading.Plugins.CharacterKnowledge)."""
import unittest
from pathlib import Path

from immersive_knowledge.store import SCHEMA

FIXTURE = Path(__file__).resolve().parents[2] / "specs" / "fixtures" / "knowledge-schema.sql"


class SchemaContractTests(unittest.TestCase):
    def test_the_published_schema_is_exactly_what_the_builder_writes(self):
        # The C# reader tests create their database from this file. If you change store.SCHEMA, regenerate it:
        #   python -c "from immersive_knowledge.store import SCHEMA; open('../specs/fixtures/knowledge-schema.sql','w',encoding='utf-8',newline='\n').write(SCHEMA)"
        self.assertEqual(FIXTURE.read_text(encoding="utf-8"), SCHEMA)


if __name__ == "__main__":
    unittest.main()
