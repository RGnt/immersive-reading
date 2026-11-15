"""Module boundaries of the knowledge builder, read from the import statements (not from convention).

* ``store`` is the only module that touches SQLite; ``llm`` is the only one that touches the network.
* Data access (``store``), the spoiler gate (``gate``), validation of model output (``ops``), the model client (``llm``) and
  the orchestration (``builder``, ``cli``) depend in one direction only; a lower layer never imports a higher one.
* The project never imports the offline study pipeline (``immersive``), which in turn must never import this one.
"""
import ast
import pathlib
import unittest

PACKAGE = pathlib.Path(__file__).resolve().parents[1] / "immersive_knowledge"

# module -> the sibling modules it may import. Anything not listed here may import nothing from the package.
ALLOWED = {
    "position": set(),
    "evidence": set(),
    "templates": set(),
    "pronouns": set(),
    "llm": set(),
    "store": {"position"},
    "gate": {"position"},
    "scanner": {"evidence"},
    "ops": {"evidence", "position", "pronouns", "store"},
    "story": {"position", "scanner"},
    "profile": {"gate", "position", "scanner", "store", "story", "templates"},
    "builder": {"evidence", "gate", "llm", "ops", "position", "profile", "pronouns", "templates"},
    "cli": {"builder", "gate", "llm", "position", "profile", "scanner", "store", "story", "templates"},
}
NETWORK = {"urllib.request", "http", "http.client", "socket", "requests", "httpx", "aiohttp", "openai", "anthropic", "ssl"}
DATABASE = {"sqlite3"}


def imports_of(path):
    """(sibling modules imported with ``from .x import``, absolute module names imported)."""
    siblings, absolute = set(), set()
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.ImportFrom):
            if node.level >= 1 and node.module:
                siblings.add(node.module.split(".")[0])
            elif node.level == 0 and node.module:
                absolute.add(node.module)
        elif isinstance(node, ast.Import):
            absolute.update(alias.name for alias in node.names)
    return siblings, absolute


def modules():
    return {p.stem: p for p in PACKAGE.glob("*.py") if p.stem not in ("__init__", "__main__")}


class ArchitectureTests(unittest.TestCase):
    def test_every_module_is_covered_by_the_rules(self):
        self.assertEqual(set(modules()) - set(ALLOWED), set(), "a new module needs an entry in ALLOWED (decide its layer)")

    def test_modules_only_import_the_siblings_they_are_allowed_to(self):
        for name, path in modules().items():
            siblings, _ = imports_of(path)
            extra = siblings - ALLOWED[name]
            self.assertFalse(extra, f"{name} may not import {sorted(extra)} (allowed: {sorted(ALLOWED[name])})")

    def test_only_store_touches_the_database(self):
        for name, path in modules().items():
            _, absolute = imports_of(path)
            if name != "store":
                self.assertFalse(absolute & DATABASE, f"{name} imports sqlite3: data access belongs in store.py")

    def test_only_llm_touches_the_network(self):
        for name, path in modules().items():
            _, absolute = imports_of(path)
            if name != "llm":
                self.assertFalse(absolute & NETWORK, f"{name} imports a network module: only llm.py talks to the model server")

    def test_the_offline_study_pipeline_is_not_imported(self):
        for name, path in modules().items():
            _, absolute = imports_of(path)
            bad = {m for m in absolute if m == "immersive" or m.startswith("immersive.")}
            self.assertFalse(bad, f"{name} imports the study pipeline: {sorted(bad)}")


if __name__ == "__main__":
    unittest.main()
