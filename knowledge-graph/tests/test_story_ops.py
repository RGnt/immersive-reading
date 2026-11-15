"""Story pass operations, item holdings and rollbacks (spec 22)."""
import unittest

from immersive_knowledge.ops import ChapterContext, apply_ops, PASS_STORY
from immersive_knowledge.position import StoryPosition, as_of, chapter_key
from immersive_knowledge.store import Store

import loot_saga
from loot_saga import TEXTS, q


def story_ctx(volume, chapter):
    return ChapterContext(volume, chapter, TEXTS[(volume, chapter)], 0, PASS_STORY, as_of(volume, chapter + 1).key)


def character_pass(store, upto=(1, 3)):
    for key, answer in sorted(loot_saga.CHARACTER_ANSWERS.items()):
        if key <= upto:
            apply_ops(store, ChapterContext(*key, TEXTS[key]), answer["ops"])
    return store


def story_pass(store, upto=(2, 0), answers=None):
    answers = answers or loot_saga.STORY_ANSWERS
    for key, answer in sorted(answers.items()):
        if key <= upto:
            results = apply_ops(store, story_ctx(*key), answer["ops"])
            rejected = [(r.op, r.reason) for r in results if r.status == "rejected"]
            assert not rejected, (key, rejected)
    return store


def saga():
    return story_pass(character_pass(Store()))


def holdings(store, item):
    return [dict(r) for r in store.query("SELECT * FROM holdings WHERE item_id=? ORDER BY from_key, id", (item,))]


