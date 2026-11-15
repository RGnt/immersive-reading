"""The story pass end to end: builder, causality, gated cards and profiles, migration (spec 22)."""
import copy
import json
import re
import tempfile
import unittest
from pathlib import Path

from immersive_knowledge.builder import Builder, BuildConfig, story_snapshot
from immersive_knowledge.gate import Gate
from immersive_knowledge.llm import LlmError
from immersive_knowledge.position import StoryPosition, chapter_key
from immersive_knowledge.profile import generate_snapshots, get_profile
from immersive_knowledge.store import Store, _V2_COLUMNS
from immersive_knowledge.story import list_events, list_items, member_ids

import loot_saga
from loot_saga import StoryLlm, TEXTS

SNAPSHOT_BLOCK = re.compile(r"<<<SNAPSHOT_JSON\n(.*?)\nSNAPSHOT_JSON>>>", re.S)


def build(store=None, llm=None, book=None, **config):
    store, llm, events = store or Store(), llm or StoryLlm(), []
    config.setdefault("story_pass", True)
    report = Builder(store, llm, BuildConfig(**config), progress=events.append).run(book or loot_saga.book_text())
    return store, llm, report, events


def names(events):
    return [e["event"] for e in events]


def snapshot_of(call):
    return json.loads(SNAPSHOT_BLOCK.search(call["user"]).group(1))


class StoryBuilderTests(unittest.TestCase):
    def test_a_full_build_runs_the_story_pass_after_the_character_pass(self):
        store, llm, report, events = build()
        self.assertIsNone(report.failed)
        self.assertEqual((report.chapters_built, report.story_built), (5, 5))
        self.assertEqual((len(llm.calls), len(llm.story_calls)), (5, 5))
        order = names(events)
        self.assertLess(max(i for i, n in enumerate(order) if n == "chapter_done"), min(i for i, n in enumerate(order) if n == "story_start"))
        self.assertEqual(sum(r["ops_rejected"] for r in store.story_chapters()), 0)
        self.assertEqual(len(store.story_chapters()), 5)

    def test_the_story_pass_is_off_unless_asked_for(self):
        store, llm, report, _ = build(story_pass=False)
        self.assertEqual((len(llm.story_calls), report.story_built, len(store.story_chapters())), (0, 0, 0))

    def test_untyped_events_are_replaced_where_the_story_pass_recorded_events(self):
        store, _, _, events = build()
        left = [r["src_key"] for r in store.query("SELECT src_key FROM events WHERE origin='c'")]
        self.assertEqual(left, [chapter_key(1, 3)])                         # chapter 3 had no story event: its untyped one stays
        self.assertEqual(sum(e.get("replaced_events", 0) for e in events if e["event"] == "story_done"), 3)

    def test_a_second_run_does_nothing(self):
        store, llm, _, _ = build()
        _, llm2, report, events = build(store=store)
        self.assertEqual((len(llm2.calls), len(llm2.story_calls)), (0, 0))
        self.assertEqual((report.story_skipped, report.story_built), (5, 0))
        self.assertEqual(names(events).count("story_skipped"), 5)

    def test_the_story_pass_can_be_added_to_a_series_whose_characters_are_already_built(self):
        store, llm, _, _ = build(story_pass=False)
        before = {t: store.query(f"SELECT COUNT(*) AS n FROM {t} WHERE origin='c'")[0]["n"] for t in ("entities", "names", "attributes", "events")}
        _, llm2, report, _ = build(store=store)
        self.assertEqual((len(llm2.calls), len(llm2.story_calls), report.chapters_skipped, report.story_built), (0, 5, 5, 5))
        after = {t: store.query(f"SELECT COUNT(*) AS n FROM {t} WHERE origin='c'")[0]["n"] for t in ("entities", "names", "attributes", "events")}
        self.assertEqual(after["entities"], before["entities"])
        self.assertEqual(after["events"], before["events"] - 3)             # the three replaced untyped events; nothing else of the characters changed
        self.assertEqual(after["names"], before["names"])

    def test_a_changed_chapter_redoes_the_character_pass_and_the_story_pass_from_there(self):
        store, _, _, _ = build()
        changed = loot_saga.book_text()
        chapter = changed["volumes"][1]["chapters"][0]
        chapter["text"] += "彼は盾のことを忘れなかった。\n"
        _, llm, report, _ = build(store=store, book=changed)
        self.assertEqual([c["key"] for c in llm.calls], [(2, 0)])
        self.assertEqual([c["key"] for c in llm.story_calls], [(2, 0)])
        self.assertEqual((report.chapters_built, report.story_built, report.story_skipped), (1, 1, 4))
        shield = store.query("SELECT to_key FROM holdings WHERE item_id='e0009'")[0]
        self.assertIsNotNone(shield["to_key"])                                # the loss was recorded again

    def test_a_failing_story_chapter_stops_the_story_pass_but_not_the_characters(self):
        llm = StoryLlm(story_fail={(1, 2): LlmError("boom", fatal=False)})
        store, llm, report, events = build(llm=llm)
        self.assertEqual(report.failed["pass"], "story")
        self.assertEqual((report.failed["volume"], report.failed["chapter"]), (1, 2))
        self.assertEqual(report.chapters_built, 5)                            # every character chapter is built
        self.assertEqual([r["status"] for r in store.story_chapters()], ["built", "built", "failed"])
        self.assertEqual([c["key"] for c in llm.story_calls], [(1, 0), (1, 1), (1, 2)])   # chapters after the failure were not tried
        self.assertIn("story_failed", names(events))
        # fixing the cause resumes at the failed chapter only
        _, healthy, report2, _ = build(store=store)
        self.assertIsNone(report2.failed)
        self.assertEqual([c["key"] for c in healthy.story_calls], [(1, 2), (1, 3), (2, 0)])
        self.assertEqual(len(healthy.calls), 0)

    def test_a_failing_character_chapter_means_no_story_pass_at_all(self):
        llm = StoryLlm(fail={(1, 1): LlmError("boom", fatal=False)})
        store, llm, report, _ = build(llm=llm)
        self.assertNotIn("pass", report.failed)
        self.assertEqual(len(llm.story_calls), 0)


