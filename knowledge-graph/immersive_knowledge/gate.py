"""The gate: what is visible at a story position (spec 18, section 4.2). **The only place where visibility is decided.**

``Gate.view(P)`` returns an immutable ``View``: every visible entity, name, attribute, relation, event and identity link at
``P`` and nothing else. Snapshots, profiles, the character list and the causal snapshot given to the model are all built from
a ``View``, never from raw tables, so there is exactly one rule to get right and to test.

Rules (``P.key`` is the integer position key; chapter-granular use passes ``as_of(volume, chapter)``):
  * an element is visible iff ``from_key < P.key``;
  * an attribute / relation is *valid* iff additionally ``to_key is None or to_key >= P.key``;
  * an entity exists for the reader iff at least one of its names is visible;
  * ``same_as`` links apply only from their own position and only between visible entities;
  * participants, endpoints and locations that are not visible are dropped, never replaced by a placeholder that names them;
  * an item holding follows the relation rule (valid at P) and needs both the item and its holder to be visible.
"""
from dataclasses import dataclass, field
from functools import cached_property

import networkx as nx

from .position import StoryPosition


@dataclass(frozen=True)
class EntityView:
    id: str
    kind: str
    names: tuple          # ({"name","type","preferred","from_key"}, ...) canonical/preferred first
    attributes: tuple     # ({"key","value","from_key"}, ...) valid at P, ordered by (key, from_key, id)


@dataclass(frozen=True)
class View:
    position: StoryPosition
    entities: dict                      # id -> EntityView (visible entities only)
    relations: tuple                    # valid relations between visible entities
    events: tuple                       # visible events, participants filtered to visible entities
    links: tuple                        # visible identity links as (a, b)
    ended_relations: tuple = ()         # relations that started and ended before P (history, never "current")
    group_of: dict = field(default_factory=dict)   # entity id -> representative id of its identity group
    holdings: tuple = ()                # item holdings valid at P (both item and holder visible)
    past_holdings: tuple = ()           # holdings that ended before P (history: what a character used to wear)

    # ------------------------------------------------------------ identity groups
    @cached_property
    def _members_of(self):
        groups = {}
        for entity, rep in self.group_of.items():
            groups.setdefault(rep, []).append(entity)
        return {rep: tuple(sorted(members)) for rep, members in groups.items()}

    def members(self, rep):
        return self._members_of.get(rep, ())

    def representatives(self):
        return tuple(sorted(set(self.group_of.values())))

    # ------------------------------------------------------------ richness / names
    @cached_property
    def _richness(self):
        """Attributes + relations + 2 x events per entity, counted in one pass (it used to be recomputed per call)."""
        table = {eid: len(entity.attributes) for eid, entity in self.entities.items()}
        for r in self.relations:
            for end in {r["src"], r["dst"]}:
                table[end] = table.get(end, 0) + 1
        for e in self.events:
            for entity in {p["entity"] for p in e["participants"]}:
                table[entity] = table.get(entity, 0) + 2
        return table

    def richness(self, entity_id):
        return self._richness[entity_id]

    def prominence(self, rep):
        """How much the reader has seen of this character (events count double)."""
        return sum(self.richness(m) for m in self.members(rep))

    def main_member(self, rep):
        """The identity-group member shown by default: the one the reader knows most about, then the lowest id."""
        return min(self.members(rep), key=lambda m: (-self.richness(m), m))

    def display_name(self, entity_id):
        """Best name of an entity (or of its group's main member) among the names visible at P."""
        rep = self.group_of.get(entity_id, entity_id)
        names = self.entities[self.main_member(rep)].names
        return names[0]["name"] if names else ""

    def all_names(self, rep):
        """All visible names of an identity group, display name first, without duplicates."""
        ordered = list(self.entities[self.main_member(rep)].names)
        for member in self.members(rep):
            ordered += [n for n in self.entities[member].names if n not in ordered]
        seen, result = set(), []
        for n in ordered:
            if n["name"] not in seen:
                seen.add(n["name"])
                result.append(n)
        return result


