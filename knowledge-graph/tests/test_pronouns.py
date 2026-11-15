"""Pronouns are not names (spec 18, section on pronouns): a first-person story must not produce a character called わたし.

Found in a real build of a whole series: the narrator was recorded as the character "わたし" (2,392 event roles, 539 relations) next to a
separate protagonist, because the model "created" the pronoun again in every chapter and the name matched the old entity.
"""
import json
import re
import unittest

from immersive_knowledge.builder import Builder, BuildConfig, causal_snapshot, story_snapshot
from immersive_knowledge.gate import Gate
from immersive_knowledge.ops import ChapterContext, apply_ops
from immersive_knowledge.position import StoryPosition, as_of
from immersive_knowledge.pronouns import is_pronoun, pronoun_kind, pronoun_names
from immersive_knowledge.store import ORIGIN_CHARACTERS, Store

from saga import ANSWERS, TEXTS, FakeLlm, apply_saga, book_text, q

SNAPSHOT_BLOCK = re.compile(r"<<<SNAPSHOT_JSON\n(.*?)\nSNAPSHOT_JSON>>>", re.S)
CTX = ChapterContext(1, 0, TEXTS[(1, 0)])
NEXT = ChapterContext(1, 1, TEXTS[(1, 0)])        # a later chapter (the text is chapter 0's, so its quotes can be reused)
LATER = ChapterContext(1, 2, TEXTS[(1, 0)])


def run(store, ops, ctx=CTX):
    return apply_ops(store, ctx, ops)


def named(name, ref="n1", quote="私はミナです", kind="character", type_="canonical"):
    return {"op": "add_entity", "ref": ref, "kind": kind, "names": [{"name": name, "type": type_}], "evidence": [q(quote)]}


class RecognisingPronounsTests(unittest.TestCase):
    def test_personal_pronouns_of_all_three_persons(self):
        for name, kind in (("わたし", "first"), ("私", "first"), ("僕", "first"), ("俺", "first"), ("ワタシ", "first"), ("わたしたち", "first"),
                           ("あなた", "second"), ("お前", "second"), ("君", "second"), ("彼", "third"), ("彼女", "third"), ("あの人", "third")):
            self.assertEqual(pronoun_kind(name), kind, name)

    def test_spacing_and_width_do_not_hide_a_pronoun(self):
        self.assertTrue(is_pronoun(" わたし "))
        self.assertTrue(is_pronoun("わ た し"))
        self.assertTrue(is_pronoun("ﾜﾀｼ"))                      # half-width katakana folds to ワタシ

    def test_names_are_not_pronouns_even_when_they_contain_one(self):
        for name in ("ミナ", "キミコ", "私の友達", "彼女の母", "ユナ", "クマさん", "", None):
            self.assertFalse(is_pronoun(name), name)

    def test_operations_that_would_store_a_pronoun_are_found(self):
        self.assertEqual(pronoun_names(named("わたし")), ["わたし"])
        self.assertEqual(pronoun_names({"op": "add_name", "entity": "e0001", "name": "俺", "type": "alias"}), ["俺"])
        self.assertEqual(pronoun_names(named("ミナ")), [])
        self.assertEqual(pronoun_names(named("余", kind="item")), [])                                   # an item called 余 is not a person
        self.assertEqual(pronoun_names({"op": "add_name", "entity": "e0009", "name": "余"}, lambda e: "item"), [])
        self.assertEqual(pronoun_names("not an op"), [])