class HoldingTests(unittest.TestCase):
    def test_the_scripted_saga_applies_without_a_single_rejection(self):
        store = saga()
        self.assertEqual([r["kind"] for r in store.query("SELECT kind FROM entities WHERE origin='s' ORDER BY id")],
                         ["item", "creature", "item", "location", "creature", "item", "item", "item"])
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM events WHERE origin='s'")[0]["n"], 6)

    def test_newer_gear_in_the_same_slot_replaces_older_gear(self):
        store = saga()
        knife, iron, flame = holdings(store, "e0003"), holdings(store, "e0005"), holdings(store, "e0008")
        self.assertEqual((knife[0]["state"], knife[0]["slot"], knife[0]["end_reason"]), ("equipped", "weapon", "replaced"))
        self.assertIsNotNone(knife[0]["to_key"])
        self.assertEqual(iron[0]["end_reason"], "replaced")           # the flame sword took the weapon slot; only afterwards the iron sword was handed over
        self.assertEqual(flame[0]["to_key"], None)                    # the newest weapon is still equipped
        # the knife was replaced exactly where the iron sword was equipped (the same position, not an approximation)
        self.assertEqual(knife[0]["to_key"], iron[0]["from_key"])
        self.assertEqual(iron[0]["to_key"], flame[0]["from_key"])

    def test_a_replacement_is_a_link_between_the_two_items(self):
        store = saga()
        knife, iron, flame = holdings(store, "e0003")[0], holdings(store, "e0005")[0], holdings(store, "e0008")[0]
        self.assertEqual((knife["replaces"], knife["replaced_by"]), ("", "e0005"))
        self.assertEqual((iron["replaces"], iron["replaced_by"]), ("e0003", "e0008"))
        self.assertEqual((flame["replaces"], flame["replaced_by"]), ("e0005", ""))
        handed = holdings(store, "e0005")[1]                                   # given to Aya: no slot involved
        self.assertEqual((handed["replaces"], handed["replaced_by"]), ("", ""))

    def test_a_transfer_or_a_change_of_state_is_not_a_replacement(self):
        store = Store()
        for e in ("character", "character", "item"):
            store.add_entity(e, 0, 0)
        store.set_holding("e0003", "e0001", "owned", "", 10, 0, [])
        store.set_holding("e0003", "e0001", "equipped", "weapon", 20, 0, [])
        store.set_holding("e0003", "e0002", "equipped", "weapon", 30, 0, [])
        self.assertEqual({(h["replaces"], h["replaced_by"]) for h in holdings(store, "e0003")}, {("", "")})

    def test_a_replacement_listed_out_of_order_links_both_ways(self):
        store = Store()
        for e in ("character", "item", "item"):
            store.add_entity(e, 0, 0)
        store.set_holding("e0003", "e0001", "equipped", "weapon", 50, 0, [])     # the newer sword, listed first
        store.set_holding("e0002", "e0001", "equipped", "weapon", 20, 0, [])     # the older one, earlier in the text
        self.assertEqual(holdings(store, "e0002")[0]["replaced_by"], "e0003")
        self.assertEqual(holdings(store, "e0003")[0]["replaces"], "e0002")

    def test_rolling_back_the_replacing_chapter_removes_the_link_from_the_older_item(self):
        store = saga()
        store.rollback_story_from(chapter_key(1, 2))                            # the flame sword's chapter
        iron = holdings(store, "e0005")[0]
        self.assertEqual((iron["to_key"], iron["end_reason"], iron["replaced_by"]), (None, "", ""))
        self.assertEqual(iron["replaces"], "e0003")                              # chapter 1's own link stays

    def test_the_replaced_item_is_not_assumed_to_be_kept(self):
        store = saga()
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM holdings WHERE item_id='e0003' AND to_key IS NULL")[0]["n"], 0)

    def test_an_item_has_one_holder_at_a_time(self):
        store = saga()
        iron = holdings(store, "e0005")
        self.assertEqual([(h["holder_id"], h["state"]) for h in iron], [("e0001", "equipped"), ("e0002", "owned")])
        self.assertLessEqual(iron[0]["to_key"], iron[1]["from_key"])      # Ren's holding ended before Aya's began: never two holders
        self.assertEqual(iron[1]["end_reason"], "lost")
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM holdings WHERE item_id='e0005' AND to_key IS NULL")[0]["n"], 0)

    def test_accessories_do_not_replace_each_other(self):
        store = Store()
        for e in ("character", "item", "item"):
            store.add_entity(e, 0, 0)
        store.set_holding("e0002", "e0001", "equipped", "accessory", 10, 0, [])
        store.set_holding("e0003", "e0001", "equipped", "accessory", 20, 0, [])
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM holdings WHERE to_key IS NULL")[0]["n"], 2)

    def test_two_characters_wear_the_same_slot_independently(self):
        store = Store()
        for e in ("character", "character", "item", "item"):
            store.add_entity(e, 0, 0)
        store.set_holding("e0003", "e0001", "equipped", "weapon", 10, 0, [])
        store.set_holding("e0004", "e0002", "equipped", "weapon", 20, 0, [])
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM holdings WHERE to_key IS NULL")[0]["n"], 2)

    def test_carrying_then_wielding_the_same_item_is_one_change(self):
        store = Store()
        for e in ("character", "item"):
            store.add_entity(e, 0, 0)
        store.set_holding("e0002", "e0001", "owned", "", 10, 0, [])
        store.set_holding("e0002", "e0001", "equipped", "weapon", 20, 0, [])
        rows = holdings(store, "e0002")
        self.assertEqual([(r["state"], r["to_key"], r["end_reason"]) for r in rows], [("owned", 20, "changed"), ("equipped", None, "")])

    def test_the_same_claim_twice_is_recorded_once(self):
        store = Store()
        for e in ("character", "item"):
            store.add_entity(e, 0, 0)
        self.assertIsNotNone(store.set_holding("e0002", "e0001", "owned", "", 10, 0, []))
        self.assertIsNone(store.set_holding("e0002", "e0001", "owned", "", 30, 0, []))
        self.assertEqual(len(holdings(store, "e0002")), 1)

    def test_a_claim_listed_out_of_order_is_valid_only_until_the_later_one_starts(self):
        store = Store()
        for e in ("character", "item", "item"):
            store.add_entity(e, 0, 0)
        store.set_holding("e0003", "e0001", "equipped", "weapon", 50, 0, [])      # later in the text, listed first
        store.set_holding("e0002", "e0001", "equipped", "weapon", 20, 0, [])      # earlier in the text
        early = holdings(store, "e0002")[0]
        self.assertEqual((early["from_key"], early["to_key"], early["end_reason"]), (20, 50, "replaced"))
        self.assertIsNone(holdings(store, "e0003")[0]["to_key"])

    def test_a_claim_at_the_same_position_as_an_existing_one_leaves_no_empty_interval(self):
        store = Store()
        for e in ("character", "item", "item"):
            store.add_entity(e, 0, 0)
        store.set_holding("e0003", "e0001", "equipped", "weapon", 50, 0, [])
        self.assertIsNone(store.set_holding("e0002", "e0001", "equipped", "weapon", 50, 0, []))
        self.assertEqual(holdings(store, "e0002"), [])

    def test_losing_an_item_nobody_holds_is_rejected(self):
        store = character_pass(Store())
        story_pass(store, upto=(1, 0))
        results = apply_ops(store, story_ctx(1, 1), [
            {"op": "add_entity", "ref": "n1", "kind": "item", "names": [{"name": "鉄の剣", "type": "canonical"}], "evidence": [q("鉄の剣が落ちた")]},
            {"op": "item_state", "item": "n1", "state": "lost", "evidence": [q("鉄の剣が落ちた")]}])
        self.assertEqual([r.status for r in results], ["applied", "rejected"])
        self.assertIn("nobody is recorded", results[1].reason)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM evidence WHERE origin='s' AND quote LIKE '%落ちた%'")[0]["n"], 1)  # only the entity's

    def test_losing_an_item_ends_it_where_the_text_says_so(self):
        store = saga()
        shield = holdings(store, "e0009")[0]
        self.assertEqual(shield["end_reason"], "lost")
        self.assertEqual(StoryPosition.from_key(shield["to_key"]).volume, 2)


