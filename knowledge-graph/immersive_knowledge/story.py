"""Gated story cards: the events graph and the item holdings as the reader sees them (spec 22).

Like the character snapshots, cards are precomputed **from a gated ``View``** and stored once per *change*: an event or item gets a
row at the first boundary where it is visible and a new row whenever its rendering changes (a participant becomes known, an item
changes hands). The reader at ``(volume, chapter)`` sees, for every event and item, the latest row at or before their chapter:
older means less information, never more. The app only does that lookup (``list_events`` / ``list_items`` here are the
reference implementation of the same two queries). A card whose text names someone the reader has not met is **not stored**
(spoiler scanner), so the reader keeps the previous, older card.
"""
import json

from .position import StoryPosition
from .scanner import LazyScanner

# Newest events first, at most this many, unless the caller asks for another limit.
DEFAULT_EVENT_LIMIT = 200


def _where(view, entity_id):
    return {"id": view.group_of.get(entity_id, entity_id), "name": view.display_name(entity_id),
            "ids": list(view.members(view.group_of.get(entity_id, entity_id)))}


def _position(key):
    p = StoryPosition.from_key(key)
    return {"volume": p.volume, "chapter": p.chapter}


def event_card(view, event):
    """Plain data of one visible event: participants by identity group (ids of every member, so a filter by character keeps
    working after an identity reveal), location, parent and the position of the event."""
    participants, seen = [], set()
    for p in event["participants"]:
        entity = _where(view, p["entity"])
        key = (entity["id"], p["role"])
        if key in seen:
            continue
        seen.add(key)
        participants.append({**entity, "kind": view.entities[p["entity"]].kind, "role": p["role"]})
    location = _where(view, event["location"]) if event["location"] else None
    return {"id": event["id"], "kind": event["kind"] or "other", "title": event["title"], "summary": event["summary"],
            **_position(event["from_key"]), "parent": event["parent"], "participants": participants, "location": location,
            "typed": bool(event["kind"])}


_ITEM_ATTRIBUTES = ("appearance", "ability", "note", "status")


def item_card(view, item_id):
    """Plain data of one visible item: description, who has it now (and how), and its whole visible history of holders."""
    entity = view.entities[item_id]
    description = {}
    for a in entity.attributes:
        if a["key"] in _ITEM_ATTRIBUTES:
            description.setdefault(a["key"], []).append(a["value"])
    mine = sorted([h for h in view.holdings + view.past_holdings if h["item"] == item_id], key=lambda h: (h["from_key"], h["id"]))
    def other(item):
        return {"id": item, "name": view.display_name(item)} if item else None

    history = [{"holder": _where(view, h["holder"]), "state": h["state"], "slot": h["slot"], "from": _position(h["from_key"]),
                "to": _position(h["to_key"]) if h["to_key"] is not None else None, "reason": h["reason"],
                "replaces": other(h["replaces"]), "replaced_by": other(h["replaced_by"])} for h in mine]
    current = next((h for h in history if h["to"] is None), None)
    last = history[-1] if history else None
    return {"id": item_id, "name": entity.names[0]["name"], "names": [n["name"] for n in entity.names], "description": description,
            "holder": current["holder"] if current else None, "state": current["state"] if current else "",
            "slot": current["slot"] if current else "", "history": history,
            # the graph tags: this item replaced that one in its slot / it was replaced by that one
            "replaces": current["replaces"] if current else None,
            "replaced_by": last["replaced_by"] if last and not current else None}


def card_text(card):
    """Every piece of free text of a card (what the scanner reads)."""
    parts = [card.get("title", ""), card.get("summary", ""), card.get("name", ""), *card.get("names", [])]
    parts += [p["name"] + " " + p["role"] for p in card.get("participants", [])]
    if card.get("location"):
        parts.append(card["location"]["name"])
    for values in card.get("description", {}).values():
        parts += values
    if card.get("holder"):
        parts.append(card["holder"]["name"])
    parts += [h["holder"]["name"] + " " + h["reason"] for h in card.get("history", [])]
    parts += [o["name"] for h in card.get("history", []) for o in (h.get("replaces"), h.get("replaced_by")) if o]
    return "\n".join(parts)


