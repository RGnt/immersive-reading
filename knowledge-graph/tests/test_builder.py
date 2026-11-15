import json
import re
import unittest

from immersive_knowledge.builder import Builder, BuildConfig, causal_snapshot, split_in_half, split_parts
from immersive_knowledge.gate import Gate
from immersive_knowledge.llm import LlmError
from immersive_knowledge.position import StoryPosition
from immersive_knowledge.store import Store

from saga import ANSWERS, CANARY, SECRET_NAME, TEXTS, FakeLlm, book_text, q

SNAPSHOT_BLOCK = re.compile(r"<<<SNAPSHOT_JSON\n(.*?)\nSNAPSHOT_JSON>>>", re.S)


def build(llm=None, store=None, book=None, **config):
    store = store or Store()
    llm = llm or FakeLlm()
    events = []
    builder = Builder(store, llm, BuildConfig(**config), progress=events.append)
    report = builder.run(book or book_text())
    return store, llm, report, events


def snapshot_of(call):
    return json.loads(SNAPSHOT_BLOCK.search(call["user"]).group(1))


class CausalBuildTests(unittest.TestCase):
    def test_the_whole_saga_builds_with_the_scripted_model(self):
        store, llm, report, events = build()
        self.assertEqual((report.chapters_built, report.chapters_skipped, report.failed), (6, 0, None))
        self.assertEqual(report.ops_applied, sum(len(a["ops"]) for a in ANSWERS.values()))
        self.assertEqual(report.ops_rejected, 0)
        self.assertEqual([c["status"] for c in store.chapters()], ["built"] * 6)
        self.assertEqual([e["event"] for e in events].count("chapter_done"), 6)
        self.assertEqual(events[-1]["event"], "build_done")

    def test_the_model_only_ever_sees_the_past(self):
        _, llm, _, _ = build()
        order = sorted(TEXTS)
        for call in llm.calls:
            current = call["key"]
            for later in (k for k in order if k > current):
                first_line = TEXTS[later].split("\n")[0]
                self.assertNotIn(first_line, call["user"], f"chapter {later} leaked into the request for {current}")
            self.assertIn(TEXTS[current].split("\n")[0], call["user"])

    def test_the_snapshot_holds_exactly_what_was_established_before_the_chapter(self):
        _, llm, _, _ = build()
        by_key = {c["key"]: c for c in llm.calls}
        self.assertEqual(snapshot_of(by_key[(1, 0)])["entities"], [])
        names_1_1 = {n for e in snapshot_of(by_key[(1, 1)])["entities"] for n in e["names"]}
        self.assertEqual(names_1_1, {"ミナ", "仮面の男"})
        # chapter 2 is processed with a snapshot that does not know the teacher yet (the secret is in the chapter text only)
        snapshot_2 = json.dumps(snapshot_of(by_key[(1, 2)]), ensure_ascii=False)
        self.assertNotIn(SECRET_NAME, snapshot_2)
        self.assertNotIn(CANARY, snapshot_2)
        # chapter 3 knows the identity, but not the canary fact it is about to establish
        snapshot_3 = snapshot_of(by_key[(1, 3)])
        self.assertIn(SECRET_NAME, json.dumps(snapshot_3, ensure_ascii=False))
        self.assertNotIn(CANARY, json.dumps(snapshot_3, ensure_ascii=False))
        self.assertTrue(any(e.get("same_as") for e in snapshot_3["entities"]))

    def test_volume_two_starts_with_everything_from_volume_one(self):
        _, llm, _, _ = build()
        snapshot = snapshot_of({c["key"]: c for c in llm.calls}[(2, 0)])
        self.assertIn("ミナ", {n for e in snapshot["entities"] for n in e["names"]})
        self.assertIn(CANARY, json.dumps(snapshot, ensure_ascii=False))     # volume 1 is read by then

    def test_prompts_carry_the_configured_language_and_limits(self):
        _, llm, _, _ = build(output_language="English", max_ops=33)
        self.assertIn("English", llm.calls[0]["system"])
        self.assertIn("33", llm.calls[0]["system"])
        self.assertNotIn("{{", llm.calls[0]["system"] + llm.calls[0]["user"])

    def test_untrusted_blocks_are_fenced(self):
        _, llm, _, _ = build()
        user = llm.calls[0]["user"]
        self.assertIn("<<<CHAPTER_TEXT\n", user)
        self.assertTrue(user.count("CHAPTER_TEXT>>>") == 1)


