"""Full-study command and compatibility facade over shared orchestration."""
import argparse
import sys
from pathlib import Path
from .orchestration import BuildOptions, ExportFailure, INPUT_ERRORS, run_job
from .jobs import CancelledError
from .resources import add_resource_arguments, bundle_from_arguments


def _console_event(event):
    if event["stage"] == "cache":
        print(f'Reader cache: {event["status"]}', flush=True)
    elif event["stage"] == "publication_warning":
        print(f'Publication warning: {event["code"]} {event.get("path", event.get("message", ""))}', file=sys.stderr, flush=True)
    elif event["stage"] == "deck_update":
        for report in event["reports"]:
            print(f'Deck {report["file"]}: first_appearance populated for {report["matched"]}/{report["rows"]} rows.', flush=True)
    elif event["stage"] == "book_complete":
        print(f'Built {event["title"]}: {event["path"]}', flush=True)
    elif event["stage"] == "complete" and event["archive"]:
        print(f'Complete: {event["archive"]}', flush=True)


def run_stages(books, output, resources, font=None, verify=True, decks_only=False,
               *, book_folder=None, reader_only=False, update_decks=False,
               skip_pdf=False, skip_html=False, skip_cards=False, skip_package=False, yomitan_paths=None, cache=None):
    """Compatibility return values: reader directories, deck None, or ZIP path."""
    if reader_only and (decks_only or update_decks):
        raise ValueError("reader-only cannot be combined with deck updates")
    mode = "reader" if reader_only else "decks" if decks_only else "study"
    study = mode == "study"
    options = BuildOptions(mode=mode, html=study and not skip_html, pdf=study and not skip_pdf,
                           cards=study and not skip_cards, package=study and not skip_package,
                           validate=verify, update_decks=update_decks)
    result = run_job(books, output, resources, book_folder=book_folder, options=options,
                     font=font, on_event=_console_event, yomitan_paths=yomitan_paths, cache=cache)
    if result.status == "partial":
        raise ExportFailure(result)
    if reader_only:
        return [b.data_directory for b in result.books]
    return result.archive


def main(argv=None):
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description="Offline Japanese reader enrichment and supplementary study exports.")
    parser.add_argument("--books", type=Path, default=Path("books"))
    parser.add_argument("--book", type=Path, help="Process only this EPUB/deck folder")
    parser.add_argument("--output", type=Path, default=Path("output"))
    add_resource_arguments(parser)
    parser.add_argument("--skip-verify", action="store_true")
    parser.add_argument("--skip-pdf", action="store_true")
    parser.add_argument("--skip-html", action="store_true")
    parser.add_argument("--skip-cards", action="store_true")
    parser.add_argument("--skip-package", action="store_true")
    parser.add_argument("--doctor", action="store_true", help="Check the runtime and resources without a book")
    parser.add_argument("--decks-only", action="store_true", help="Update source CSV first_appearance without rendering or packaging")
    parser.add_argument("--reader-only", action="store_true", help="Validate/write reader JSON; leave source CSVs unchanged")
    parser.add_argument("--update-decks", action="store_true", help="Also update first_appearance during a study build")
    args = parser.parse_args(argv)
    if args.reader_only and (args.decks_only or args.update_decks):
        parser.error("--reader-only cannot be combined with --decks-only or --update-decks")
    try:
        bundle = bundle_from_arguments(args)
        if args.doctor:
            from .doctor import check
            resources = args.resources.resolve()
            capabilities = {"reader"}
            if not (args.reader_only or args.decks_only):
                capabilities.update(name for name, skipped in (("html", args.skip_html), ("pdf", args.skip_pdf), ("cards", args.skip_cards)) if not skipped)
            check(resources.parent, resources=bundle, capabilities=capabilities)
            return 0
        from .cache import ContentCache
        cache = False if args.no_cache else ContentCache(args.cache_dir or args.output.resolve().parent / ".cache/immersive")
        run_stages(args.books, args.output, bundle, None, not args.skip_verify, args.decks_only,
                   book_folder=args.book, reader_only=args.reader_only, update_decks=args.update_decks,
                   skip_pdf=args.skip_pdf, skip_html=args.skip_html, skip_cards=args.skip_cards, skip_package=args.skip_package,
                   yomitan_paths=None, cache=cache)
        return 0
    except (CancelledError, KeyboardInterrupt):
        parser.exit(130, "Job cancelled; no uncommitted output was published.\n")
    except INPUT_ERRORS as error:
        parser.exit(1, f"Pipeline error: {error}\n")


if __name__ == "__main__":
    raise SystemExit(main())
