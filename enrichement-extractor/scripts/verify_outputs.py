"""Compatibility command; implementation is in immersive.verification."""
if __name__ == "__main__":
    import sys
    from pathlib import Path
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from immersive.verification import verify, main


if __name__ == "__main__":
    main()