class StoryCausalityTests(unittest.TestCase):
    def setUp(self):
        self.store, self.llm, _, _ = build()
        self.calls = {c["key"]: c for c in self.llm.story_calls}

    def test_a_story_request_holds_only_its_own_chapter_and_what_came_before(self):
        user = self.calls[(1, 1)]["user"]
        self.assertIn(TEXTS[(1, 1)].strip(), user)
        for later in ((1, 2), (1, 3), (2, 0)):
            self.assertNotIn(TEXTS[later].splitlines()[0], user)
        self.assertNotIn("炎の剣", user)                                        # an item of chapter 2 does not exist yet
        self.assertNotIn("黒い指輪", user)

    def test_the_story_snapshot_has_the_characters_found_in_this_chapter_and_the_items_and_holders_before_it(self):
        first = snapshot_of(self.calls[(1, 0)])
        self.assertEqual([c["id"] for c in first["characters"]], ["e0001", "e0002"])     # found by the character pass of this very chapter
        self.assertEqual(first["items"], [])
        second = snapshot_of(self.calls[(1, 1)])
        self.assertEqual(second["items"], [{"id": "e0003", "names": ["古いナイフ"], "held_by": "e0001", "state": "equipped", "slot": "weapon"}])
        self.assertEqual([e["kind"] for e in second["recent_events"]], ["quest"])        # typed events only, never the untyped ones
        third = snapshot_of(self.calls[(1, 2)])
        held = {i["id"]: i for i in third["items"]}
        self.assertEqual(held["e0005"]["held_by"], "e0001")
        self.assertNotIn("held_by", held["e0003"])                                       # replaced: nobody holds the knife any more

    def test_the_story_prompt_is_recognisable_and_complete(self):
        system = self.calls[(1, 0)]["system"]
        self.assertIn("STORY PASS", system)
        self.assertNotIn("{{", system + self.calls[(1, 0)]["user"])

    def test_the_snapshot_levels_shrink_the_story_snapshot(self):
        view = Gate(self.store).view(StoryPosition(1, 4))
        sizes = [len(story_snapshot(view, BuildConfig(), level)) for level in range(3)]
        self.assertGreaterEqual(sizes[0], sizes[1])
        self.assertGreaterEqual(sizes[1], sizes[2])
        capped = json.loads(story_snapshot(view, BuildConfig(story_snapshot_items=2, story_snapshot_events=1)))
        self.assertEqual((len(capped["items"]), len(capped["recent_events"])), (2, 1))

    def test_prompt_too_big_shrinks_only_the_story_snapshot_and_the_limit_is_shared(self):
        error = LlmError("HTTP 400: prompt is too long: 8641 tokens > 8241 maximum", truncated=True)
        error.prompt_too_big, error.context_limit = True, 8241

        class Picky(StoryLlm):
            def chat_json(inner, system, user, **options):
                if "STORY PASS" in system and len(snapshot_of({"user": user})["characters"]) > 100 and not inner.refused:
                    inner.refused = True
                    raise error
                return super().chat_json(system, user, **options)

        picky = Picky()
        picky.refused = False
        store = Store()
        for i in range(150):                       # a crowded cast makes level 0 differ from level 1
            e = store.add_entity("character", 10 + i, 1)
            store.add_name(e, f"人物{i}", "canonical", 10 + i, 1, [], preferred=True)
        store.set_chapter_state(1, 0, text_sha="x", status="built")
        book = {"volumes": [{"volume": 1, "chapters": [{"index": 0, "title": "t", "text": TEXTS[(1, 0)]}]}]}
        builder = Builder(store, picky, BuildConfig(story_pass=True), progress=lambda e: None)
        events = []
        builder.progress = events.append
        builder.run(book)
        shrunk = [e for e in events if e["event"] == "snapshot_shrunk"]
        self.assertEqual([e.get("pass") for e in shrunk], ["story"])
        self.assertEqual((builder.story_snapshot_level, builder.snapshot_level, builder.context_tokens), (1, 0, 8241))


class StoryCardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store, _, _, _ = build()
        cls.result = generate_snapshots(cls.store)

    def test_every_card_was_stored_and_none_was_blocked(self):
        self.assertEqual(self.result["story_blocked"], [])
        self.assertEqual(self.result["blocked"], [])
        self.assertGreater(self.result["events"], 5)
        self.assertGreater(self.result["items"], 4)

    def titles(self, volume, chapter, **filters):
        return [e["title"] for e in list_events(self.store, volume, chapter, **filters)["events"]]

    def test_events_become_visible_only_when_their_chapter_is_behind_the_reader(self):
        self.assertEqual(self.titles(1, 0), [])
        self.assertEqual(self.titles(1, 1), ["ゴブリン討伐の依頼"])
        self.assertEqual(self.titles(1, 2), ["鉄の剣が落ちた", "ゴブリン狩り", "ゴブリン討伐の依頼"])          # newest first
        self.assertEqual(len(self.titles(1, 3)), 6)
        # chapter 3 has no story event, so its untyped one is kept; it has no title and is shown as "other"
        self.assertEqual(len(self.titles(1, 4)), 7)
        untyped = [e for e in list_events(self.store, 1, 4)["events"] if not e["typed"]]
        self.assertEqual([(e["kind"], e["title"]) for e in untyped], [("other", "")])

    def test_an_event_card_links_characters_monsters_items_the_place_and_the_parent(self):
        events = {e["title"]: e for e in list_events(self.store, 1, 3)["events"]}
        dungeon, battle, drop = events["古い迷宮の攻略"], events["竜との戦い"], events["竜の宝箱"]
        self.assertEqual(battle["parent"], dungeon["id"])
        self.assertEqual(drop["parent"], dungeon["id"])
        self.assertEqual(events["ゴブリン狩り"]["parent"], events["ゴブリン討伐の依頼"]["id"])
        self.assertEqual(battle["location"]["name"], "古い迷宮")
        by_kind = {(p["kind"], p["name"]) for p in battle["participants"]}
        self.assertEqual(by_kind, {("character", "レン"), ("character", "アヤ"), ("creature", "竜")})
        self.assertEqual({(p["name"], p["role"]) for p in drop["participants"]}, {("炎の剣", "dropped"), ("竜の盾", "dropped")})
        self.assertEqual(battle["kind"], "battle")

    def test_events_can_be_filtered_by_character_and_by_kind(self):
        self.assertEqual(self.titles(1, 3, character_id="e0002"), ["古い迷宮の攻略", "竜との戦い", "ゴブリン討伐の依頼"])
        self.assertEqual(self.titles(1, 3, kind="item"), ["竜の宝箱", "鉄の剣が落ちた"])
        self.assertEqual(self.titles(1, 3, kind="hunt", character_id="e0002"), [])
        result = list_events(self.store, 1, 3, limit=2)
        self.assertEqual((result["total"], len(result["events"])), (6, 2))

    def test_the_cards_never_name_an_item_the_reader_has_not_met(self):
        for chapter in range(0, 4):
            text = json.dumps(list_events(self.store, 1, chapter)["events"] + list_items(self.store, 1, chapter), ensure_ascii=False)
            self.assertNotIn("黒い指輪", text, chapter)
        self.assertIn("黒い指輪", json.dumps(list_items(self.store, 1, 4), ensure_ascii=False))

    def test_items_follow_their_holders_and_keep_their_history(self):
        items = {i["name"]: i for i in list_items(self.store, 1, 3)}
        self.assertEqual(set(items), {"古いナイフ", "鉄の剣", "炎の剣", "竜の盾"})
        iron = items["鉄の剣"]
        self.assertEqual((iron["holder"]["name"], iron["state"]), ("アヤ", "owned"))
        self.assertEqual([(h["holder"]["name"], h["state"], h["reason"]) for h in iron["history"]],
                         [("レン", "equipped", "replaced"), ("アヤ", "owned", "")])
        self.assertEqual(iron["history"][1]["to"], None)
        self.assertIsNone(items["古いナイフ"]["holder"])
        self.assertEqual(items["古いナイフ"]["description"], {"appearance": ["古びたナイフ"]})
        before = {i["name"]: i for i in list_items(self.store, 1, 2)}
        self.assertEqual((before["鉄の剣"]["holder"]["name"], before["鉄の剣"]["slot"]), ("レン", "weapon"))
        self.assertEqual(set(before), {"古いナイフ", "鉄の剣"})

    def test_items_can_be_listed_by_holder(self):
        self.assertEqual([i["name"] for i in list_items(self.store, 1, 3, holder_id="e0001")], ["炎の剣", "竜の盾"])
        self.assertEqual([i["name"] for i in list_items(self.store, 1, 3, holder_id="e0002")], ["鉄の剣"])
        self.assertEqual([i["name"] for i in list_items(self.store, 1, 4, holder_id="e0001")], ["炎の剣", "竜の盾", "黒い指輪"])
        self.assertEqual([i["name"] for i in list_items(self.store, 1, 4, holder_id="e0002")], [])

    def test_a_card_is_stored_once_per_change_not_once_per_chapter(self):
        rows = self.store.query("SELECT volume, chapter FROM story_item_cards WHERE item_id='e0005' ORDER BY volume, chapter")
        self.assertEqual([(r["volume"], r["chapter"]) for r in rows], [(1, 2), (1, 3), (1, 4)])     # created, handed over, sold
        # the quest never changes after it is first shown: one row for it
        quest = self.store.query("SELECT COUNT(*) AS n FROM story_event_cards WHERE event_id=(SELECT id FROM events WHERE kind='quest')")[0]["n"]
        self.assertEqual(quest, 1)

    def test_a_reader_beyond_the_built_chapters_sees_the_latest_older_card_never_a_newer_one(self):
        items = {i["name"]: i for i in list_items(self.store, 1, 99)}
        self.assertIn("黒い指輪", items)                    # everything built is behind a reader at chapter 99
        self.assertEqual({i["name"] for i in list_items(self.store, 0, 0)}, set())

    def test_the_shield_is_lost_in_the_next_volume(self):
        reading_volume_two = {i["name"]: i for i in list_items(self.store, 2, 0)}
        self.assertEqual(reading_volume_two["竜の盾"]["holder"]["name"], "レン")
        afterwards = {i["name"]: i for i in list_items(self.store, 2, 1)}
        self.assertIsNone(afterwards["竜の盾"]["holder"])
        self.assertEqual(afterwards["竜の盾"]["history"][0]["reason"], "lost")

    def test_member_ids_fall_back_to_the_id_itself_without_a_snapshot(self):
        self.assertEqual(member_ids(self.store, 0, 0, "e0001"), {"e0001"})
        self.assertEqual(member_ids(self.store, 1, 3, "e0001"), {"e0001"})


