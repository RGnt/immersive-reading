import json
import unittest

from immersive_knowledge.ops import ChapterContext, apply_ops
from immersive_knowledge.position import StoryPosition
from immersive_knowledge.store import Store

from saga import ANSWERS, TEXTS, apply_saga, q

CTX = ChapterContext(1, 0, TEXTS[(1, 0)])


def run(store, ops, ctx=CTX):
    return apply_ops(store, ctx, ops)


def entity(ref="n1", name="ミナ", quote="少女ミナは森の入口で"):
    return {"op": "add_entity", "ref": ref, "kind": "character", "names": [{"name": name, "type": "canonical"}], "evidence": [q(quote)]}


class ApplyTests(unittest.TestCase):
    def test_a_valid_answer_is_applied_and_logged(self):
        store = Store()
        results = run(store, ANSWERS[(1, 0)]["ops"])
        self.assertEqual([r.status for r in results], ["applied"] * 6)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM ops_log WHERE status='applied'")[0]["n"], 6)
        self.assertEqual([r["id"] for r in store.query("SELECT id FROM entities ORDER BY id")], ["e0001", "e0002"])

    def test_position_is_computed_from_the_evidence_not_from_the_model(self):
        store = Store()
        run(store, [entity()])
        quote = "少女ミナは森の入口で"
        end = TEXTS[(1, 0)].index(quote) + len(quote)
        self.assertEqual(store.query("SELECT first_key FROM entities")[0]["first_key"], StoryPosition(1, 0, end).key)

    def test_a_fact_is_as_late_as_its_latest_quote(self):
        store = Store()
        ctx = ChapterContext(1, 0, TEXTS[(1, 0)])
        run(store, [entity(), {"op": "set_attribute", "entity": "n1", "key": "personality", "value": "勇敢で、礼儀正しい",
                               "evidence": [q("ミナは勇気を出して"), q("私はミナです。友だちを探しています")]}], ctx)
        latest = TEXTS[(1, 0)].index("私はミナです。友だちを探しています") + len("私はミナです。友だちを探しています")
        row = store.query("SELECT from_key FROM attributes")[0]
        self.assertEqual(row["from_key"], StoryPosition(1, 0, latest).key)

    def test_an_invented_quote_rejects_the_operation_and_creates_nothing(self):
        store = Store()
        [result] = run(store, [entity(quote="この文章は本のどこにもありません")])
        self.assertEqual(result.status, "rejected")
        self.assertIn("evidence not found", result.reason)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 0)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM evidence")[0]["n"], 0)

    def test_back_dated_evidence_from_another_chapter_is_rejected(self):
        store = Store()
        # the quote is real, but it belongs to chapter 3: the model was never shown it in chapter 0
        [result] = run(store, [entity(ref="n1", name="青木先生", quote="青木先生こそが事件の真犯人だった")])
        self.assertEqual(result.status, "rejected")

    def test_operations_without_evidence_are_rejected(self):
        store = Store()
        results = run(store, [entity(), {"op": "set_attribute", "entity": "n1", "key": "personality", "value": "静か"}])
        self.assertEqual(results[1].status, "rejected")
        self.assertIn("no evidence", results[1].reason)

    def test_unknown_entities_are_rejected(self):
        store = Store()
        [result] = run(store, [{"op": "add_event", "summary": "何かが起きた", "participants": [{"entity": "e9999"}], "evidence": [q("仮面の男に出会った")]}])
        self.assertEqual(result.status, "rejected")
        self.assertIn("unknown entity", result.reason)

    def test_an_entity_the_model_was_not_shown_cannot_be_referenced(self):
        store = Store()
        apply_saga(store, upto=(1, 2))      # e0004 (青木先生) now exists, created in chapter 2
        later = ChapterContext(1, 0, TEXTS[(1, 0)])   # pretend a model is working on chapter 0 again
        [result] = run(store, [{"op": "set_attribute", "entity": "e0004", "key": "personality", "value": "厳しい", "evidence": [q("ミナは勇気を出して")]}], later)
        self.assertEqual(result.status, "rejected")
        self.assertIn("unknown entity", result.reason)

    def test_local_refs_work_only_within_one_answer_and_must_be_unique(self):
        store = Store()
        results = run(store, [entity("n1"), entity("n1", name="仮面の男", quote="仮面の男に出会った")])
        self.assertEqual([r.status for r in results], ["applied", "rejected"])

    def test_duplicate_entities_are_rejected_with_the_existing_id(self):
        store = Store()
        run(store, [entity()])
        [result] = run(store, [entity("n9", name="ミナ", quote="ミナは勇気を出して")])
        self.assertEqual(result.status, "rejected")
        self.assertIn("e0001", result.reason)

    def test_schema_violations_are_rejected_not_repaired(self):
        store = Store()
        bad = [
            "not an object", {"op": "teleport"}, {"nope": 1},
            {"op": "add_entity", "ref": "n1", "kind": "dragon", "names": [{"name": "ミナ"}], "evidence": [q("少女ミナは森の入口で")]},
            {"op": "add_entity", "ref": "n1", "kind": "character", "names": [], "evidence": [q("少女ミナは森の入口で")]},
            {"op": "set_attribute", "entity": "e0001", "key": "favourite_food", "value": "x", "evidence": [q("少女ミナは森の入口で")]},
        ]
        results = run(store, bad)
        self.assertTrue(all(r.status == "rejected" for r in results))
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 0)

    def test_speech_samples_must_be_verbatim_chapter_text(self):
        store = Store()
        results = run(store, [entity(), {"op": "set_attribute", "entity": "n1", "key": "speech_sample", "value": "本にはない台詞です"}])
        self.assertEqual(results[1].status, "rejected")
        self.assertIn("verbatim", results[1].reason)

    def test_single_valued_attributes_supersede_but_multi_valued_accumulate(self):
        store = Store()
        run(store, [entity(),
                    {"op": "set_attribute", "entity": "n1", "key": "status", "value": "元気", "evidence": [q("ミナは勇気を出して")]},
                    {"op": "set_attribute", "entity": "n1", "key": "status", "value": "疲れている", "evidence": [q("友だちを探しています")]}])
        rows = store.query("SELECT value, to_key FROM attributes WHERE key='status' ORDER BY from_key")
        self.assertEqual([r["value"] for r in rows], ["元気", "疲れている"])
        self.assertIsNotNone(rows[0]["to_key"])
        self.assertIsNone(rows[1]["to_key"])

    def test_the_same_claim_twice_is_a_noop(self):
        store = Store()
        op = {"op": "set_attribute", "entity": "n1", "key": "personality", "value": "勇気がある", "evidence": [q("ミナは勇気を出して")]}
        results = run(store, [entity(), op, dict(op)])
        self.assertEqual([r.status for r in results], ["applied", "applied", "noop"])

    def test_an_earlier_claim_found_later_is_valid_only_until_the_existing_one_starts(self):
        store = Store()
        run(store, [entity(),
                    {"op": "set_attribute", "entity": "n1", "key": "status", "value": "後の状態", "evidence": [q("友だちを探しています")]},
                    {"op": "set_attribute", "entity": "n1", "key": "status", "value": "先の状態", "evidence": [q("ミナは勇気を出して")]}])
        rows = {r["value"]: r for r in store.query("SELECT value, from_key, to_key FROM attributes WHERE key='status'")}
        self.assertEqual(rows["先の状態"]["to_key"], rows["後の状態"]["from_key"])
        self.assertIsNone(rows["後の状態"]["to_key"])

    def test_relations_dedupe_and_end_only_once(self):
        store = Store()
        apply_saga(store, upto=(1, 1))
        ctx = ChapterContext(1, 2, TEXTS[(1, 2)])
        again = {"op": "add_relation", "src": "e0001", "dst": "e0003", "type": "友だち", "evidence": [q("ミナは「先生が仮面の男だったなんて」")]}
        self.assertEqual(run(store, [again], ctx)[0].status, "noop")
        end = {"op": "end_relation", "relation": "r0001", "evidence": [q("ミナは「先生が仮面の男だったなんて」と叫んだ")]}
        self.assertEqual([r.status for r in run(store, [end, end], ctx)], ["applied", "rejected"])

    def test_over_limit_operations_are_rejected(self):
        store = Store()
        results = apply_ops(store, CTX, [entity("n1"), entity("n2", name="仮面の男", quote="仮面の男に出会った")], max_ops=1)
        self.assertEqual([r.status for r in results], ["applied", "rejected"])

    def test_identity_links_are_recorded_not_merged(self):
        store = apply_saga(store=Store(), upto=(1, 2))
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 4)
        self.assertEqual(len(store.query("SELECT * FROM identity_links")), 1)

    def test_rejected_operations_are_audited_with_their_json(self):
        store = Store()
        run(store, [{"op": "add_entity", "ref": "n1"}])
        row = store.query("SELECT op_json, status, reason FROM ops_log")[0]
        self.assertEqual(row["status"], "rejected")
        self.assertEqual(json.loads(row["op_json"])["ref"], "n1")
        self.assertTrue(row["reason"])