class RefusingPronounNamesTests(unittest.TestCase):
    def test_a_character_called_only_by_a_pronoun_is_not_recorded_and_the_reason_says_what_to_do(self):
        store = Store()
        results = run(store, [named("私"), {"op": "set_attribute", "entity": "n1", "key": "personality", "value": "勇気がある",
                                           "evidence": [q("ミナは勇気を出して")]}])
        self.assertEqual([r.status for r in results], ["rejected", "rejected"])
        self.assertIn("pronoun", results[0].reason)
        self.assertIn("narrator", results[0].reason)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 0)

    def test_a_pronoun_next_to_a_real_name_is_left_out_and_the_character_keeps_the_real_name(self):
        store = Store()
        [result] = run(store, [{"op": "add_entity", "ref": "n1", "kind": "character",
                                "names": [{"name": "ミナ", "type": "canonical"}, {"name": "私", "type": "alias"}], "evidence": [q("私はミナです")]}])
        self.assertEqual(result.status, "applied")
        self.assertEqual([r["name"] for r in store.query("SELECT name FROM names")], ["ミナ"])

    def test_a_pronoun_is_never_added_as_an_alias(self):
        store = apply_saga(upto=(1, 0))
        [result] = run(store, [{"op": "add_name", "entity": "e0001", "name": "私", "type": "alias", "evidence": [q("私はミナです")]}], NEXT)
        self.assertEqual(result.status, "rejected")
        self.assertIn("pronoun", result.reason)

    def test_a_pronoun_as_the_name_of_something_that_is_not_a_person_is_allowed(self):
        store = Store()
        [result] = run(store, [named("私", kind="item", quote="私はミナです")])
        self.assertEqual(result.status, "applied")

    def test_a_pronoun_character_of_an_earlier_build_is_no_longer_a_valid_target(self):
        store = Store()
        # what the old extraction left behind: the narrator recorded as "わたし"
        eid = store.add_entity("character", StoryPosition(1, 0, 3).key, 1000000000000, ORIGIN_CHARACTERS)
        store.add_name(eid, "わたし", "canonical", StoryPosition(1, 0, 3).key, 1000000000000, [], preferred=True, origin=ORIGIN_CHARACTERS)
        [result] = run(store, [{"op": "set_attribute", "entity": eid, "key": "personality", "value": "勇気がある", "evidence": [q("ミナは勇気を出して")]}],
                       ChapterContext(1, 1, TEXTS[(1, 0)] + "ミナは勇気を出して"))
        self.assertEqual(result.status, "rejected")
        self.assertIn("pronoun", result.reason)

    def test_the_pronoun_character_is_not_offered_to_the_model(self):
        store = Store()
        eid = store.add_entity("character", StoryPosition(1, 0, 3).key, 1000000000000, ORIGIN_CHARACTERS)
        store.add_name(eid, "わたし", "canonical", StoryPosition(1, 0, 3).key, 1000000000000, [], preferred=True, origin=ORIGIN_CHARACTERS)
        view = Gate(store).view(as_of(1, 1))
        self.assertEqual(json.loads(causal_snapshot(view, BuildConfig()))["entities"], [])
        self.assertEqual(json.loads(story_snapshot(view, BuildConfig()))["characters"], [])


