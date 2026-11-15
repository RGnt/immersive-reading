import random
import re
import unittest

from immersive_knowledge.gate import Gate
from immersive_knowledge.position import StoryPosition, as_of
from immersive_knowledge.store import Store

from saga import CANARY, SECRET_NAME, apply_saga


def texts_of(view):
    """Every string a view exposes, to assert that something never appears in it."""
    out = []
    for e in view.entities.values():
        out += [n["name"] for n in e.names] + [a["value"] for a in e.attributes]
    out += [r["type"] + r["summary"] for r in view.relations + view.ended_relations]
    out += [ev["summary"] for ev in view.events]
    return "\n".join(out)


class SagaGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = apply_saga()
        cls.gate = Gate(cls.store)

    def view(self, text):
        return self.gate.view(StoryPosition.parse(text))

    def names(self, text):
        return sorted(n["name"] for e in self.view(text).entities.values() for n in e.names)

    def test_facts_appear_only_after_their_chapter(self):
        self.assertEqual(self.names("1:0"), [])
        self.assertEqual(self.names("1:1"), ["ミナ", "仮面の男"])
        self.assertEqual(self.names("1:2"), ["ミナ", "リク", "仮面の男"])
        self.assertIn(SECRET_NAME, self.names("1:3"))

    def test_the_current_chapter_is_never_visible(self):
        # as_of(1, 2) is the start of chapter 2: the reveal written in chapter 2 must not be there
        self.assertNotIn(SECRET_NAME, texts_of(self.view("1:2")))

    def test_names_are_revealed_at_their_own_position_and_identities_link_only_then(self):
        before, after = self.view("1:2"), self.view("1:3")
        self.assertEqual(before.links, ())
        self.assertEqual(len({before.group_of[e] for e in before.entities}), len(before.entities))
        self.assertEqual(after.group_of["e0002"], after.group_of["e0004"])
        merged = after.all_names(after.group_of["e0002"])
        self.assertEqual({n["name"] for n in merged}, {"仮面の男", SECRET_NAME})

    def test_a_fact_is_hidden_until_the_chapter_after_its_evidence_ends(self):
        self.assertNotIn(CANARY, texts_of(self.view("1:3")))   # established in chapter 3
        self.assertIn(CANARY, texts_of(self.view("2:0")))      # visible once chapter 3 is behind the reader

    def test_relationships_end_without_erasing_history(self):
        self.assertEqual([r["type"] for r in self.view("1:2").relations], ["友だち"])
        self.assertEqual([r["type"] for r in self.view("1:3").relations], ["友だち"])   # the end is written in chapter 3
        after = self.view("2:0")
        self.assertEqual(after.relations, ())
        self.assertEqual([r["type"] for r in after.ended_relations], ["友だち"])

    def test_events_participants_and_locations_are_filtered_to_visible_entities(self):
        view = self.view("1:3")     # the chapter-2 event names 青木先生, visible by now
        reveal = next(e for e in view.events if "正体" in e["summary"])
        self.assertEqual({p["entity"] for p in reveal["participants"]}, {"e0001", "e0004"})
        # a view where e0004 is not visible would drop the participant, never replace it with a placeholder
        early = self.view("1:2")
        self.assertTrue(all(p["entity"] in early.entities for e in early.events for p in e["participants"]))

    def test_entity_ids_carry_no_names(self):
        for entity_id in self.view("2:1").entities:
            self.assertRegex(entity_id, r"^e\d+$")
        self.assertEqual(self.store.query("SELECT COUNT(*) AS n FROM entities WHERE id NOT GLOB 'e[0-9]*'")[0]["n"], 0)

    def test_visibility_only_grows_for_entities_names_events_and_links(self):
        positions = [StoryPosition(v, c, o) for v in (1, 2) for c in range(5) for o in (0, 3, 40)]
        previous = None
        for position in sorted(positions):
            view = self.gate.view(position)
            current = ({*view.entities}, {n["name"] for e in view.entities.values() for n in e.names},
                       {e["id"] for e in view.events}, set(view.links))
            if previous:
                for before, now in zip(previous, current):
                    self.assertLessEqual(before, now, f"something disappeared at {position}")
            previous = current

    def test_graph_view_contracts_identity_groups(self):
        graph = self.gate.graph(StoryPosition.parse("1:3"))
        self.assertIn("e0002", graph.nodes)
        self.assertNotIn("e0004", graph.nodes)       # merged into e0002's group
        self.assertEqual(graph.number_of_edges(), 1)

    def test_offsets_gate_inside_a_chapter(self):
        end = self.store.query("SELECT first_key FROM entities WHERE id='e0002'")[0]["first_key"]
        position = StoryPosition.from_key(end)
        self.assertNotIn("e0002", self.gate.view(position).entities)                     # evidence ends exactly here: not before it
        self.assertIn("e0002", self.gate.view(StoryPosition(position.volume, position.chapter, position.offset + 1)).entities)