class EvidenceStrictnessTests(unittest.TestCase):
    """Behaviour found with the real model (spike K0): verified, but never lenient where it matters."""

    def test_an_op_with_one_verified_quote_is_kept_but_positioned_at_the_end_of_the_text_it_was_shown(self):
        store = Store()
        [result] = run(store, [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}],
                                "evidence": [q("少女ミナは森の入口で"), q("この文章は本のどこにもありません")]}])
        self.assertEqual(result.status, "applied")
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM evidence")[0]["n"], 1)       # only the verified quote is stored
        end_of_text = StoryPosition(1, 0, len(TEXTS[(1, 0)])).key
        self.assertEqual(store.query("SELECT first_key FROM entities")[0]["first_key"], end_of_text)

    def test_all_quotes_unverifiable_rejects(self):
        [result] = run(Store(), [entity(quote="どこにもない一文です")])
        self.assertEqual(result.status, "rejected")

    def test_a_name_may_be_introduced_by_a_phrase_shorter_than_the_general_minimum(self):
        text = "ミナ様、どうしましたか？と聞かれた。"
        store = Store()
        [result] = run(store, [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}], "evidence": [q("ミナ様")]}],
                       ChapterContext(1, 0, text))
        self.assertEqual(result.status, "applied")

    def test_a_short_quote_is_not_enough_for_other_facts(self):
        text = "ミナ様、どうしましたか？と聞かれた。"
        store = Store()
        results = run(store, [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}], "evidence": [q("ミナ様")]},
                              {"op": "set_attribute", "entity": "n1", "key": "personality", "value": "丁寧", "evidence": [q("様")]}],
                      ChapterContext(1, 0, text))
        self.assertEqual([r.status for r in results], ["applied", "rejected"])

    def test_a_name_the_text_never_uses_is_rejected_as_invented(self):
        [result] = run(Store(), [entity(name="ドラゴン王", quote="少女ミナは森の入口で")])
        self.assertEqual(result.status, "rejected")
        self.assertIn("does not occur in the chapter text", result.reason)

    def test_the_introducing_quote_must_contain_the_name(self):
        [result] = run(Store(), [entity(name="ミナ", quote="この森には近づくな")])
        self.assertEqual(result.status, "rejected")
        self.assertIn("no evidence quote contains the name", result.reason)

    def test_readings_are_exempt_from_the_name_in_text_rule_because_ruby_is_not_plain_text(self):
        [result] = run(Store(), [{"op": "add_entity", "ref": "n1", "kind": "character",
                                  "names": [{"name": "ミナ", "type": "canonical"}, {"name": "みな", "type": "reading"}],
                                  "evidence": [q("少女ミナは森の入口で")]}])
        self.assertEqual(result.status, "applied")

    def test_add_name_follows_the_same_rules(self):
        store = Store()
        results = run(store, [entity(),
                              {"op": "add_name", "entity": "n1", "name": "ミナちゃん", "type": "nickname", "evidence": [q("少女ミナは森の入口で")]},
                              {"op": "add_name", "entity": "n1", "name": "ドラゴン", "type": "alias", "evidence": [q("少女ミナは森の入口で")]}])
        self.assertEqual([r.status for r in results], ["applied", "rejected", "rejected"])


