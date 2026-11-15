"""Command line (spec 18, 6.3): ``python -m immersive_knowledge <command>``.

Progress is one JSON object per line on stdout (``{"event": ...}``); human-readable problems go to stderr. The API key is read
from an environment variable only, never from arguments or the config file (it would show up in process lists and logs).

Exit codes: 0 ok · 1 verify found problems · 2 invalid input · 3 LLM failure (fatal: bad key, unreachable, ...) · 4 partial
(a chapter failed; the build is resumable).
"""
import argparse
import json
import os
import sys
from pathlib import Path

from .builder import Builder, BuildConfig
from .gate import Gate
from .llm import LlmError, OpenAiClient
from .position import StoryPosition
from .profile import PROMPTS_DIR, generate_snapshots, get_profile, list_characters
from .scanner import ViewScanner, scan
from .store import EVENT_KINDS, EXCLUSIVE_SLOTS, HOLDING_STATES, SLOTS, Store
from .story import DEFAULT_EVENT_LIMIT, card_text, list_events, list_items
from .templates import TemplateError, load_directory

EXIT_OK, EXIT_VERIFY, EXIT_INPUT, EXIT_LLM, EXIT_PARTIAL = 0, 1, 2, 3, 4
DEFAULT_KEY_ENV = "KNOWLEDGE_API_KEY"

CONFIG_KEYS = {
    "endpoint", "model", "temperature", "max_tokens", "timeout_seconds", "thinking", "output_language", "max_input_chars",
    "max_ops", "min_quote_chars", "min_prominence", "snapshot_entities", "snapshot_relations", "snapshot_events", "context_tokens",
    "chars_per_token", "api_key_env", "story_pass", "story_snapshot_characters", "story_snapshot_others", "story_snapshot_items",
    "story_snapshot_events", "prompts_dir"}


def emit(**event):
    print(json.dumps(event, ensure_ascii=False), flush=True)


class InputError(Exception):
    pass


def load_json(path, what):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise InputError(f"cannot read {what} {path}: {error}") from None


def load_config(path):
    config = load_json(path, "config")
    if not isinstance(config, dict):
        raise InputError(f"{path}: the configuration must be a JSON object")
    unknown = sorted(set(config) - CONFIG_KEYS)
    if unknown:
        raise InputError(f"{path}: unknown configuration key '{unknown[0]}' (a secret such as 'api_key' belongs in the environment)")
    if not config.get("endpoint"):
        raise InputError(f"{path}: 'endpoint' is required (for example http://127.0.0.1:1919/v1)")
    return config


def load_templates(config):
    """The built-in prompts, replaced by the files of ``prompts_dir`` (the user's edited copies, already validated by the app).

    A file that does not load is an input error, never silently skipped: a build must not run with a prompt the user did not intend.
    """
    try:
        templates = load_directory(PROMPTS_DIR)
        folder = config.get("prompts_dir")
        if folder:
            if not Path(folder).is_dir():
                raise InputError(f"prompts_dir {folder} is not a folder")
            for name, template in load_directory(folder).items():
                if name not in templates:
                    raise InputError(f"prompts_dir: '{name}' is not a prompt of this builder")
                templates[name] = template
        return templates
    except TemplateError as error:
        raise InputError(f"prompt template: {error}") from None


def validate_book_text(book):
    if not isinstance(book, dict) or not isinstance(book.get("volumes"), list) or not book["volumes"]:
        raise InputError("book-text.json: 'volumes' must be a non-empty list")
    for volume in book["volumes"]:
        if not isinstance(volume.get("volume"), int) or not isinstance(volume.get("chapters"), list):
            raise InputError("book-text.json: every volume needs an integer 'volume' and a 'chapters' list")
        for chapter in volume["chapters"]:
            if not isinstance(chapter.get("index"), int) or not isinstance(chapter.get("text"), str):
                raise InputError("book-text.json: every chapter needs an integer 'index' and a string 'text'")
    return book