class StoryCards:
    """Collects the change rows while ``generate_snapshots`` walks the boundaries (one gated view per boundary, shared with the profiles)."""

    def __init__(self, store):
        self.store = store
        self._last = {"event": {}, "item": {}}
        self.stored = {"event": 0, "item": 0}
        self.blocked = []

    def add(self, view, scanner=None):
        position = view.position
        scanner = scanner or LazyScanner(self.store, view)
        rows = []
        for event in view.events:
            rows.append(("event", event["id"], event["from_key"], event_card(view, event)))
        for item_id, entity in view.entities.items():
            if entity.kind == "item":
                rows.append(("item", item_id, min(n["from_key"] for n in entity.names), item_card(view, item_id)))
        for kind, ident, sort_key, card in rows:
            encoded = json.dumps(card, ensure_ascii=False, sort_keys=True)
            if self._last[kind].get(ident) == encoded:
                continue
            hits = scanner.scan(card_text(card))
            if hits:
                self.blocked.append({"kind": kind, "id": ident, "position": str(position), "hits": hits})
                continue
            table = "story_event_cards" if kind == "event" else "story_item_cards"
            column = "event_id" if kind == "event" else "item_id"
            self.store.conn.execute(
                f"INSERT OR REPLACE INTO {table}({column}, volume, chapter, sort_key, card_json) VALUES (?,?,?,?,?)",
                (ident, position.volume, position.chapter, sort_key, encoded))
            self._last[kind][ident] = encoded
            self.stored[kind] += 1


# --------------------------------------------------------------------------- reading cards (what the app does, in Python for tests/CLI)
_LATEST = ("(c.volume, c.chapter) = (SELECT d.volume, d.chapter FROM {table} d WHERE d.{column} = c.{column} AND "
           "(d.volume < ? OR (d.volume = ? AND d.chapter <= ?)) ORDER BY d.volume DESC, d.chapter DESC LIMIT 1)")


def member_ids(store, volume, chapter, character_id):
    """Entity ids of the identity group a character id belongs to at the reader's position (the id itself if there is no snapshot)."""
    row = store.query(
        "SELECT m.character_id, m.volume, m.chapter FROM profile_snapshot_members m WHERE m.member_id = ? AND "
        "(m.volume < ? OR (m.volume = ? AND m.chapter <= ?)) ORDER BY m.volume DESC, m.chapter DESC LIMIT 1",
        (character_id, volume, volume, chapter))
    if not row:
        return {character_id}
    r = row[0]
    return {m["member_id"] for m in store.query(
        "SELECT member_id FROM profile_snapshot_members WHERE character_id=? AND volume=? AND chapter=?", (r["character_id"], r["volume"], r["chapter"]))}


def _cards(store, table, column, volume, chapter):
    sql = (f"SELECT c.card_json FROM {table} c WHERE " + _LATEST.format(table=table, column=column) + f" ORDER BY c.sort_key, c.{column}")
    return [json.loads(r["card_json"]) for r in store.query(sql, (volume, volume, chapter))]


def list_events(store, volume, chapter, *, character_id=None, kind=None, limit=DEFAULT_EVENT_LIMIT):
    """Events the reader knows at ``(volume, chapter)``, newest first. ``total`` counts all that match before ``limit``."""
    cards = _cards(store, "story_event_cards", "event_id", volume, chapter)
    if character_id:
        members = member_ids(store, volume, chapter, character_id)
        cards = [c for c in cards if any(members & set(p["ids"]) for p in c["participants"])]
    if kind:
        cards = [c for c in cards if c["kind"] == kind]
    cards.reverse()
    return {"total": len(cards), "events": cards[:limit] if limit else cards}


def list_items(store, volume, chapter, *, holder_id=None):
    """Items the reader knows at ``(volume, chapter)``; with ``holder_id`` only the items that character has right now."""
    cards = _cards(store, "story_item_cards", "item_id", volume, chapter)
    if holder_id:
        members = member_ids(store, volume, chapter, holder_id)
        cards = [c for c in cards if c["holder"] and members & set(c["holder"]["ids"])]
    return cards
