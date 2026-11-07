"""Actionable checks at optional format boundaries, without importing renderers."""
import importlib.util


def require_print():
    missing = [name for name in ("reportlab", "pymupdf", "fontTools", "PIL") if importlib.util.find_spec(name) is None]
    if missing:
        raise ValueError("PDF support requires immersive-reading[print]; missing: " + ", ".join(missing))