def build_command(args):
    config = load_config(args.config)
    book = validate_book_text(load_json(args.book_text, "book text"))
    key_env = config.get("api_key_env", DEFAULT_KEY_ENV)
    client = OpenAiClient(
        config["endpoint"], config.get("model", ""), os.environ.get(key_env) or None,
        temperature=config.get("temperature", 0.2), max_tokens=config.get("max_tokens", 16000),
        timeout=config.get("timeout_seconds", 900), thinking=config.get("thinking", "off"))
    model = client.resolve_model()
    build_config = BuildConfig(
        model=model, output_language=config.get("output_language", "Japanese"),
        **{k: config[k] for k in ("max_input_chars", "max_ops", "min_quote_chars", "snapshot_entities", "snapshot_relations", "snapshot_events",
                                  "context_tokens", "chars_per_token", "story_pass", "story_snapshot_characters", "story_snapshot_others",
                                  "story_snapshot_items", "story_snapshot_events") if k in config})
    templates = load_templates(config)
    store = Store(args.db)
    emit(event="build_start", model=model, db=str(args.db))
    report = Builder(store, client, build_config, templates=templates, progress=lambda e: emit(**e)).run(
        book, resume=not args.no_resume, from_position=StoryPosition.parse(args.from_position) if args.from_position else None)
    if not args.no_snapshots:
        result = generate_snapshots(store, min_prominence=config.get("min_prominence", 1), template=templates["profile.block"])
        emit(event="snapshots_done", stored=result["stored"], blocked=len(result["blocked"]), events=result["events"], items=result["items"],
             story_blocked=len(result["story_blocked"]))
        for blocked in result["blocked"] + result["story_blocked"]:
            print(f"snapshot blocked (spoiler scanner): {blocked}", file=sys.stderr)
    if report.failed:
        print(f"chapter {report.failed['volume']}:{report.failed['chapter']} failed: {report.failed['reason']}", file=sys.stderr)
        return EXIT_PARTIAL
    return EXIT_OK


def snapshots_command(args):
    result = generate_snapshots(Store(args.db), min_prominence=args.min_prominence)
    emit(event="snapshots_done", stored=result["stored"], blocked=len(result["blocked"]), events=result["events"], items=result["items"],
         story_blocked=len(result["story_blocked"]))
    return EXIT_OK


def events_command(args):
    position = StoryPosition.parse(args.position)
    result = list_events(Store(args.db), position.volume, position.chapter, character_id=args.character, kind=args.kind, limit=args.limit)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return EXIT_OK


def items_command(args):
    position = StoryPosition.parse(args.position)
    print(json.dumps(list_items(Store(args.db), position.volume, position.chapter, holder_id=args.holder), ensure_ascii=False, indent=2))
    return EXIT_OK


def characters_command(args):
    position = StoryPosition.parse(args.position)
    print(json.dumps(list_characters(Store(args.db), position.volume, position.chapter), ensure_ascii=False, indent=2))
    return EXIT_OK


def profile_command(args):
    position = StoryPosition.parse(args.position)
    found = get_profile(Store(args.db), position.volume, position.chapter, args.character)
    if found is None:
        print(f"no profile for {args.character} at {position}", file=sys.stderr)
        return EXIT_INPUT
    print(json.dumps(found, ensure_ascii=False, indent=2))
    return EXIT_OK