class ReplacementTagTests(unittest.TestCase):
    """The graph tags: the newer item "replaces" the older one, the older one is "replaced by" the newer one."""

    @classmethod
    def setUpClass(cls):
        cls.store, _, _, _ = build()
        generate_snapshots(cls.store)

    def items(self, volume, chapter):
        return {i["name"]: i for i in list_items(self.store, volume, chapter)}

    def test_the_new_item_replaces_the_old_one_and_the_old_one_is_replaced_by_the_new_one(self):
        items = self.items(1, 4)
        self.assertEqual(items["鉄の剣"]["history"][0]["replaces"]["name"], "古いナイフ")
        self.assertEqual(items["鉄の剣"]["history"][0]["replaced_by"]["name"], "炎の剣")
        self.assertEqual(items["炎の剣"]["replaces"]["name"], "鉄の剣")
        self.assertEqual(items["古いナイフ"]["replaced_by"]["name"], "鉄の剣")
        self.assertIsNone(items["炎の剣"]["replaced_by"])               # still the weapon
        self.assertIsNone(items["古いナイフ"]["replaces"])

    def test_an_item_that_is_still_in_use_does_not_reveal_what_will_replace_it(self):
        # before the dungeon the iron sword is Ren's weapon; that the flame sword takes its place is a later fact
        items = self.items(1, 2)
        self.assertEqual(items["鉄の剣"]["replaces"]["name"], "古いナイフ")
        self.assertIsNone(items["鉄の剣"]["replaced_by"])
        self.assertNotIn("炎の剣", json.dumps(items["鉄の剣"], ensure_ascii=False))
        self.assertEqual(items["古いナイフ"]["replaced_by"]["name"], "鉄の剣")

    def test_giving_an_item_away_is_not_a_replacement(self):
        # the iron sword went to Aya as an owned item: no slot was taken over
        aya = self.items(1, 4)["鉄の剣"]["history"][1]
        self.assertEqual((aya["replaces"], aya["replaced_by"]), (None, None))

    def test_the_tags_never_name_an_item_the_reader_has_not_met(self):
        for chapter in range(0, 3):                      # the flame sword is found in chapter 3 of the book (index 2)
            seen = json.dumps(list_items(self.store, 1, chapter) + list_events(self.store, 1, chapter)["events"], ensure_ascii=False)
            self.assertNotIn("炎の剣", seen, chapter)


class ProfileGearTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store, _, _, _ = build()
        generate_snapshots(cls.store)

    def profile(self, volume, chapter, character):
        return get_profile(self.store, volume, chapter, character)["profile"]

    def test_newer_gear_overwrites_older_gear_in_the_profile(self):
        after_first = self.profile(1, 2, "e0001")
        self.assertEqual(after_first["equipment"], [{"slot": "weapon", "item": "鉄の剣", "replaces": "古いナイフ"}])
        self.assertEqual([(g["item"], g["reason"], g["replaced_by"]) for g in after_first["former_gear"]], [("古いナイフ", "replaced", "鉄の剣")])
        after_dungeon = self.profile(1, 3, "e0001")
        self.assertEqual(after_dungeon["equipment"], [{"slot": "weapon", "item": "炎の剣", "replaces": "鉄の剣"},
                                                      {"slot": "shield", "item": "竜の盾", "replaces": None}])
        self.assertEqual([g["item"] for g in after_dungeon["former_gear"]], ["古いナイフ", "鉄の剣"])
        self.assertEqual(self.profile(1, 4, "e0001")["equipment"][-1], {"slot": "accessory", "item": "黒い指輪", "replaces": None})

    def test_carried_items_are_belongings_and_disappear_when_lost(self):
        self.assertEqual(self.profile(1, 3, "e0002")["belongings"], ["鉄の剣"])
        self.assertEqual(self.profile(1, 4, "e0002")["belongings"], [])

    def test_lost_gear_leaves_the_equipment_in_the_next_volume(self):
        self.assertIn("竜の盾", [e["item"] for e in self.profile(2, 0, "e0001")["equipment"]])
        self.assertNotIn("竜の盾", [e["item"] for e in self.profile(2, 1, "e0001")["equipment"]])

    def test_the_prompt_block_lists_equipment_and_belongings(self):
        block = get_profile(self.store, 1, 3, "e0001")["block"]
        self.assertIn("equipped (weapon): 炎の剣 (replaces 鉄の剣)", block)
        self.assertIn("formerly equipped (weapon): 鉄の剣 (replaced by 炎の剣)", block)
        self.assertIn("formerly equipped (weapon): 古いナイフ (replaced by 鉄の剣)", block)
        self.assertIn("carrying: 鉄の剣", get_profile(self.store, 1, 3, "e0002")["block"])
        self.assertNotIn("黒い指輪", get_profile(self.store, 1, 3, "e0001")["block"])

    def test_the_timeline_carries_the_event_kinds(self):
        kinds = [t["kind"] for t in self.profile(1, 3, "e0001")["timeline"]]
        self.assertEqual(kinds, ["quest", "hunt", "item", "battle", "dungeon"])        # in the order the text shows them


