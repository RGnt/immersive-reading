"""Document and reader command presets over the shared task executor."""
import argparse
import json
import sys
from pathlib import Path
from .orchestration import BuildOptions, INPUT_ERRORS, run_job
from .jobs import CancelledError
from .resources import add_resource_arguments, bundle_from_arguments


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Enrich books, build study documents or explicitly update decks.")
    parser.add_argument("command", choices=["build", "enrich", "decks", "run-manifest", "pos-chapter", "generate-transfer"])
    parser.add_argument("--manifest", type=Path, help="Path to pipeline-manifest.json for run-manifest")
    parser.add_argument("--transfer", type=Path, help="Path to learning_material.json or study_transfer.json for generate-transfer")
    parser.add_argument("--input", type=Path, help="Input JSON file for pos-chapter (default: stdin)")
    parser.add_argument("--books", type=Path, default=Path("books"))
    parser.add_argument("--book", type=Path)
    parser.add_argument("--output", type=Path, default=Path("output"))
    add_resource_arguments(parser)
    parser.add_argument("--skip-pdf", action="store_true")
    parser.add_argument("--skip-html", action="store_true")
    parser.add_argument("--skip-verify", action="store_true")
    parser.add_argument("--cards", action="store_true", help="Also export card JSON")
    parser.add_argument("--package", action="store_true", help="Also create study-pack.zip")
    parser.add_argument("--update-decks", action="store_true")
    args = parser.parse_args(argv)
    if args.command == "pos-chapter":
        from .pos_chapter import main as pos_main
        cli_args = [sys.argv[0]]
        if args.input:
            cli_args.append(str(args.input))
        return pos_main(cli_args)
    if args.command == "generate-transfer":
        if not args.transfer:
            parser.error("generate-transfer requires --transfer <path>")
        from .transfer import generate_from_transfer_file
        generate_from_transfer_file(args.transfer, args.output)
        return 0
    if args.command == "run-manifest":
        if not args.manifest:
            parser.error("run-manifest requires --manifest <path>")
        from .pipeline_runner import run_pipeline_from_manifest
        run_pipeline_from_manifest(args.manifest)
        return 0
    if args.command == "enrich" and args.update_decks:
        parser.error("enrich leaves inputs unchanged; use decks to update source CSVs")
    if args.command != "build" and (args.cards or args.package):
        parser.error("--cards and --package require build")
    documents = args.command == "build"
    options = BuildOptions(mode="study" if documents else "reader" if args.command == "enrich" else "decks",
                           html=documents and not args.skip_html, pdf=documents and not args.skip_pdf,
                           cards=args.cards, package=args.package, validate=not args.skip_verify,
                           update_decks=args.update_decks)
    try:
        from .cache import ContentCache
        cache = False if args.no_cache else ContentCache(args.cache_dir or args.output.resolve().parent / ".cache/immersive")
        result = run_job(args.books, args.output, bundle_from_arguments(args), book_folder=args.book, options=options, cache=cache)
    except (CancelledError, KeyboardInterrupt):
        parser.exit(130, "Job cancelled; no uncommitted output was published.\n")
    except INPUT_ERRORS as error:
        parser.exit(1, f"Pipeline error: {error}\n")
    for book in result.books:
        summary = {"book_id": book.book_id, "book": book.title,
                   "output": str(book.html_directory or book.data_directory) if book.data_directory else None,
                   **book.counts, "diagnostics": book.diagnostics, "export_status": book.export_status,
                   "cache_status": book.cache_status,
                   "reader_generation": book.reader_generation, "study_directory": str(book.study_directory) if book.study_directory else None}
        if book.export_error:
            summary["export_error"] = book.export_error
        if book.deck_updates:
            summary["deck_updates"] = book.deck_updates
        print(json.dumps(summary, ensure_ascii=True))
    if result.errors:
        print(json.dumps({"status": result.status, "errors": result.errors}, ensure_ascii=True), file=sys.stderr)
    if result.warnings:
        print(json.dumps({"warnings": result.warnings}, ensure_ascii=True), file=sys.stderr)
    return 1 if result.status == "partial" else 0


if __name__ == "__main__":
    raise SystemExit(main())