class ReuseTests(unittest.TestCase):
    """Found with the real model (spike K0): it re-creates a known character; the ref must bind to the existing entity."""

    def after_chapter_zero(self):
        return apply_saga(upto=(1, 0))

    def test_recreating_a_known_entity_binds_the_ref_instead_of_rejecting_every_dependent_operation(self):
        store = self.after_chapter_zero()
        ctx = ChapterContext(1, 1, TEXTS[(1, 1)])
        results = run(store, [
            {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}], "evidence": [q("ミナの友だちのリクは")]},
            {"op": "set_attribute", "entity": "n1", "key": "occupation", "value": "探検家", "evidence": [q("ミナに「いっしょに森を探そう」と言った")]}], ctx)
        self.assertEqual([r.status for r in results], ["noop", "applied"])
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 2)                  # no second ミナ
        self.assertEqual(store.query("SELECT entity_id FROM attributes WHERE key='occupation'")[0]["entity_id"], "e0001")

    def test_a_new_name_for_a_reused_entity_becomes_an_alias_with_evidence(self):
        store = self.after_chapter_zero()
        ctx = ChapterContext(1, 1, "ミナちゃんは、ミナと呼ばれていた。")
        [result] = run(store, [{"op": "add_entity", "ref": "n1", "kind": "character",
                                "names": [{"name": "ミナ"}, {"name": "ミナちゃん", "type": "nickname"}], "evidence": [q("ミナちゃんは")]}], ctx)
        self.assertEqual(result.status, "applied")
        self.assertEqual(sorted(r["name"] for r in store.query("SELECT name FROM names WHERE entity_id='e0001'")), ["ミナ", "ミナちゃん"])
        self.assertEqual(store.query("SELECT type FROM names WHERE name='ミナちゃん'")[0]["type"], "nickname")

    def test_names_that_belong_to_two_different_entities_are_rejected(self):
        store = apply_saga(upto=(1, 1))
        ctx = ChapterContext(1, 2, "ミナとリクは歩いた。")
        [result] = run(store, [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}, {"name": "リク"}], "evidence": [q("ミナとリク")]}], ctx)
        self.assertEqual(result.status, "rejected")
        self.assertIn("different entities", result.reason)

    def test_an_entity_the_model_was_not_shown_is_still_not_reused(self):
        store = Store()
        run(store, [entity()])          # created in this very chapter by an earlier answer: not in this answer's snapshot
        [result] = run(store, [entity("n9", name="ミナ", quote="ミナは勇気を出して")])
        self.assertEqual(result.status, "rejected")
        self.assertIn("e0001", result.reason)

    def test_reuse_is_per_kind(self):
        store = self.after_chapter_zero()
        ctx = ChapterContext(1, 1, "ミナという名の村があった。")
        [result] = run(store, [{"op": "add_entity", "ref": "n1", "kind": "location", "names": [{"name": "ミナ"}], "evidence": [q("ミナという名の村")]}], ctx)
        self.assertEqual(result.status, "applied")                      # a place called ミナ is not the girl ミナ
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 3)


if __name__ == "__main__":
    unittest.main()