class SpoilerLeakTests(unittest.TestCase):
    def test_a_card_that_names_something_not_yet_met_is_held_back_until_it_is_safe(self):
        answers = copy.deepcopy(loot_saga.STORY_ANSWERS)
        answers[(1, 0)]["ops"][1]["summary"] = "レンは黒い指輪の依頼を知らされた"      # the ring is only found in chapter 3
        store, _, report, _ = build(llm=StoryLlm(story=answers))
        self.assertIsNone(report.failed)
        result = generate_snapshots(store)
        self.assertTrue(result["story_blocked"])
        self.assertEqual(result["story_blocked"][0]["hits"], ["黒い指輪"])
        for chapter in (1, 2, 3):
            self.assertNotIn("依頼", " ".join(e["title"] for e in list_events(store, 1, chapter)["events"]))
        self.assertIn("ゴブリン討伐の依頼", [e["title"] for e in list_events(store, 1, 4)["events"]])     # shown once it is safe

    def test_a_word_the_reader_already_read_is_not_a_secret(self):
        # "ゴブリン" is in the title of the quest of chapter 0 and only becomes an entity in chapter 1
        store, _, _, _ = build()
        self.assertEqual(generate_snapshots(store)["story_blocked"], [])


class MigrationTests(unittest.TestCase):
    def downgrade(self, path):
        store = Store(path)
        store.conn.execute("INSERT INTO entities(id, kind, first_key, src_key) VALUES ('e0001','character',5,5)")
        store.conn.execute("INSERT INTO names(entity_id, name, type, from_key, src_key, preferred) VALUES ('e0001','ミナ','canonical',5,5,1)")
        store.conn.execute("INSERT INTO events(id, summary, from_key, src_key) VALUES ('v0001','ミナは歩いた',6,5)")
        store.conn.commit()
        for table, column, _ in reversed(_V2_COLUMNS):
            store.conn.execute(f"ALTER TABLE {table} DROP COLUMN {column}")
        for table in ("holdings", "story_chapters", "story_event_cards", "story_item_cards"):
            store.conn.execute(f"DROP TABLE {table}")
        store.conn.execute("UPDATE meta SET value='1' WHERE key='schema_version'")
        store.conn.commit()
        store.close()

    def test_a_version_1_database_is_migrated_in_place_and_keeps_its_rows(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "knowledge.db"
            self.downgrade(path)
            store = Store(path)
            self.assertEqual(store.get_meta("schema_version"), "2")
            event = store.query("SELECT summary, kind, title, parent, origin FROM events")[0]
            self.assertEqual((event["summary"], event["kind"], event["title"], event["parent"], event["origin"]), ("ミナは歩いた", "", "", None, "c"))
            self.assertEqual(store.query("SELECT origin FROM entities")[0]["origin"], "c")
            self.assertEqual(store.query("SELECT COUNT(*) AS n FROM holdings")[0]["n"], 0)
            store.close()
            Store(path).close()                                        # opening it again is harmless

    def test_a_migrated_database_accepts_the_story_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "knowledge.db"
            store, _, _, _ = build(store=Store(path), story_pass=False)
            store.close()
            self.downgrade_loaded(path)
            store, llm, report, _ = build(store=Store(path))
            self.assertIsNone(report.failed)
            self.assertEqual(len(llm.story_calls), 5)
            store.close()

    def downgrade_loaded(self, path):
        store = Store(path)
        for table, column, _ in reversed(_V2_COLUMNS):
            store.conn.execute(f"ALTER TABLE {table} DROP COLUMN {column}")
        for table in ("holdings", "story_chapters", "story_event_cards", "story_item_cards"):
            store.conn.execute(f"DROP TABLE {table}")
        store.conn.execute("UPDATE meta SET value='1' WHERE key='schema_version'")
        store.conn.commit()
        store.close()

    def test_an_unknown_schema_version_is_refused(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "knowledge.db"
            store = Store(path)
            store.conn.execute("UPDATE meta SET value='99' WHERE key='schema_version'")
            store.conn.commit()
            store.close()
            with self.assertRaises(ValueError):
                Store(path)


if __name__ == "__main__":
    unittest.main()
