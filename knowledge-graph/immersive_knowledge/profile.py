"""Gated character profiles and the per-chapter snapshots the app reads (spec 18, FR-20..FR-22).

A profile is built **only** from a ``View`` (so it can contain nothing the gate hides), rendered through the
``profile.block`` template, and the result is passed through the hidden-name scanner. A text that fails the scan is never
stored. The app does no filtering of its own: it looks up the row for the reader's chapter.
"""
import json
from pathlib import Path

from .gate import Gate
from .position import StoryPosition, as_of
from .scanner import LazyScanner, SpoilerLeak
from .store import SLOTS
from .story import StoryCards
from .templates import load_directory, render

PROMPTS_DIR = Path(__file__).with_name("prompts")

# order of the trait lines in the rendered block
TRAIT_ORDER = ("age", "gender", "appearance", "occupation", "residence", "personality", "speech_style", "goal", "motivation",
               "background", "status", "ability", "note")
SINGLE_VALUED = ("age", "gender", "appearance", "occupation", "residence", "personality", "speech_style", "goal", "motivation",
                 "background", "status")


def position_label(position):
    """Human wording for the prompt: chapters are 0-based internally, 1-based for people."""
    return f"volume {position.volume}, chapter {position.chapter + 1}"


MAX_FORMER_GEAR = 8


def gear_of(view, member_set):
    """What the identity group wears now, carries now, and used to wear (newer gear has replaced it), as visible in ``view``.

    ``equipment`` is one entry per item in slot order (accessories can be several); an item moved to a slot that already held
    something has *replaced* it, the gate has closed the older holding. ``former_gear`` lists closed *equipped* holdings of items
    the group no longer holds, most recent last.
    """
    mine = [h for h in view.holdings if h["holder"] in member_set]
    equipment = [{"slot": h["slot"], "item": view.display_name(h["item"]), "replaces": view.display_name(h["replaces"]) if h["replaces"] else None}
                 for h in sorted((h for h in mine if h["state"] == "equipped"), key=lambda h: (SLOTS.index(h["slot"]), h["from_key"], h["id"]))]
    belongings = [view.display_name(h["item"]) for h in sorted((h for h in mine if h["state"] == "owned"), key=lambda h: (h["from_key"], h["id"]))]
    held_now = {h["item"] for h in mine}
    former = []
    for h in sorted((h for h in view.past_holdings if h["holder"] in member_set and h["state"] == "equipped" and h["item"] not in held_now),
                    key=lambda h: (h["to_key"], h["id"])):
        entry = {"slot": h["slot"], "item": view.display_name(h["item"]), "reason": h["reason"],
                 "replaced_by": view.display_name(h["replaced_by"]) if h["replaced_by"] else None,
                 "until_chapter": StoryPosition.from_key(h["to_key"]).chapter}
        if entry not in former:
            former.append(entry)
    return equipment, belongings, former[-MAX_FORMER_GEAR:]


def build_profile(view, rep, *, max_timeline=25, max_samples=4):
    """Plain-data profile of the identity group ``rep`` as visible in ``view``."""
    members = view.members(rep)
    member_set = set(members)
    names = view.all_names(rep)
    display = view.display_name(rep)

    attributes = [a for m in members for a in view.entities[m].attributes]
    traits = {}
    for key in TRAIT_ORDER:
        rows = sorted((a for a in attributes if a["key"] == key), key=lambda a: a["from_key"])
        if not rows:
            continue
        traits[key] = [rows[-1]["value"]] if key in SINGLE_VALUED else list(dict.fromkeys(r["value"] for r in rows))
    samples = [a["value"] for a in sorted((a for a in attributes if a["key"] == "speech_sample"), key=lambda a: a["from_key"])]
    samples = list(dict.fromkeys(samples))[-max_samples:]

    def other_group(relation):
        a, b = view.group_of[relation["src"]], view.group_of[relation["dst"]]
        return (b, False) if a == rep else (a, True) if b == rep else (None, False)

    relationships, former = [], []
    for source, target in ((view.relations, relationships), (view.ended_relations, former)):
        for r in source:
            other, reversed_ = other_group(r)
            if other is None or other == rep:
                continue
            target.append({"type": r["type"], "other": view.display_name(other), "summary": r["summary"],
                           "directed": r["directed"], "reversed": reversed_})

    timeline = []
    for event in view.events:
        if any(p["entity"] in member_set for p in event["participants"]):
            timeline.append({"summary": event["summary"], "from_key": event["from_key"], "kind": event["kind"], "title": event["title"],
                             "chapter": StoryPosition.from_key(event["from_key"]).chapter})
    timeline = timeline[-max_timeline:]
    equipment, belongings, former_gear = gear_of(view, member_set)

    profile = {
        "character_id": rep,
        "as_of": str(view.position),
        "as_of_label": position_label(view.position),
        "name": display,
        "names": [n["name"] for n in names],
        "aliases": [n["name"] for n in names if n["name"] != display],
        "traits": traits,
        "speech_samples": samples,
        "relationships": relationships,
        "former_relationships": former,
        "timeline": timeline,
        "equipment": equipment,
        "belongings": belongings,
        "former_gear": former_gear,
        "prominence": view.prominence(rep),
        "voice_cues": {k: traits[k][0] for k in ("age", "gender", "personality", "speech_style") if k in traits},
    }
    return profile