class StoryOperationTests(unittest.TestCase):
    def setUp(self):
        self.store = character_pass(Store(), upto=(1, 1))
        story_pass(self.store, upto=(1, 0))

    def run_ops(self, ops, ctx=None):
        return apply_ops(self.store, ctx or story_ctx(1, 1), ops)

    def test_the_story_pass_can_use_entities_the_character_pass_found_in_the_same_chapter(self):
        store = character_pass(Store(), upto=(1, 0))
        results = apply_ops(store, story_ctx(1, 0), [{"op": "add_event", "kind": "quest", "title": "依頼", "summary": "依頼を知らされた",
                                                       "participants": [{"entity": "e0001", "role": ""}], "evidence": [q("ゴブリン討伐の依頼があります")]}])
        self.assertEqual(results[0].status, "applied", results[0].reason)
        # the same reference is unknown to a pass that has no horizon (the character pass only knows earlier text)
        results = apply_ops(store, ChapterContext(1, 0, TEXTS[(1, 0)]), [{"op": "add_event", "summary": "依頼を知らされた",
                                                                         "participants": [{"entity": "e0001", "role": ""}], "evidence": [q("ゴブリン討伐の依頼があります")]}])
        self.assertEqual(results[0].status, "rejected")

    def test_the_story_pass_cannot_create_characters(self):
        results = self.run_ops([{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ゴブリン", "type": "canonical"}],
                                 "evidence": [q("ゴブリンを二十匹倒した")]}])
        self.assertIn("character pass", results[0].reason)

    def test_the_character_pass_cannot_record_items_or_typed_events(self):
        store = character_pass(Store(), upto=(1, 0))
        results = apply_ops(store, ChapterContext(1, 1, TEXTS[(1, 1)]), [
            {"op": "item_state", "item": "e0001", "holder": "e0001", "state": "owned", "evidence": [q("鉄の剣が落ちた")]}])
        self.assertIn("does not accept", results[0].reason)

    def test_a_character_pass_event_ignores_story_fields(self):
        store = character_pass(Store(), upto=(1, 0))
        self.assertEqual(store.query("SELECT kind, title, origin FROM events")[0]["kind"], "")
        self.assertEqual(store.query("SELECT origin FROM events")[0]["origin"], "c")

    def test_story_events_need_a_known_kind_and_a_title(self):
        base = {"op": "add_event", "summary": "x", "participants": [], "evidence": [q("森でゴブリンを二十匹倒した")]}
        results = self.run_ops([dict(base, kind="party", title="t"), dict(base, kind="hunt"), dict(base, kind="hunt", title="")])
        self.assertEqual([r.status for r in results], ["rejected"] * 3)
        self.assertIn("invalid event kind", results[0].reason)

    def test_a_parent_must_be_an_event_the_model_was_shown_or_declared_earlier(self):
        base = {"op": "add_event", "kind": "hunt", "title": "t", "summary": "x", "participants": [], "evidence": [q("森でゴブリンを二十匹倒した")]}
        quest = self.store.query("SELECT id FROM events WHERE kind='quest'")[0]["id"]
        ok, unknown, malformed = self.run_ops([dict(base, parent=quest), dict(base, parent="v0099"), dict(base, parent="zz")])
        self.assertEqual(ok.status, "applied", ok.reason)
        self.assertEqual((unknown.status, malformed.status), ("rejected", "rejected"))
        self.assertEqual(self.store.query("SELECT parent FROM events WHERE id=?", (ok.created[0],))[0]["parent"], quest)

    def test_an_event_ref_cannot_be_reused_and_cannot_look_like_an_id(self):
        base = {"op": "add_event", "kind": "hunt", "title": "t", "summary": "x", "participants": [], "evidence": [q("森でゴブリンを二十匹倒した")]}
        results = self.run_ops([dict(base, ref="a"), dict(base, ref="a"), dict(base, ref="v0042")])
        self.assertEqual([r.status for r in results], ["applied", "rejected", "rejected"])

    def test_item_state_validation(self):
        quote = [q("鉄の剣が落ちた")]
        results = self.run_ops([
            {"op": "add_entity", "ref": "n1", "kind": "item", "names": [{"name": "鉄の剣", "type": "canonical"}], "evidence": quote},
            {"op": "item_state", "item": "e0001", "holder": "e0001", "state": "owned", "evidence": quote},             # a character is not an item
            {"op": "item_state", "item": "n1", "holder": "e0001", "state": "worn", "evidence": quote},                 # unknown state
            {"op": "item_state", "item": "n1", "holder": "e0001", "state": "equipped", "evidence": quote},             # no slot
            {"op": "item_state", "item": "n1", "holder": "e0001", "state": "equipped", "slot": "hat", "evidence": quote},
            {"op": "item_state", "item": "n1", "state": "owned", "evidence": quote},                                   # no holder
            {"op": "item_state", "item": "n1", "holder": "e0003", "state": "owned", "evidence": quote},                # an item cannot hold an item
            {"op": "item_state", "item": "n1", "holder": "e0001", "state": "owned"},                                   # no evidence
        ])
        self.assertEqual([r.status for r in results], ["applied"] + ["rejected"] * 7)

    def test_story_rows_are_tagged_with_their_origin(self):
        origins = {t: {r["origin"] for r in self.store.query(f"SELECT origin FROM {t}")} for t in ("entities", "names", "events", "evidence", "ops_log")}
        self.assertEqual(origins["entities"], {"c", "s"})
        self.assertEqual(origins["events"], {"c", "s"})
        self.assertEqual(self.store.query("SELECT origin FROM entities WHERE id='e0003'")[0]["origin"], "s")
        self.assertEqual(self.store.query("SELECT origin FROM entities WHERE id='e0001'")[0]["origin"], "c")

    def test_a_rejected_operation_leaves_no_evidence_behind(self):
        before = self.store.query("SELECT COUNT(*) AS n FROM evidence")[0]["n"]
        # valid shape, valid evidence, but Aya does not hold the knife: rejected only after the evidence was stored, so it must be rolled back
        results = self.run_ops([{"op": "item_state", "item": "e0003", "holder": "e0002", "state": "lost", "evidence": [q("鉄の剣が落ちた")]}])
        self.assertEqual(results[0].status, "rejected")
        self.assertEqual(self.store.query("SELECT COUNT(*) AS n FROM evidence")[0]["n"], before)