def verify(store):
    """Integrity of the stored knowledge plus a fresh spoiler scan of every snapshot. Returns a list of problems."""
    problems = []
    evidence = {r["id"]: r["end_key"] for r in store.query("SELECT id, end_key FROM evidence")}
    tables = (("names", "from_key", "id"), ("attributes", "from_key", "id"), ("relations", "from_key", "id"),
              ("events", "from_key", "id"), ("identity_links", "from_key", "id"), ("holdings", "from_key", "id"))
    for table, column, ident in tables:
        for row in store.query(f"SELECT {ident} AS ident, {column} AS pos, evidence_ids FROM {table}"):
            ids = json.loads(row["evidence_ids"])
            if not ids or any(i not in evidence for i in ids):
                problems.append(f"{table} {row['ident']}: missing or unknown evidence")
            elif row["pos"] < max(evidence[i] for i in ids):
                # earlier than its evidence would show the fact before the reader has seen what supports it. *Later* is allowed:
                # a fact with a quote that could not be verified is deliberately positioned at the end of the text the model was shown
                problems.append(f"{table} {row['ident']}: position is earlier than the end of its latest evidence (I-K1)")
    for row in store.query("SELECT id FROM entities WHERE id NOT GLOB 'e[0-9]*'"):
        problems.append(f"entity {row['id']}: id is not opaque")
    for row in store.query("SELECT id FROM relations WHERE to_key IS NOT NULL AND to_key < from_key"):
        problems.append(f"relation {row['id']}: ends before it starts")
    problems += verify_story(store)
    gate = Gate(store)
    for row in store.query("SELECT character_id, volume, chapter, block_text, profile_json FROM profile_snapshots"):
        hits = scan(store, gate.view(StoryPosition(row["volume"], row["chapter"])), row["block_text"] + "\n" + row["profile_json"])
        if hits:
            problems.append(f"snapshot {row['character_id']} at {row['volume']}:{row['chapter']}: names not yet visible: {', '.join(hits)}")
    for row in store.chapters():
        if row["status"] != "built":
            problems.append(f"chapter {row['volume']}:{row['chapter']} is {row['status']}")
    for row in store.story_chapters():
        if row["status"] != "built":
            problems.append(f"story chapter {row['volume']}:{row['chapter']} is {row['status']}")
    return problems


def verify_story(store):
    """Integrity of events, item holdings and the stored story cards (spec 22)."""
    problems = []
    ids = {r["id"] for r in store.query("SELECT id FROM events")}
    for row in store.query("SELECT id, kind, title, parent, origin FROM events"):
        if row["origin"] == "s" and (row["kind"] not in EVENT_KINDS or not row["title"]):
            problems.append(f"event {row['id']}: a story event needs a known kind and a title")
        if row["parent"] and row["parent"] not in ids:
            problems.append(f"event {row['id']}: unknown parent {row['parent']}")
    for row in store.query("SELECT id, replaced_by, end_reason FROM holdings WHERE replaced_by != '' AND end_reason != 'replaced'"):
        problems.append(f"holding {row['id']}: replaced_by is set but the holding did not end as replaced")
    for row in store.query("SELECT id, state, slot, from_key, to_key FROM holdings"):
        if row["state"] not in HOLDING_STATES or (row["state"] == "equipped") != bool(row["slot"]) or (row["slot"] and row["slot"] not in SLOTS):
            problems.append(f"holding {row['id']}: invalid state or slot")
        if row["to_key"] is not None and row["to_key"] <= row["from_key"]:
            problems.append(f"holding {row['id']}: ends before it starts")
    for row in store.query("SELECT item_id FROM holdings WHERE to_key IS NULL GROUP BY item_id HAVING COUNT(*) > 1"):
        problems.append(f"item {row['item_id']}: more than one open holding")
    for row in store.query("SELECT holder_id, slot FROM holdings WHERE to_key IS NULL AND state='equipped' AND slot IN "
                           f"({','.join('?' * len(EXCLUSIVE_SLOTS))}) GROUP BY holder_id, slot HAVING COUNT(*) > 1", EXCLUSIVE_SLOTS):
        problems.append(f"{row['holder_id']}: more than one item equipped in slot {row['slot']}")
    gate, scanners = Gate(store), {}
    for table, column in (("story_event_cards", "event_id"), ("story_item_cards", "item_id")):
        for row in store.query(f"SELECT {column} AS ident, volume, chapter, card_json FROM {table}"):
            key = (row["volume"], row["chapter"])
            if key not in scanners:
                scanners[key] = ViewScanner(store, gate.view(StoryPosition(*key)))
            hits = scanners[key].scan(card_text(json.loads(row["card_json"])))
            if hits:
                problems.append(f"story card {row['ident']} at {key[0]}:{key[1]}: names not yet visible: {', '.join(hits)}")
    return problems