def _lines(profile):
    traits = [f"{key}: {'; '.join(values)}" for key, values in profile["traits"].items()]
    relationships = [f"{r['type']}: {r['other']}" + (f" ({r['summary']})" if r["summary"] else "") for r in profile["relationships"]]
    relationships += [f"former {r['type']}: {r['other']} (ended)" for r in profile["former_relationships"]]
    timeline = [t["summary"] for t in profile["timeline"]]
    return traits, relationships, timeline


def _possessions(profile):
    lines = [f"equipped ({e['slot']}): {e['item']}" + (f" (replaces {e['replaces']})" if e.get("replaces") else "") for e in profile.get("equipment", [])]
    if profile.get("belongings"):
        lines.append("carrying: " + "、".join(profile["belongings"]))
    lines += [f"formerly equipped ({g['slot']}): {g['item']}" + (f" (replaced by {g['replaced_by']})" if g.get("replaced_by") else "")
              for g in profile.get("former_gear", [])]
    return lines


def render_block(profile, template=None):
    """Text block for a persona prompt. ``template`` defaults to the packaged ``profile.block``."""
    template = template or load_directory(PROMPTS_DIR)["profile.block"]
    traits, relationships, timeline = _lines(profile)
    return render(template, {
        "as_of": profile["as_of_label"], "name": profile["name"], "aliases": "、".join(profile["aliases"]),
        "traits": traits, "speech_samples": [f"「{s}」" if not s.startswith("「") else s for s in profile["speech_samples"]],
        "relationships": relationships, "timeline": timeline, "possessions": _possessions(profile),
    })


def profile_text(profile, block):
    """Every piece of free text that leaves the builder for this profile (scanned as one string)."""
    parts = [block, profile["name"], *profile["aliases"], *profile["speech_samples"]]
    parts += [v for values in profile["traits"].values() for v in values]
    parts += [r["other"] + " " + r["summary"] for r in profile["relationships"] + profile["former_relationships"]]
    parts += [t["summary"] + " " + t.get("title", "") for t in profile["timeline"]]
    parts += _possessions(profile)
    return "\n".join(parts)


def render_checked(store, view, rep, template=None, scanner=None):
    """Profile + block, or ``SpoilerLeak`` if any text names someone the reader has not met.

    ``scanner`` lets a caller that renders many profiles at one position prepare the hidden-name scan once.
    """
    profile = build_profile(view, rep)
    block = render_block(profile, template)
    hits = (scanner or LazyScanner(store, view)).scan(profile_text(profile, block))
    if hits:
        raise SpoilerLeak(f"profile of {rep} as of {view.position}", hits)
    return profile, block


def boundaries(store):
    """Chapter boundaries that can be asked for: ``as_of(v, c)`` for every built chapter, plus the end of each volume."""
    rows = [r for r in store.chapters() if r["status"] == "built"]
    result = [as_of(r["volume"], r["chapter"]) for r in rows]
    last = {}
    for r in rows:
        last[r["volume"]] = max(last.get(r["volume"], -1), r["chapter"])
    result += [as_of(v, c + 1) for v, c in last.items()]
    return sorted(set(result))


