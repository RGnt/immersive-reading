"""Compatibility command for reusable flashcard JSON export."""
# Keep direct checkout execution working without an editable installation.
# Library callers and the pipeline import immersive.card_export directly.
if __name__ == "__main__":
    import sys
    from pathlib import Path
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from immersive.card_export import export, main


if __name__ == "__main__":
    main()