class Gate:
    def __init__(self, store):
        self.store = store

    def view(self, position):
        key = position.key
        q = self.store.query

        names_by_entity = {}
        for row in q("SELECT n.entity_id, n.name, n.type, n.preferred, n.from_key, n.id AS nid FROM names n WHERE n.from_key < ? "
                     "ORDER BY n.entity_id, n.from_key, n.id", (key,)):
            names_by_entity.setdefault(row["entity_id"], []).append(dict(row))
        visible_ids = set(names_by_entity)

        kinds = {r["id"]: r["kind"] for r in q("SELECT id, kind FROM entities")}
        attrs_by_entity = {}
        for row in q("SELECT entity_id, key, value, from_key, id FROM attributes WHERE from_key < ? AND (to_key IS NULL OR to_key >= ?) "
                     "ORDER BY entity_id, key, from_key, id", (key, key)):
            if row["entity_id"] in visible_ids:
                attrs_by_entity.setdefault(row["entity_id"], []).append({"key": row["key"], "value": row["value"], "from_key": row["from_key"]})

        entities = {}
        for eid in sorted(visible_ids):
            ordered = sorted(names_by_entity[eid], key=lambda n: (n["type"] != "canonical", not n["preferred"], n["from_key"], n["nid"]))
            entities[eid] = EntityView(
                id=eid, kind=kinds[eid],
                names=tuple({"name": n["name"], "type": n["type"], "preferred": bool(n["preferred"]), "from_key": n["from_key"]} for n in ordered),
                attributes=tuple(attrs_by_entity.get(eid, ())))

        relations = tuple(
            {"id": r["id"], "src": r["src"], "dst": r["dst"], "type": r["type"], "directed": bool(r["directed"]),
             "summary": r["summary"], "from_key": r["from_key"]}
            for r in q("SELECT * FROM relations WHERE from_key < ? AND (to_key IS NULL OR to_key >= ?) ORDER BY from_key, id", (key, key))
            if r["src"] in entities and r["dst"] in entities)

        ended = tuple(
            {"id": r["id"], "src": r["src"], "dst": r["dst"], "type": r["type"], "directed": bool(r["directed"]),
             "summary": r["summary"], "from_key": r["from_key"], "to_key": r["to_key"]}
            for r in q("SELECT * FROM relations WHERE from_key < ? AND to_key IS NOT NULL AND to_key < ? ORDER BY from_key, id", (key, key))
            if r["src"] in entities and r["dst"] in entities)

        participants = {}
        for row in q("SELECT event_id, entity_id, role FROM event_participants ORDER BY rowid"):
            if row["entity_id"] in entities:
                participants.setdefault(row["event_id"], []).append({"entity": row["entity_id"], "role": row["role"]})
        events = tuple(
            {"id": e["id"], "summary": e["summary"], "from_key": e["from_key"],
             "location": e["location"] if e["location"] in entities else None,
             "participants": participants.get(e["id"], []),
             "kind": e["kind"], "title": e["title"], "parent": e["parent"], "origin": e["origin"], "src_key": e["src_key"]}
            for e in q("SELECT * FROM events WHERE from_key < ? ORDER BY from_key, id", (key,)))
        visible_events = {e["id"] for e in events}
        events = tuple({**e, "parent": e["parent"] if e["parent"] in visible_events else None} for e in events)

        # A holding valid at P is *open* for the reader: when and why it ends is a later fact and must not be shown (I-K1).
        holdings = tuple(
            {"id": h["id"], "item": h["item_id"], "holder": h["holder_id"], "state": h["state"], "slot": h["slot"],
             "from_key": h["from_key"], "to_key": None, "reason": "", "replaced_by": "",
             "replaces": h["replaces"] if h["replaces"] in entities else ""}
            for h in q("SELECT * FROM holdings WHERE from_key < ? AND (to_key IS NULL OR to_key >= ?) ORDER BY from_key, id", (key, key))
            if h["item_id"] in entities and h["holder_id"] in entities)
        past = tuple(
            {"id": h["id"], "item": h["item_id"], "holder": h["holder_id"], "state": h["state"], "slot": h["slot"],
             "from_key": h["from_key"], "to_key": h["to_key"], "reason": h["end_reason"],
             "replaces": h["replaces"] if h["replaces"] in entities else "", "replaced_by": h["replaced_by"] if h["replaced_by"] in entities else ""}
            for h in q("SELECT * FROM holdings WHERE from_key < ? AND to_key IS NOT NULL AND to_key < ? ORDER BY from_key, id", (key, key))
            if h["item_id"] in entities and h["holder_id"] in entities)

        links = tuple((r["a"], r["b"]) for r in q("SELECT a, b FROM identity_links WHERE from_key < ? ORDER BY id", (key,))
                      if r["a"] in entities and r["b"] in entities)

        return View(position=position, entities=entities, relations=relations, events=events, links=links,
                    group_of=_identity_groups(entities, links), ended_relations=ended, holdings=holdings, past_holdings=past)

    # ------------------------------------------------------------ graph (analytics, never used for visibility)
    def graph(self, position):
        """NetworkX multigraph of identity groups (nodes) and valid relations (edges) as of ``position``."""
        view = self.view(position)
        graph = nx.MultiDiGraph()
        for rep in view.representatives():
            graph.add_node(rep, name=view.display_name(rep), kind=view.entities[view.main_member(rep)].kind)
        for r in view.relations:
            a, b = view.group_of[r["src"]], view.group_of[r["dst"]]
            if a != b:
                graph.add_edge(a, b, key=r["id"], type=r["type"], directed=r["directed"], summary=r["summary"])
        return graph


def _identity_groups(entities, links):
    parent = {e: e for e in entities}

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for a, b in links:
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[max(ra, rb)] = min(ra, rb)   # the lowest id is the representative: deterministic
    return {e: find(e) for e in entities}
