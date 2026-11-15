import json
import unittest

from immersive_knowledge import profile as prof
from immersive_knowledge.gate import Gate
from immersive_knowledge.ops import ChapterContext, apply_ops
from immersive_knowledge.position import StoryPosition
from immersive_knowledge.scanner import SpoilerLeak, ensure_clean, hidden_names, scan
from immersive_knowledge.store import Store

from saga import ANSWERS, CANARY, SECRET_NAME, TEXTS, apply_saga, q


def mark_built(store):
    for (v, c) in sorted(TEXTS):
        store.set_chapter_state(v, c, text_sha="x", status="built")


def saga_store():
    store = apply_saga()
    mark_built(store)
    return store


class ScannerTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = saga_store()
        cls.gate = Gate(cls.store)

    def view(self, text):
        return self.gate.view(StoryPosition.parse(text))

    def test_names_that_are_not_visible_yet_are_hidden_names(self):
        self.assertEqual(set(hidden_names(self.store, self.view("1:2")).values()), {SECRET_NAME})
        self.assertEqual(hidden_names(self.store, self.view("1:3")), {})      # revealed: nothing left to hide

    def test_a_hidden_name_in_text_is_found_even_with_spaces_and_width_variants(self):
        view = self.view("1:2")
        self.assertEqual(scan(self.store, view, f"実は{SECRET_NAME}が犯人だ"), [SECRET_NAME])
        self.assertEqual(scan(self.store, view, "青木 先生は笑った"), [SECRET_NAME])
        self.assertEqual(scan(self.store, view, "ミナは仮面の男と話した"), [])

    def test_ensure_clean_raises_with_the_hits_and_returns_clean_text(self):
        view = self.view("1:2")
        with self.assertRaises(SpoilerLeak) as raised:
            ensure_clean(self.store, view, SECRET_NAME, "summary")
        self.assertEqual(raised.exception.hits, (SECRET_NAME,))
        self.assertEqual(ensure_clean(self.store, view, "ミナは森にいた", "summary"), "ミナは森にいた")

    def test_a_hidden_name_inside_a_visible_name_is_not_a_false_alarm(self):
        store = Store()
        with store.transaction():
            visible = store.add_entity("character", 1, 1)
            store.add_name(visible, "青木先生", "canonical", 1, 1, [], preferred=True)
            other = store.add_entity("character", 10 ** 12 * 5, 1)
            store.add_name(other, "先生", "alias", 10 ** 12 * 5, 1, [])        # hidden, and a substring of a visible name
        view = Gate(store).view(StoryPosition(1, 0))
        self.assertEqual(scan(store, view, "青木先生は笑った"), [])           # visible name masked first
        self.assertEqual(scan(store, view, "その先生は笑った"), ["先生"])      # the bare hidden word still counts

    def test_single_character_names_are_not_scanned(self):
        store = Store()
        with store.transaction():
            e = store.add_entity("character", 10 ** 12 * 5, 1)
            store.add_name(e, "月", "canonical", 10 ** 12 * 5, 1, [])
        self.assertEqual(hidden_names(store, Gate(store).view(StoryPosition(1, 0))), {})


