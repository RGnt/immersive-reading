"""Reusable flashcard JSON export, independent of HTML and PDF renderers."""
import argparse
import json
from pathlib import Path
from .study_data import prepare_study_data
from .validation import load_json
from .jobs import check_cancelled

def export_book(source, *, study=None, cancel=None):
    study = study if study is not None else prepare_study_data(source, include_context=False, cancel=cancel)
    if study.book_id != source["id"]:
        raise ValueError("Study data belongs to another book")
    numbers = {c["id"]: g["number"] for g in study.learning_sets for c in g["cards"]}
    chapters = []
    for ch in source["chapters"]:
        check_cancelled(cancel)
        cards = []
        for collection in (study.vocabulary, study.kanji, study.grammar):
            for c in collection[ch["id"]]:
                notes = c["details"]
                if c["kind"] == "grammar":
                    notes = "Grammar candidate: review in context.\n" + notes
                cards.append({"id": c["id"], "kind": c["kind"], "prompt": c["title"],
                              "meaning": c["meaning"] or "No English gloss in the supplied dictionary.",
                              "reading": c["reading"], "example": c["plain_example"], "notes": notes,
                              "example_ruby": c.get("ruby", []),
                              "dictionary_entries": c.get("dictionary_entries", []),
                              "dictionary_metadata": c.get("dictionary_metadata", []),
                              "definition_candidates": c.get("definition_candidates", []),
                              "sense_selection": c.get("sense_selection"),
                              "set_number": numbers.get(c["id"], 0) if c["kind"] == "vocabulary" else 0})
        chapters.append({"id": ch["id"], "number": ch["number"], "title": ch["title"], "cards": cards})
    return {"id": source["id"], "title": source["title"], "chapters": chapters,
                  "dictionary_sources": source.get("dictionary_sources", []),
                  "attribution": ["Japanese examples and book text: user-supplied EPUB.",
                                  "Vocabulary glosses: user-supplied Jiten / JMdict deck or Yomitan dictionaries; see dictionary_sources for dictionary notices.",
                                  "Dictionary fields: James William Breen and EDRDG, CC BY-SA 4.0. https://www.edrdg.org/edrdg/licence.html",
                                  "Primary kanji meanings: Kanji alive, Harumi Hibino Lory & Arno Bosse, CC BY 4.0. https://kanjialive.com/credits/ KANJIDIC2 fallback where uncovered.",
                                  "Grammar: rule-based candidates; not an exhaustive or reviewed grammar analysis."]}


def export(output_root, destination, *, prepared_by_book=None, announce=True, reader_paths=None, cancel=None):
    if reader_paths is None:
        from .publication import read_manifest, reader_paths as published_readers, Publication, PublicationError
        manifest = read_manifest(output_root)
        if manifest is not None and Path(destination).resolve() == (Path(output_root)/"data/flashcards/library.json").resolve():
            with Publication(output_root, base=manifest, cancel=cancel) as publication:
                published, _ = publication.publish(cards=True, prepared_by_book=prepared_by_book)
            errors = [e for e in published.get("export_errors", []) if e["stage"] == "cards"]
            if errors:
                raise PublicationError("Card export failed: " + errors[0]["message"])
            if announce:
                print(f"Published card library to {destination}")
            return
        if manifest is not None:
            from .retention import lease_manifest
            with lease_manifest(output_root, manifest) as leased:
                return export(output_root, destination, prepared_by_book=prepared_by_book, announce=announce,
                              reader_paths=published_readers(output_root, leased), cancel=cancel)
        reader_paths = published_readers(output_root, manifest)
    books = []
    for path in reader_paths:
        check_cancelled(cancel)
        source = load_json(path)
        study = (prepared_by_book or {}).get(source["id"])
        books.append(export_book(source, study=study, cancel=cancel))
    if not books:
        raise ValueError("No built reader.json files found; build the study materials first.")
    destination = Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps({"schema_version": "1.0", "books": books}, ensure_ascii=False, indent=2), encoding="utf-8")
    if announce:
        print(f"Exported {len(books)} book(s), {sum(len(c['cards']) for b in books for c in b['chapters'])} cards to {destination}")


def main(argv=None):
    parser = argparse.ArgumentParser(description="Export plain book/chapter/card JSON without rendering")
    parser.add_argument("--input", type=Path, default=Path("output"))
    parser.add_argument("--output", type=Path, default=Path("output/data/flashcards/library.json"))
    args = parser.parse_args(argv)
    try:
        export(args.input, args.output)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Card export error: {error}\n")


if __name__ == "__main__":
    main()
