import contextlib
import io
import json
import shutil
import tempfile
import unittest
from pathlib import Path

import loot_saga
from loot_saga import CHARACTER_ANSWERS, STORY_ANSWERS
from test_cli import run, cli
from test_llm import Server, completion
from immersive_knowledge.store import Store


class StoryCliTests(unittest.TestCase):
    def setUp(self):
        self.temp = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: shutil.rmtree(self.temp, ignore_errors=True))
        self.db = self.temp / "knowledge" / "loot-saga" / "knowledge.db"
        self.book = self.temp / "book-text.json"
        self.book.write_text(json.dumps(loot_saga.book_text(), ensure_ascii=False), encoding="utf-8")

    def serve(self, *script):
        server = Server(*script)
        self.addCleanup(server.close)
        return server

    def saga_server(self):
        script = [completion(json.dumps(CHARACTER_ANSWERS[k], ensure_ascii=False)) for k in sorted(CHARACTER_ANSWERS)]
        script += [completion(json.dumps(STORY_ANSWERS[k], ensure_ascii=False)) for k in sorted(STORY_ANSWERS)]
        return self.serve(*script)

    def build(self, server, **config):
        path = self.temp / "config.json"
        path.write_text(json.dumps({"endpoint": server.url, "model": "", "output_language": "Japanese", **config}), encoding="utf-8")
        return run("build", "--db", str(self.db), "--book-text", str(self.book), "--config", str(path))

    def build_story(self):
        code, out, err, events = self.build(self.saga_server(), story_pass=True)
        self.assertEqual(code, cli.EXIT_OK, err)
        return events

    def test_a_story_build_reports_both_passes_and_stores_cards(self):
        events = self.build_story()
        names = [e["event"] for e in events]
        self.assertEqual((names.count("chapter_done"), names.count("story_done")), (5, 5))
        self.assertEqual(events[-1]["event"], "snapshots_done")
        self.assertGreater(events[-1]["events"], 5)
        self.assertGreater(events[-1]["items"], 4)
        self.assertEqual(events[-1]["story_blocked"], 0)
        done = next(e for e in events if e["event"] == "story_done")
        self.assertEqual((done["volume"], done["chapter"], done["ops_applied"], done["ops_rejected"]), (1, 0, 4, 0))
        build_done = next(e for e in events if e["event"] == "build_done")
        self.assertEqual((build_done["story_built"], build_done["story_ops_rejected"]), (5, 0))

    def test_verify_passes_and_counts_the_story_data(self):
        self.build_story()
        code, out, err, _ = run("verify", "--db", str(self.db))
        self.assertEqual(code, cli.EXIT_OK, out)
        report = json.loads(out)
        self.assertTrue(report["ok"], report["problems"])
        self.assertEqual(report["counts"]["holdings"], 6)

    def test_events_and_items_commands_answer_for_a_position(self):
        self.build_story()
        code, out, _, _ = run("events", "--db", str(self.db), "--position", "1:3", "--kind", "item")
        self.assertEqual(code, cli.EXIT_OK)
        self.assertEqual([e["title"] for e in json.loads(out)["events"]], ["竜の宝箱", "鉄の剣が落ちた"])
        code, out, _, _ = run("events", "--db", str(self.db), "--position", "1:3", "--character", "e0002", "--limit", "1")
        result = json.loads(out)
        self.assertEqual((result["total"], len(result["events"])), (3, 1))
        code, out, _, _ = run("items", "--db", str(self.db), "--position", "1:3", "--holder", "e0001")
        self.assertEqual([i["name"] for i in json.loads(out)], ["炎の剣", "竜の盾"])
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as refused:
            run("events", "--db", str(self.db), "--position", "1:3", "--kind", "party")
        self.assertEqual(refused.exception.code, 2)                   # argparse: not a known kind

    def test_verify_notices_story_data_that_breaks_the_rules(self):
        self.build_story()
        store = Store(self.db)
        store.conn.execute("UPDATE story_event_cards SET card_json = REPLACE(card_json, '依頼', '黒い指輪の依頼') WHERE volume = 1 AND chapter = 1")
        store.conn.execute("UPDATE holdings SET to_key = NULL WHERE item_id = 'e0005'")        # the iron sword is held by two people at once now
        store.conn.execute("UPDATE events SET parent = 'v9999' WHERE kind = 'hunt'")
        store.conn.commit()
        store.close()
        code, out, _, _ = run("verify", "--db", str(self.db))
        problems = "\n".join(json.loads(out)["problems"])
        self.assertEqual(code, cli.EXIT_VERIFY)
        self.assertIn("names not yet visible: 黒い指輪", problems)
        self.assertIn("more than one open holding", problems)
        self.assertIn("unknown parent v9999", problems)

    def test_story_pass_is_off_unless_the_config_says_so(self):
        server = self.saga_server()
        code, _, err, events = self.build(server)
        self.assertEqual(code, cli.EXIT_OK, err)
        self.assertNotIn("story_start", [e["event"] for e in events])
        self.assertEqual(len(server.requests), 6)                     # /models + the five characters

    def test_an_unknown_story_setting_is_refused_like_any_other_unknown_key(self):
        code, out, err, events = self.build(self.saga_server(), story_pas=True)
        self.assertEqual(code, cli.EXIT_INPUT)
        self.assertIn("story_pas", err)


if __name__ == "__main__":
    unittest.main()