# ---------------------------------------------------------------------------- differential property test
def random_store(rng):
    """A store filled directly (not through ops) with random positions, to test the gate against an oracle."""
    store = Store()
    keys = sorted({StoryPosition(rng.randint(1, 2), rng.randint(0, 4), rng.randint(1, 60)).key for _ in range(40)})
    pick = lambda low=0: rng.choice([k for k in keys if k >= low] or [keys[-1]])
    entity_ids = []
    with store.transaction():
        for _ in range(rng.randint(2, 9)):
            first = pick()
            eid = store.add_entity(rng.choice(["character", "location"]), first, first)
            entity_ids.append(eid)
            for i in range(rng.randint(1, 3)):
                store.add_name(eid, f"名前{eid}{i}", rng.choice(["canonical", "alias"]), pick(first), first, [], preferred=i == 0)
            for _ in range(rng.randint(0, 3)):
                start = pick(first)
                end = pick(start) if rng.random() < 0.4 else None
                store.conn.execute("INSERT INTO attributes(entity_id,key,value,from_key,to_key,src_key) VALUES (?,?,?,?,?,?)",
                                   (eid, rng.choice(["status", "note"]), f"値{rng.random()}", start, end, start))
        for _ in range(rng.randint(0, 6)):
            a, b = rng.sample(entity_ids, 2)
            start = pick()
            end = pick(start) if rng.random() < 0.5 else None
            rid = store.add_relation(a, b, "x", False, "", start, start, [])
            if end is not None:
                store.end_relation(rid, end, end)
        for _ in range(rng.randint(0, 6)):
            start = pick()
            store.add_event(f"出来事{rng.random()}", start, None, start, [],
                            [(e, "") for e in rng.sample(entity_ids, rng.randint(0, min(3, len(entity_ids))))])
        for _ in range(rng.randint(0, 3)):
            a, b = rng.sample(entity_ids, 2)
            start = pick()
            store.add_identity_link(a, b, start, start, [])
    return store, keys


def oracle(store, key):
    """Brute-force visibility straight from the rules in spec 18, independent of gate.py."""
    q = store.query
    names = {}
    for r in q("SELECT * FROM names"):
        if r["from_key"] < key:
            names.setdefault(r["entity_id"], set()).add(r["name"])
    visible = set(names)
    attrs = {(r["entity_id"], r["value"]) for r in q("SELECT * FROM attributes")
             if r["from_key"] < key and (r["to_key"] is None or r["to_key"] >= key) and r["entity_id"] in visible}
    rels = {r["id"] for r in q("SELECT * FROM relations")
            if r["from_key"] < key and (r["to_key"] is None or r["to_key"] >= key) and r["src"] in visible and r["dst"] in visible}
    events = {}
    parts = {}
    for p in q("SELECT * FROM event_participants"):
        parts.setdefault(p["event_id"], set()).add(p["entity_id"])
    for e in q("SELECT * FROM events"):
        if e["from_key"] < key:
            events[e["id"]] = {x for x in parts.get(e["id"], set()) if x in visible}
    links = {(r["a"], r["b"]) for r in q("SELECT * FROM identity_links") if r["from_key"] < key and r["a"] in visible and r["b"] in visible}
    return visible, names, attrs, rels, events, links


class DifferentialGateTests(unittest.TestCase):
    def test_gate_equals_the_brute_force_oracle_on_random_stores(self):
        for seed in range(60):
            rng = random.Random(seed)
            store, keys = random_store(rng)
            gate = Gate(store)
            probes = rng.sample(keys, min(8, len(keys))) + [keys[0], keys[0] + 1, keys[-1], keys[-1] + 1, 0]
            for key in probes:
                with self.subTest(seed=seed, key=key):
                    view = gate.view(StoryPosition.from_key(key))
                    visible, names, attrs, rels, events, links = oracle(store, key)
                    self.assertEqual(set(view.entities), visible)
                    self.assertEqual({e: {n["name"] for n in v.names} for e, v in view.entities.items()}, names)
                    self.assertEqual({(e, a["value"]) for e, v in view.entities.items() for a in v.attributes}, attrs)
                    self.assertEqual({r["id"] for r in view.relations}, rels)
                    self.assertEqual({e["id"]: {p["entity"] for p in e["participants"]} for e in view.events}, events)
                    self.assertEqual(set(view.links), links)

    def test_nothing_in_any_view_comes_from_at_or_after_the_position(self):
        for seed in range(40):
            rng = random.Random(1000 + seed)
            store, keys = random_store(rng)
            gate = Gate(store)
            for key in rng.sample(keys, min(6, len(keys))):
                view = gate.view(StoryPosition.from_key(key))
                for entity in view.entities.values():
                    self.assertTrue(all(n["from_key"] < key for n in entity.names))
                    self.assertTrue(all(a["from_key"] < key for a in entity.attributes))
                self.assertTrue(all(r["from_key"] < key for r in view.relations))
                self.assertTrue(all(e["from_key"] < key for e in view.events))


if __name__ == "__main__":
    unittest.main()