def verify_command(args):
    store = Store(args.db)
    problems = verify(store)
    counts = {t: store.query(f"SELECT COUNT(*) AS n FROM {t}")[0]["n"]
              for t in ("entities", "names", "attributes", "relations", "events", "identity_links", "evidence", "profile_snapshots",
                        "holdings", "story_event_cards", "story_item_cards")}
    print(json.dumps({"ok": not problems, "problems": problems, "counts": counts}, ensure_ascii=False, indent=2))
    return EXIT_OK if not problems else EXIT_VERIFY


def parser():
    top = argparse.ArgumentParser(prog="immersive_knowledge", description="Spoiler-gated character knowledge graph builder.")
    sub = top.add_subparsers(dest="command", required=True)

    build = sub.add_parser("build", help="build or continue the knowledge base (needs the LLM endpoint)")
    build.add_argument("--db", required=True)
    build.add_argument("--book-text", required=True)
    build.add_argument("--config", required=True)
    build.add_argument("--no-resume", action="store_true", help="rebuild every chapter")
    build.add_argument("--from", dest="from_position", help="rebuild from VOLUME:CHAPTER onwards")
    build.add_argument("--no-snapshots", action="store_true")
    build.set_defaults(run=build_command)

    snaps = sub.add_parser("snapshots", help="regenerate the gated profile snapshots")
    snaps.add_argument("--db", required=True)
    snaps.add_argument("--min-prominence", type=int, default=1)
    snaps.set_defaults(run=snapshots_command)

    events = sub.add_parser("events", help="events known as of VOLUME:CHAPTER, newest first")
    events.add_argument("--db", required=True)
    events.add_argument("--position", required=True)
    events.add_argument("--character", help="only events this character took part in")
    events.add_argument("--kind", choices=EVENT_KINDS)
    events.add_argument("--limit", type=int, default=DEFAULT_EVENT_LIMIT)
    events.set_defaults(run=events_command)

    items = sub.add_parser("items", help="items known as of VOLUME:CHAPTER, with their holders and history")
    items.add_argument("--db", required=True)
    items.add_argument("--position", required=True)
    items.add_argument("--holder", help="only items this character has")
    items.set_defaults(run=items_command)

    chars = sub.add_parser("characters", help="characters known as of VOLUME:CHAPTER")
    chars.add_argument("--db", required=True)
    chars.add_argument("--position", required=True)
    chars.set_defaults(run=characters_command)

    profile = sub.add_parser("profile", help="a character's gated profile as of VOLUME:CHAPTER")
    profile.add_argument("--db", required=True)
    profile.add_argument("--character", required=True)
    profile.add_argument("--position", required=True)
    profile.set_defaults(run=profile_command)

    check = sub.add_parser("verify", help="integrity and spoiler scan of the stored data")
    check.add_argument("--db", required=True)
    check.set_defaults(run=verify_command)
    return top


def main(argv=None):
    args = parser().parse_args(argv)
    try:
        return args.run(args)
    except InputError as error:
        emit(event="error", message=str(error))
        print(error, file=sys.stderr)
        return EXIT_INPUT
    except ValueError as error:                      # bad position, unsupported database version, ...
        emit(event="error", message=str(error))
        print(error, file=sys.stderr)
        return EXIT_INPUT
    except LlmError as error:
        emit(event="error", message=str(error), fatal=error.fatal)
        print(error, file=sys.stderr)
        return EXIT_LLM


if __name__ == "__main__":
    sys.exit(main())
