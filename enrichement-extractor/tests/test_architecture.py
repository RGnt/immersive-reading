"""Invariant 1 of AGENTS.md as a test: the study pipeline is offline and independent of the LLM project.

Reads the import statements of every module of the ``immersive`` package. ``urllib.parse`` is string handling, not networking.
"""
import ast
import pathlib
import unittest

PACKAGE = pathlib.Path(__file__).resolve().parents[1] / "immersive"
NETWORK = {"urllib.request", "urllib.error", "http", "http.client", "http.server", "socket", "ssl", "ftplib", "smtplib", "xmlrpc",
           "requests", "httpx", "aiohttp", "openai", "anthropic", "websockets"}


def absolute_imports(path):
    found = set()
    for node in ast.walk(ast.parse(path.read_text(encoding="utf-8"))):
        if isinstance(node, ast.ImportFrom) and node.level == 0 and node.module:
            found.add(node.module)
            found.update(f"{node.module}.{alias.name}" for alias in node.names)
        elif isinstance(node, ast.Import):
            found.update(alias.name for alias in node.names)
    return found


class OfflineBoundaryTests(unittest.TestCase):
    def test_no_module_imports_a_network_library(self):
        for path in sorted(PACKAGE.rglob("*.py")):
            bad = {m for m in absolute_imports(path) if any(m == n or m.startswith(n + ".") for n in NETWORK)}
            self.assertFalse(bad, f"{path.relative_to(PACKAGE)} imports {sorted(bad)}: the study pipeline must run offline")

    def test_no_module_imports_the_knowledge_project(self):
        for path in sorted(PACKAGE.rglob("*.py")):
            bad = {m for m in absolute_imports(path) if m == "immersive_knowledge" or m.startswith("immersive_knowledge.")}
            self.assertFalse(bad, f"{path.relative_to(PACKAGE)} imports the LLM project: {sorted(bad)}")


if __name__ == "__main__":
    unittest.main()