class CanaryTests(unittest.TestCase):
    """The headline guarantee: nothing about the culprit before the reader has reached it."""

    @classmethod
    def setUpClass(cls):
        cls.store = saga_store()
        cls.gate = Gate(cls.store)

    def blocks(self, text):
        view = self.gate.view(StoryPosition.parse(text))
        result = []
        for rep in view.representatives():
            profile, block = prof.render_checked(self.store, view, rep)
            result.append((profile, block, prof.profile_text(profile, block)))
        return result

    def test_no_profile_before_the_reveal_contains_the_secret_or_the_canary(self):
        for position in ("1:1", "1:2"):
            for profile, block, text in self.blocks(position):
                with self.subTest(position=position, character=profile["name"]):
                    self.assertNotIn(CANARY, text)
                    self.assertNotIn(SECRET_NAME, text)

    def test_the_reveal_appears_exactly_when_the_reader_has_passed_it(self):
        texts = "\n".join(t for _, _, t in self.blocks("1:3"))
        self.assertIn(SECRET_NAME, texts)
        self.assertNotIn(CANARY, texts)        # the culprit fact is written in chapter 3: visible from 2:0
        later = "\n".join(t for _, _, t in self.blocks("2:0"))
        self.assertIn(CANARY, later)           # positive control: the canary check can fail, so passing means something

    def test_snapshot_rows_never_contain_a_name_that_is_not_visible(self):
        result = prof.generate_snapshots(self.store)
        self.assertEqual(result["blocked"], [])
        for row in self.store.query("SELECT volume, chapter, block_text, profile_json FROM profile_snapshots"):
            if (row["volume"], row["chapter"]) < (1, 3):
                self.assertNotIn(SECRET_NAME, row["block_text"] + row["profile_json"])
            if (row["volume"], row["chapter"]) <= (1, 3):      # the culprit fact is written in chapter 3: visible from 1:4 (volume finished)
                self.assertNotIn(CANARY, row["block_text"] + row["profile_json"])

    def test_free_text_that_names_a_hidden_person_blocks_the_snapshot(self):
        store = apply_saga(upto=(1, 0))
        # the model wrote a chapter-1 summary that already names the culprit: structurally valid, but a spoiler
        apply_ops(store, ChapterContext(1, 1, TEXTS[(1, 1)]), [
            {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "リク"}], "evidence": [q("ミナの友だちのリクは")]},
            {"op": "add_event", "summary": f"リクは{SECRET_NAME}を疑った", "participants": [{"entity": "n1"}], "evidence": [q("二人は固い友情を誓った")]}])
        apply_ops(store, ChapterContext(1, 2, TEXTS[(1, 2)]), ANSWERS[(1, 2)]["ops"][:1] and [
            {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": SECRET_NAME}], "evidence": [q("担任の青木先生だった")]}])
        mark_built(store)
        result = prof.generate_snapshots(store)
        self.assertTrue(result["blocked"], "the leak must be detected")
        self.assertTrue(all(SECRET_NAME in b["hits"] for b in result["blocked"]))
        for row in store.query("SELECT block_text, profile_json FROM profile_snapshots WHERE volume=1 AND chapter<=2"):
            self.assertNotIn(SECRET_NAME, row["block_text"] + row["profile_json"])

    def test_render_checked_raises_instead_of_returning_leaking_text(self):
        store = apply_saga(upto=(1, 0))
        apply_ops(store, ChapterContext(1, 1, TEXTS[(1, 1)]), [
            {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "リク"}], "evidence": [q("ミナの友だちのリクは")]},
            {"op": "add_event", "summary": f"{SECRET_NAME}の企み", "participants": [{"entity": "n1"}], "evidence": [q("二人は固い友情を誓った")]}])
        apply_ops(store, ChapterContext(1, 2, TEXTS[(1, 2)]), [
            {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": SECRET_NAME}], "evidence": [q("担任の青木先生だった")]}])
        view = Gate(store).view(StoryPosition(1, 2))
        riku = next(r for r in view.representatives() if view.display_name(r) == "リク")
        with self.assertRaises(SpoilerLeak):
            prof.render_checked(store, view, riku)


class ProfileTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = saga_store()
        cls.gate = Gate(cls.store)

    def profile(self, position, name):
        view = self.gate.view(StoryPosition.parse(position))
        rep = next(r for r in view.representatives() if name in {n["name"] for n in view.all_names(r)})
        return prof.build_profile(view, rep), view, rep

    def test_profile_content_at_a_chapter_boundary(self):
        profile, _, _ = self.profile("1:2", "ミナ")
        self.assertEqual(profile["name"], "ミナ")
        self.assertEqual(profile["traits"]["personality"], ["勇気がある"])
        self.assertEqual(profile["speech_samples"], ["私はミナです。友だちを探しています"])
        self.assertEqual([(r["type"], r["other"]) for r in profile["relationships"]], [("友だち", "リク")])
        self.assertEqual([t["summary"] for t in profile["timeline"]], ["ミナは森の入口で仮面の男に出会った", "ミナとリクは固い友情を誓った"])

    def test_ended_relationships_become_history(self):
        profile, _, _ = self.profile("2:0", "ミナ")
        self.assertEqual(profile["relationships"], [])
        self.assertEqual([(r["type"], r["other"]) for r in profile["former_relationships"]], [("友だち", "リク")])
        self.assertIn("former 友だち: リク (ended)", prof.render_block(profile))

    def test_identity_groups_share_one_profile_with_both_names(self):
        profile, view, rep = self.profile("1:3", SECRET_NAME)
        self.assertEqual(set(profile["names"]), {"仮面の男", SECRET_NAME})
        self.assertEqual(view.group_of["e0002"], view.group_of["e0004"])
        self.assertIn("この森には近づくな", profile["speech_samples"])       # the masked man's line now belongs to the teacher

    def test_the_rendered_block_labels_the_boundary_and_is_deterministic(self):
        profile, _, _ = self.profile("1:2", "ミナ")
        block = prof.render_block(profile)
        self.assertIn("volume 1, chapter 3", block)                          # chapters are 0-based inside, 1-based for people
        self.assertEqual(block, prof.render_block(profile))
        self.assertNotIn("{{", block)

    def test_voice_cues_carry_what_a_voice_design_needs(self):
        store = apply_saga(upto=(1, 0))
        apply_ops(store, ChapterContext(1, 1, TEXTS[(1, 1)]), [
            {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "リク"}], "evidence": [q("ミナの友だちのリクは")]},
            {"op": "set_attribute", "entity": "n1", "key": "age", "value": "十二歳", "evidence": [q("いつも明るく笑う少年だった")]},
            {"op": "set_attribute", "entity": "n1", "key": "gender", "value": "男の子", "evidence": [q("いつも明るく笑う少年だった")]}])
        view = Gate(store).view(StoryPosition(1, 2))
        rep = next(r for r in view.representatives() if view.display_name(r) == "リク")
        self.assertEqual(prof.build_profile(view, rep)["voice_cues"], {"age": "十二歳", "gender": "男の子"})


class SnapshotTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.store = saga_store()
        prof.generate_snapshots(cls.store)

    def test_boundaries_cover_every_built_chapter_and_the_end_of_each_volume(self):
        self.assertEqual([str(p) for p in prof.boundaries(self.store)], ["1:0", "1:1", "1:2", "1:3", "1:4", "2:0", "2:1", "2:2"])

    def test_the_character_list_follows_the_reader(self):
        early = {c["name"] for c in prof.list_characters(self.store, 1, 1)}
        late = {c["name"] for c in prof.list_characters(self.store, 1, 3)}
        self.assertEqual(early, {"ミナ", "仮面の男"})
        self.assertTrue(early < late | {"仮面の男", SECRET_NAME} and "リク" in late)
        self.assertNotIn(SECRET_NAME, " ".join(c["name"] for c in prof.list_characters(self.store, 1, 2)))
        self.assertEqual(prof.list_characters(self.store, 1, 0), [])

    def test_lookup_returns_the_stored_row_for_the_readers_chapter_and_works_with_any_member_id(self):
        masked = prof.get_profile(self.store, 1, 3, "e0002")
        teacher = prof.get_profile(self.store, 1, 3, "e0004")
        self.assertIsNotNone(masked)
        self.assertEqual(masked["character_id"], teacher["character_id"])
        self.assertIn(SECRET_NAME, masked["block"])
        # the same character one chapter earlier is still just the masked man, and the teacher id does not exist yet
        earlier = prof.get_profile(self.store, 1, 2, "e0002")
        self.assertNotIn(SECRET_NAME, earlier["block"])
        self.assertIsNone(prof.get_profile(self.store, 1, 2, "e0004"))

    def test_a_reader_beyond_what_is_built_gets_the_closest_earlier_boundary_never_a_later_one(self):
        far = prof.get_profile(self.store, 1, 99, "e0001")
        self.assertEqual(far["profile"]["as_of"], "1:4")
        # a reader in chapter 1 must never be served the chapter-2 snapshot
        self.assertEqual(prof.get_profile(self.store, 1, 1, "e0001")["profile"]["as_of"], "1:1")

    def test_a_character_without_a_snapshot_at_the_readers_chapter_gets_the_latest_earlier_one_never_a_later_one(self):
        store = saga_store()
        prof.generate_snapshots(store)
        # simulate "blocked by the scanner" for Mina at 1:3 (and keep her later snapshots to prove they are not used)
        store.conn.execute("DELETE FROM profile_snapshots WHERE character_id='e0001' AND volume=1 AND chapter=3")
        store.conn.execute("DELETE FROM profile_snapshot_members WHERE character_id='e0001' AND volume=1 AND chapter=3")
        store.conn.commit()
        found = prof.get_profile(store, 1, 3, "e0001")
        self.assertEqual(found["profile"]["as_of"], "1:2")
        self.assertNotEqual(prof.get_profile(store, 2, 0, "e0001")["profile"]["as_of"], "1:2")
        self.assertIsNone(prof.get_profile(store, 1, 0, "e0001"))      # nothing earlier exists: no profile, not a later one

    def test_generation_is_deterministic_and_idempotent(self):
        dump = lambda: [tuple(r) for r in self.store.query("SELECT * FROM profile_snapshots ORDER BY character_id, volume, chapter")]
        first = dump()
        prof.generate_snapshots(self.store)
        self.assertEqual(first, dump())

    def test_only_characters_above_the_prominence_threshold_get_snapshots(self):
        store = saga_store()
        prof.generate_snapshots(store, min_prominence=10 ** 6)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM profile_snapshots")[0]["n"], 0)

    def test_locations_do_not_get_character_snapshots(self):
        store = apply_saga(upto=(1, 0))
        apply_ops(store, ChapterContext(1, 1, TEXTS[(1, 1)]), [
            {"op": "add_entity", "ref": "n1", "kind": "location", "names": [{"name": "迷いの森"}], "evidence": [q("ミナの友だちのリクは")]},
            {"op": "add_event", "summary": "森で二人は出会った", "participants": [{"entity": "n1"}], "evidence": [q("二人は固い友情を誓った")]}])
        mark_built(store)
        prof.generate_snapshots(store)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM profile_snapshots WHERE display_name='迷いの森'")[0]["n"], 0)


if __name__ == "__main__":
    unittest.main()
