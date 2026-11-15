"""Prompt renderer: shared vectors (also run by the C# suite) plus Python-specific checks."""
import json
import tempfile
import unittest
from pathlib import Path

from immersive_knowledge import templates
from immersive_knowledge.templates import TemplateError, parse_template, render

ROOT = Path(__file__).resolve().parents[2]
VECTORS = ROOT / "specs" / "fixtures" / "prompt-vectors.json"
PROMPTS = Path(__file__).resolve().parents[1] / "immersive_knowledge" / "prompts"


class SharedVectorTests(unittest.TestCase):
    def test_every_vector_behaves_as_specified(self):
        cases = json.loads(VECTORS.read_text(encoding="utf-8"))["cases"]
        self.assertGreaterEqual(len(cases), 30)
        ids = [c["id"] for c in cases]
        self.assertEqual(len(ids), len(set(ids)), "vector ids must be unique")
        for case in cases:
            with self.subTest(case["id"]):
                if "load_error" in case:
                    with self.assertRaises(TemplateError) as raised:
                        parse_template(case["template"])
                    self.assertIn(case["load_error"], str(raised.exception))
                    continue
                template = parse_template(case["template"])
                if "error" in case:
                    with self.assertRaises(TemplateError) as raised:
                        render(template, case.get("vars", {}))
                    self.assertIn(case["error"], str(raised.exception))
                else:
                    self.assertEqual(render(template, case.get("vars", {})), case["expect"])


class RendererTests(unittest.TestCase):
    def test_rendering_is_deterministic(self):
        t = parse_template("---\nname: t\nversion: 1\nvariables:\n  - a | untrusted\n  - w | type=list\n---\n{{a}}{{#each w}}{{this}}{{/each}}")
        values = {"a": "x", "w": ["1", "2"]}
        self.assertEqual(render(t, values), render(t, values))

    def test_unused_declared_variables_are_reported_not_fatal(self):
        t = parse_template("---\nname: t\nversion: 1\nvariables:\n  - a\n  - b\n---\n{{a}}")
        self.assertEqual(t.unused, ("b",))
        self.assertEqual(render(t, {"a": "1"}), "1")

    def test_metadata_is_read(self):
        t = parse_template("---\nname: coach.improve\nversion: 3\npurpose: tests\nresponse: json\nvariables:\n  - a\n---\n{{a}}")
        self.assertEqual((t.name, t.version, t.purpose, t.response), ("coach.improve", 3, "tests", "json"))

    def test_builtin_prompts_validate_and_use_every_declared_variable(self):
        loaded = templates.load_directory(PROMPTS)
        self.assertTrue(loaded, "the package must ship its prompts")
        for name, template in loaded.items():
            with self.subTest(name):
                self.assertEqual(template.unused, (), f"{name} declares variables it never uses")

    def test_a_broken_prompt_file_reports_its_path(self):
        with tempfile.TemporaryDirectory() as temp:
            (Path(temp) / "bad.prompt.md").write_text("no front matter", encoding="utf-8")
            with self.assertRaises(TemplateError) as raised:
                templates.load_directory(temp)
            self.assertIn("bad.prompt.md", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