class NarratorTests(unittest.TestCase):
    def narrate(self, store):
        return run(store, [{"op": "set_attribute", "entity": "e0001", "key": "narrator", "value": "ignored", "evidence": [q("私はミナです")]}], NEXT)

    def test_the_narrator_is_recorded_with_a_value_the_program_writes(self):
        store = apply_saga(upto=(1, 0))
        [result] = self.narrate(store)
        self.assertEqual(result.status, "applied")
        self.assertEqual(store.query("SELECT value FROM attributes WHERE key='narrator'")[0]["value"], "first-person narrator (volume 1, chapter 1)")

    def test_only_a_character_can_narrate(self):
        store = Store()
        run(store, [named("森", kind="location", quote="森の入口")])
        [result] = run(store, [{"op": "set_attribute", "entity": "e0001", "key": "narrator", "evidence": [q("森の入口")]}], NEXT)
        self.assertEqual(result.status, "rejected")

    def test_the_snapshot_names_the_narrator_after_the_chapter_that_set_it(self):
        store = apply_saga(upto=(1, 0))
        self.narrate(store)
        later = Gate(store).view(as_of(1, 2))
        self.assertEqual(json.loads(causal_snapshot(later, BuildConfig()))["narrator"], "e0001")
        self.assertEqual(json.loads(story_snapshot(later, BuildConfig()))["narrator"], "e0001")
        self.assertNotIn("narrator", json.dumps(json.loads(causal_snapshot(later, BuildConfig()))["entities"]))   # a hint, not an attribute value
        earlier = Gate(store).view(StoryPosition(1, 1, 0))
        self.assertNotIn("narrator", json.loads(causal_snapshot(earlier, BuildConfig())))

    def test_the_most_recent_narrator_wins_and_the_hint_follows_a_change_of_viewpoint(self):
        store = apply_saga(upto=(1, 0))
        run(store, [{"op": "set_attribute", "entity": "e0001", "key": "narrator", "evidence": [q("私はミナです")]}], NEXT)
        run(store, [{"op": "set_attribute", "entity": "e0002", "key": "narrator", "evidence": [q("仮面の男は低い声で")]}], LATER)
        self.assertEqual(json.loads(causal_snapshot(Gate(store).view(as_of(1, 3)), BuildConfig()))["narrator"], "e0002")
        self.assertEqual(store.narrator_before(as_of(1, 2).key), "e0001")

    def test_the_narrator_is_not_a_profile_trait(self):
        from immersive_knowledge.profile import TRAIT_ORDER
        self.assertNotIn("narrator", TRAIT_ORDER)


class BuilderRetryTests(unittest.TestCase):
    PRONOUN_ANSWER = {"ops": [named("私", quote="私はミナです")]}
    GOOD_ANSWER = {"ops": [named("ミナ", quote="少女ミナは森の入口で")]}

    def build(self, retry_answer):
        llm = FakeLlm(answers={}, fail={(1, 0): lambda attempt: self.PRONOUN_ANSWER if attempt == 1 else retry_answer})
        store = Store()
        report = Builder(store, llm, BuildConfig()).run(book_text())
        return store, llm, report

    def test_an_answer_that_names_the_narrator_by_a_pronoun_is_asked_again_with_the_reason(self):
        store, llm, report = self.build(self.GOOD_ANSWER)
        calls = [c for c in llm.calls if c["key"] == (1, 0)]
        self.assertEqual(len(calls), 2)
        self.assertNotIn("PREVIOUS ANSWER WAS REJECTED", calls[0]["user"])
        self.assertIn("PREVIOUS ANSWER WAS REJECTED", calls[1]["user"])
        self.assertIn("'私'", calls[1]["user"])
        self.assertEqual((report.pronoun_retries, report.failed), (1, None))
        self.assertEqual([r["name"] for r in store.query("SELECT name FROM names")], ["ミナ"])

    def test_a_retry_that_repeats_the_mistake_is_refused_operation_by_operation(self):
        store, llm, report = self.build(self.PRONOUN_ANSWER)
        self.assertEqual(len([c for c in llm.calls if c["key"] == (1, 0)]), 2)         # one retry only, never a loop
        self.assertEqual((report.chapters_built, report.failed, report.ops_rejected), (6, None, 1))
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 0)

    def test_a_clean_answer_costs_no_extra_request(self):
        llm = FakeLlm()
        report = Builder(Store(), llm, BuildConfig()).run(book_text())
        self.assertEqual((len(llm.calls), report.pronoun_retries), (6, 0))

    def test_the_prompts_tell_the_model_how_to_resolve_a_pronoun(self):
        llm = FakeLlm()
        Builder(Store(), llm, BuildConfig(story_pass=True)).run(book_text())
        system = llm.calls[0]["system"]
        self.assertIn("PRONOUNS ARE NOT NAMES", system)
        self.assertIn("narrator", system)
        story = [c for c in llm.calls if c["system"].startswith("This is the STORY PASS")][0]["system"]
        self.assertIn("PRONOUNS ARE NOT NAMES", story)


if __name__ == "__main__":
    unittest.main()