class ResumeTests(unittest.TestCase):
    def test_a_rebuild_with_nothing_changed_makes_no_requests(self):
        store, _, _, _ = build()
        llm = FakeLlm()
        report = Builder(store, llm).run(book_text())
        self.assertEqual((report.chapters_skipped, report.chapters_built, len(llm.calls)), (6, 0, 0))

    def test_a_failed_chapter_stops_the_build_and_resume_continues_from_it(self):
        store = Store()
        flaky = FakeLlm(fail={(1, 2): LlmError("the model did not return valid JSON", fatal=False)})
        _, _, report, events = build(llm=flaky, store=store)
        self.assertEqual(report.failed["chapter"], 2)
        self.assertEqual([c["status"] for c in store.chapters()], ["built", "built", "failed"])
        self.assertNotIn((1, 3), [c["key"] for c in flaky.calls])           # later chapters depend on the failed one
        self.assertEqual(events[-1]["event"], "chapter_failed")

        healthy = FakeLlm()
        report = Builder(store, healthy).run(book_text())
        self.assertEqual([c["key"] for c in healthy.calls], [(1, 2), (1, 3), (2, 0), (2, 1)])
        self.assertEqual((report.chapters_skipped, report.chapters_built, report.failed), (2, 4, None))

        reference, _, _, _ = build()
        dump = lambda s: ([tuple(r) for r in s.query("SELECT kind, first_key FROM entities ORDER BY id")],
                          [tuple(r) for r in s.query("SELECT entity_id, name, from_key FROM names ORDER BY id")],
                          [tuple(r) for r in s.query("SELECT summary, from_key FROM events ORDER BY id")])
        self.assertEqual(dump(store), dump(reference))

    def test_a_failed_chapter_leaves_no_half_applied_parts_after_resume(self):
        store = Store()
        # chapter 1 fails after the model answered once (answer without 'ops'): nothing of it may survive the resume
        broken = FakeLlm(answers={**ANSWERS, (1, 1): {"nonsense": True}})
        _, _, report, _ = build(llm=broken, store=store)
        self.assertEqual(report.failed["chapter"], 1)
        Builder(store, FakeLlm()).run(book_text())
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities WHERE id='e0003'")[0]["n"], 1)

    def test_a_fatal_error_aborts_the_build(self):
        fatal = FakeLlm(fail={(1, 1): LlmError("HTTP 401: bad key", fatal=True)})
        with self.assertRaises(LlmError):
            build(llm=fatal)

    def test_changed_text_rebuilds_that_chapter_and_everything_after_it(self):
        store, _, _, _ = build()
        changed = book_text()
        changed["volumes"][0]["chapters"][2]["text"] += "　風は冷たかった。" + chr(10)
        llm = FakeLlm()
        report = Builder(store, llm).run(changed)
        self.assertEqual([c["key"] for c in llm.calls], [(1, 2), (1, 3), (2, 0), (2, 1)])
        self.assertEqual(report.chapters_skipped, 2)
        # replaced, not duplicated: one set of operations per chapter
        for (v, c) in sorted(ANSWERS):
            self.assertEqual(store.query("SELECT COUNT(*) AS n FROM ops_log WHERE volume=? AND chapter=?", (v, c))[0]["n"],
                             len(ANSWERS[(v, c)]["ops"]), f"operations of {v}:{c}")
        # ids are never reused: the rebuilt teacher is a new entity (the scripted chapter-3 answer hard-codes the old id, which is
        # why it is rejected here; a real model reads ids from the snapshot)
        ids = [r["id"] for r in store.query("SELECT id FROM entities ORDER BY id")]
        self.assertNotIn("e0004", ids)
        self.assertIn("e0005", ids)
        self.assertEqual(ids[:3], ["e0001", "e0002", "e0003"])            # chapters before the change are untouched

    def test_resume_false_rebuilds_everything(self):
        store, _, _, _ = build()
        llm = FakeLlm()
        Builder(store, llm).run(book_text(), resume=False)
        self.assertEqual(len(llm.calls), 6)
        self.assertEqual(store.query("SELECT COUNT(*) AS n FROM entities")[0]["n"], 4)

    def test_from_position_rebuilds_from_that_chapter(self):
        store, _, _, _ = build()
        llm = FakeLlm()
        Builder(store, llm).run(book_text(), from_position=StoryPosition(1, 3))
        self.assertEqual([c["key"] for c in llm.calls], [(1, 3), (2, 0), (2, 1)])