class RollbackTests(unittest.TestCase):
    def counts(self, store):
        return {t: store.query(f"SELECT COUNT(*) AS n FROM {t}")[0]["n"] for t in ("entities", "names", "attributes", "events", "evidence", "holdings", "ops_log")}

    def test_rolling_back_the_story_pass_keeps_every_character_row(self):
        store = character_pass(Store())
        characters_only = self.counts(store)
        story_pass(store)
        self.assertNotEqual(self.counts(store), characters_only)
        store.rollback_story_from(chapter_key(1, 0))
        self.assertEqual(self.counts(store), characters_only)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM event_participants")[0]["n"], 6)   # the character pass's own participants stay

    def test_rolling_back_from_a_chapter_keeps_the_earlier_story_and_reopens_what_it_closed(self):
        store = saga()
        store.rollback_story_from(chapter_key(1, 2))
        iron, knife = holdings(store, "e0005"), holdings(store, "e0003")
        self.assertEqual([(h["holder_id"], h["to_key"], h["end_reason"]) for h in iron], [("e0001", None, "")])   # the transfer was a chapter-2 fact
        self.assertEqual(knife[0]["end_reason"], "replaced")                                                      # the replacement was chapter 1's
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities WHERE origin='s'")[0]["n"], 3)         # knife, goblin, iron sword
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM events WHERE origin='s'")[0]["n"], 3)

    def test_story_attribute_values_that_closed_character_values_are_reopened_correctly(self):
        store = character_pass(Store(), upto=(1, 0))
        # the character pass says Aya is "受付嬢"; a later story chapter supersedes the status of the same entity
        apply_ops(store, ChapterContext(1, 1, TEXTS[(1, 1)]), [
            {"op": "set_attribute", "entity": "e0002", "key": "status", "value": "受付中", "evidence": [q("森でゴブリンを二十匹倒した")]}]
            + loot_saga.CHARACTER_ANSWERS[(1, 1)]["ops"])
        apply_ops(store, story_ctx(1, 1), [{"op": "set_attribute", "entity": "e0002", "key": "status", "value": "ギルドで待機",
                                            "evidence": [q("鉄の剣が落ちた")]}])
        before = [(r["value"], r["to_key"] is not None) for r in store.query("SELECT value, to_key FROM attributes WHERE entity_id='e0002' AND key='status' ORDER BY from_key")]
        self.assertEqual(before, [("受付中", True), ("ギルドで待機", False)])
        store.rollback_story_from(chapter_key(1, 1))
        after = [(r["value"], r["to_key"]) for r in store.query("SELECT value, to_key FROM attributes WHERE entity_id='e0002' AND key='status'")]
        self.assertEqual(after, [("受付中", None)])

    def test_a_character_pass_rollback_takes_the_story_data_of_those_chapters_with_it(self):
        store = saga()
        store.set_story_state(1, 2, text_sha="x", status="built")
        store.rollback_from(chapter_key(1, 2))
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities WHERE origin='s' AND src_key >= ?", (chapter_key(1, 2),))[0]["n"], 0)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM holdings WHERE src_key >= ?", (chapter_key(1, 2),))[0]["n"], 0)
        self.assertIsNone(store.story_state(1, 2))
        self.assertEqual(holdings(store, "e0005")[0]["to_key"], None)

    def test_rolling_back_drops_derived_cards(self):
        store = saga()
        store.conn.execute("INSERT INTO story_event_cards VALUES ('v1', 1, 1, 0, '{}')")
        store.conn.execute("INSERT INTO story_item_cards VALUES ('e1', 1, 1, 0, '{}')")
        store.rollback_story_from(chapter_key(1, 3))
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM story_event_cards")[0]["n"], 0)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM story_item_cards")[0]["n"], 0)

    def test_untyped_events_are_replaced_only_when_the_story_pass_recorded_events(self):
        store = character_pass(Store())
        story_pass(store, upto=(1, 3))
        # chapter 0 has story events: its untyped event goes. Chapter 3 has none (the answer only changes items): it stays.
        self.assertEqual(store.replace_untyped_events(chapter_key(1, 0)), 1)
        self.assertEqual(store.replace_untyped_events(chapter_key(1, 3)), 0)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM events WHERE origin='c' AND src_key=?", (chapter_key(1, 3),))[0]["n"], 1)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM event_participants WHERE event_id='v0001'")[0]["n"], 0)


if __name__ == "__main__":
    unittest.main()