def generate_snapshots(store, *, min_prominence=1, template=None):
    """(Re)generate every gated snapshot: character profiles and story cards (events, items).

    Returns ``{"stored": n, "blocked": [...], "events": n, "items": n, "story_blocked": [...]}``; blocked ones are not stored.
    """
    gate = Gate(store)
    template = template or load_directory(PROMPTS_DIR)["profile.block"]
    stored, blocked = 0, []
    with store.transaction():
        for table in ("profile_snapshots", "profile_snapshot_members", "story_event_cards", "story_item_cards"):
            store.conn.execute(f"DELETE FROM {table}")
        cards = StoryCards(store)
        for position in boundaries(store):
            view = gate.view(position)
            scanner = LazyScanner(store, view)          # prepared once per boundary, shared by every profile and card
            cards.add(view, scanner)
            for rep in view.representatives():
                main = view.entities[view.main_member(rep)]
                if main.kind != "character" or view.prominence(rep) < min_prominence:
                    continue
                try:
                    profile, block = render_checked(store, view, rep, template, scanner)
                except SpoilerLeak as leak:
                    blocked.append({"character_id": rep, "position": str(position), "hits": list(leak.hits)})
                    continue
                store.conn.execute(
                    "INSERT INTO profile_snapshots(character_id, volume, chapter, display_name, prominence, profile_json, block_text) VALUES (?,?,?,?,?,?,?)",
                    (rep, position.volume, position.chapter, profile["name"], profile["prominence"], json.dumps(profile, ensure_ascii=False), block))
                for member in view.members(rep):
                    store.conn.execute("INSERT INTO profile_snapshot_members(character_id, volume, chapter, member_id) VALUES (?,?,?,?)",
                                       (rep, position.volume, position.chapter, member))
                stored += 1
    return {"stored": stored, "blocked": blocked, "events": cards.stored["event"], "items": cards.stored["item"],
            "story_blocked": cards.blocked}


# --------------------------------------------------------------------------- reading snapshots (what the app does, in Python for tests/CLI)
def _boundary_row(store, volume, chapter):
    """The snapshot boundary for a reader at ``(volume, chapter)``: that chapter if present, else the closest earlier one."""
    row = store.query(
        "SELECT volume, chapter FROM profile_snapshots WHERE (volume < ? OR (volume = ? AND chapter <= ?)) "
        "ORDER BY volume DESC, chapter DESC LIMIT 1", (volume, volume, chapter))
    return (row[0]["volume"], row[0]["chapter"]) if row else None


def list_characters(store, volume, chapter):
    boundary = _boundary_row(store, volume, chapter)
    if boundary is None:
        return []
    rows = store.query("SELECT character_id, display_name, prominence FROM profile_snapshots WHERE volume=? AND chapter=? "
                       "ORDER BY prominence DESC, character_id", boundary)
    return [{"character_id": r["character_id"], "name": r["display_name"], "prominence": r["prominence"]} for r in rows]


def get_profile(store, volume, chapter, character_id):
    """The stored snapshot for a character (any member id of its group works): the latest one **at or before** the reader's chapter.

    Normally that is exactly the reader's chapter. If the snapshot of that chapter was blocked by the spoiler scanner (or the
    character was not prominent enough yet), the character's last earlier snapshot is used: older means less information, never more.
    """
    row = store.query(
        "SELECT s.character_id, s.display_name, s.profile_json, s.block_text FROM profile_snapshots s "
        "JOIN profile_snapshot_members m ON m.character_id = s.character_id AND m.volume = s.volume AND m.chapter = s.chapter "
        "WHERE (s.volume < ? OR (s.volume = ? AND s.chapter <= ?)) AND m.member_id = ? "
        "ORDER BY s.volume DESC, s.chapter DESC LIMIT 1", (volume, volume, chapter, character_id))
    if not row:
        return None
    r = row[0]
    return {"character_id": r["character_id"], "name": r["display_name"], "profile": json.loads(r["profile_json"]), "block": r["block_text"]}