class PartsAndEdgeTests(unittest.TestCase):
    def test_split_parts_keeps_the_separator_and_covers_the_text(self):
        text = "".join(f"{'あ' * 20}{i}\n" for i in range(10))
        parts = split_parts(text, 50)
        self.assertEqual("".join(p for _, p in parts), text)
        self.assertEqual([o for o, _ in parts], [sum(len(p) for _, p in parts[:i]) for i in range(len(parts))])
        self.assertTrue(all(p.endswith("\n") for _, p in parts[:-1]))
        self.assertEqual(split_parts("短い", 50), [(0, "短い")])

    def test_a_long_chapter_is_processed_in_parts_and_part_two_sees_part_ones_facts(self):
        first = "ミナは森の入口で仮面の男に出会った。\n"
        second = "ミナは勇気を出して森へ入っていった。\n"
        book = {"volumes": [{"volume": 1, "chapters": [{"index": 0, "title": "長い章", "text": first + second}]}]}
        answers = [
            {"ops": [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}], "evidence": [q("ミナは森の入口で")]}]},
            {"ops": [{"op": "add_event", "summary": "ミナは森へ入った", "participants": [{"entity": "e0001"}], "evidence": [q("森へ入っていった")]},
                     {"op": "add_event", "summary": "第一部の引用は無効", "participants": [], "evidence": [q("仮面の男に出会った")]}]},
        ]

        class Sequenced(FakeLlm):
            def chat_json(self, system, user, **options):
                super().chat_json(system, user, **options)
                return answers[len(self.calls) - 1]

        llm = Sequenced()
        store, _, report, events = build(llm=llm, book=book, max_input_chars=len(first) + 2)
        self.assertEqual(len(llm.calls), 2)
        self.assertIn("(part 2 of 2)", llm.calls[1]["user"])
        self.assertIn("e0001", llm.calls[1]["user"])                      # facts of part one are in part two's snapshot
        self.assertNotIn(second.strip(), llm.calls[0]["user"])            # and part one never saw part two
        statuses = [r["status"] for r in store.query("SELECT status FROM ops_log ORDER BY id")]
        # a quote that lives in part one is not valid evidence in part two: the model was not shown it there
        self.assertEqual(statuses, ["applied", "applied", "rejected"])
        offsets = store.query("SELECT start FROM evidence ORDER BY id")
        self.assertGreaterEqual(offsets[-1]["start"], len(first))         # offsets are absolute in the chapter
        self.assertEqual([e for e in events if e["event"] == "chapter_done"][0]["parts"], 2)

    def test_an_empty_chapter_is_built_without_a_request(self):
        book = {"volumes": [{"volume": 1, "chapters": [{"index": 0, "title": "挿絵", "text": ""}]}]}
        store, llm, report, _ = build(book=book)
        self.assertEqual((len(llm.calls), report.chapters_built), (0, 1))
        self.assertEqual(store.chapter_state(1, 0)["status"], "built")

    def test_snapshot_caps_keep_the_most_prominent_entities(self):
        store, _, _, _ = build()
        view = Gate(store).view(StoryPosition(2, 0))
        snapshot = json.loads(causal_snapshot(view, BuildConfig(snapshot_entities=2)))
        self.assertEqual(len(snapshot["entities"]), 2)
        self.assertTrue(all(r["src"] in {e["id"] for e in snapshot["entities"]} for r in snapshot["relations"]))


class TruncationRecoveryTests(unittest.TestCase):
    """Found with the real model (spike K0): the server cuts answers at its output limit."""

    LINES = ["ミナは森の入口で立ち止まった。" + "風が木々を揺らしていた。" * 4 + f"第{i}番" for i in range(40)]
    TEXT = chr(10).join(LINES) + chr(10)

    def book(self, text=None):
        return {"volumes": [{"volume": 1, "chapters": [{"index": 0, "title": "長い章", "text": text or self.TEXT}]}]}

    def llm(self, limit):
        answers = []

        class Truncating(FakeLlm):
            def chat_json(inner, system, user, **options):
                inner.calls.append({"system": system, "user": user, "key": (1, 0), "options": options})
                chapter = user.split("CHAPTER (process only this text; quotes must come from it):")[1]
                if len(chapter) > limit:
                    raise LlmError("the answer was cut off at the output limit", fatal=False, truncated=True)
                if "第0番" in chapter:
                    return {"ops": [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}], "evidence": [q("ミナは森の入口で立ち止まった")]}]}
                return {"ops": [{"op": "add_event", "summary": "ミナは立ち止まった", "participants": [{"entity": "e0001"}], "evidence": [q("第39番")]}]}

        return Truncating()

    def test_a_truncated_answer_is_retried_as_two_halves_with_nothing_lost(self):
        llm = self.llm(limit=len(self.TEXT) * 3 // 5)
        store, _, report, events = build(llm=llm, book=self.book())
        self.assertEqual((report.failed, report.splits, report.chapters_built), (None, 1, 1))
        self.assertEqual(len(llm.calls), 3)                                    # whole chapter, first half, second half
        first, second = llm.calls[1]["user"], llm.calls[2]["user"]
        self.assertIn("第0番", first)
        self.assertNotIn("第39番", first)
        self.assertIn("第39番", second)
        self.assertNotIn("第0番", second)
        self.assertIn("e0001", second)                                         # the second half knows what the first half established
        self.assertEqual([r["status"] for r in store.query("SELECT status FROM ops_log ORDER BY id")], ["applied", "applied"])
        half = len(split_in_half(self.TEXT)[0])
        self.assertGreaterEqual(store.query("SELECT start FROM evidence ORDER BY id")[-1]["start"], half)   # absolute offsets
        self.assertIn("part_split", [e["event"] for e in events])

    def test_halves_that_are_still_too_big_are_split_again_up_to_a_limit(self):
        llm = self.llm(limit=len(self.TEXT) // 3)
        _, _, report, _ = build(llm=llm, book=self.book())
        self.assertIsNone(report.failed)
        self.assertGreaterEqual(report.splits, 2)

    def test_a_piece_too_small_to_split_fails_the_chapter_with_a_clear_reason(self):
        tiny = "ミナは森にいた。（0）" + chr(10)
        llm = self.llm(limit=1)
        store, _, report, _ = build(llm=llm, book=self.book(tiny))
        self.assertEqual(report.failed["chapter"], 0)
        self.assertIn("cut off", report.failed["reason"])
        self.assertEqual(store.chapter_state(1, 0)["status"], "failed")

    def test_split_in_half_prefers_line_ends_then_sentence_ends(self):
        lines = chr(10).join(["あ" * 50] * 10)
        a, b = split_in_half(lines)
        self.assertEqual(a + b, lines)
        self.assertTrue(a.endswith(chr(10)) and a and b)
        sentences = "。".join(["い" * 50] * 10)
        a, b = split_in_half(sentences)
        self.assertEqual(a + b, sentences)
        self.assertTrue(a.endswith("。"))
        solid = "う" * 400
        a, b = split_in_half(solid)
        self.assertEqual((len(a), len(b)), (200, 200))


if __name__ == "__main__":
    unittest.main()
