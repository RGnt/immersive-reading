import contextlib
import io
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from immersive_knowledge import cli
from immersive_knowledge.store import Store

from saga import ANSWERS, SECRET_NAME, book_text
from test_llm import Server, completion


def run(*argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = cli.main(list(argv))
    events = [json.loads(line) for line in out.getvalue().splitlines() if line.startswith("{") and '"event"' in line]
    return code, out.getvalue(), err.getvalue(), events


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp = Path(tempfile.mkdtemp())
        self.addCleanup(lambda: __import__("shutil").rmtree(self.temp, ignore_errors=True))
        self.db = self.temp / "knowledge" / "test-saga" / "knowledge.db"      # parent folders do not exist yet
        self.book = self.temp / "book-text.json"
        self.book.write_text(json.dumps(book_text(), ensure_ascii=False), encoding="utf-8")

    def config(self, server, **extra):
        path = self.temp / "config.json"
        path.write_text(json.dumps({"endpoint": server.url, "model": "", "output_language": "Japanese", **extra}), encoding="utf-8")
        return path

    def serve(self, *script):
        server = Server(*script)
        self.addCleanup(server.close)
        return server

    def saga_server(self):
        return self.serve(*[completion(json.dumps(ANSWERS[k], ensure_ascii=False)) for k in sorted(ANSWERS)])

    def build(self, server, *extra, **config):
        return run("build", "--db", str(self.db), "--book-text", str(self.book), "--config", str(self.config(server, **config)), *extra)

    # ------------------------------------------------------------------
    def test_build_reports_progress_as_json_lines_and_leaves_a_verifiable_database(self):
        server = self.saga_server()
        code, out, err, events = self.build(server)
        self.assertEqual(code, cli.EXIT_OK, err)
        names = [e["event"] for e in events]
        self.assertEqual(names[0], "build_start")
        self.assertEqual(names.count("chapter_done"), 6)
        self.assertIn("build_done", names)
        self.assertEqual(events[-1]["event"], "snapshots_done")
        self.assertGreater(events[-1]["stored"], 0)
        done = next(e for e in events if e["event"] == "chapter_done")
        self.assertEqual((done["volume"], done["chapter"], done["ops_applied"], done["ops_rejected"]), (1, 0, 6, 0))
        self.assertTrue(self.db.exists())
        self.assertEqual(server.requests[0]["path"], "/v1/models")        # model discovered, none configured

        code, out, err, _ = run("verify", "--db", str(self.db))
        self.assertEqual(code, cli.EXIT_OK, out)
        self.assertTrue(json.loads(out)["ok"])

    def test_the_key_comes_from_the_environment_only_and_never_appears_in_output(self):
        server = self.saga_server()
        with mock.patch.dict(os.environ, {cli.DEFAULT_KEY_ENV: "sekret-token-123"}):
            code, out, err, _ = self.build(server)
        self.assertEqual(code, cli.EXIT_OK)
        self.assertEqual({r["auth"] for r in server.requests}, {"Bearer sekret-token-123"})
        self.assertNotIn("sekret-token-123", out + err + (self.temp / "config.json").read_text(encoding="utf-8"))

    def test_a_key_in_the_config_file_is_refused(self):
        server = self.saga_server()
        code, out, err, events = self.build(server, api_key="sekret")
        self.assertEqual(code, cli.EXIT_INPUT)
        self.assertIn("environment", err)
        self.assertNotIn("sekret", out + err)
        self.assertEqual(server.requests, [])

    def test_an_edited_prompt_in_prompts_dir_is_the_one_sent_to_the_model(self):
        from immersive_knowledge.profile import PROMPTS_DIR
        folder = self.temp / "my-prompts"
        folder.mkdir()
        original = (PROMPTS_DIR / "knowledge.extract.system.prompt.md").read_text(encoding="utf-8")
        (folder / "knowledge.extract.system.prompt.md").write_text(original + "\nZZ-MY-OWN-RULE\n", encoding="utf-8")
        server = self.saga_server()

        code, out, err, events = self.build(server, prompts_dir=str(folder))

        self.assertEqual(code, cli.EXIT_OK, err)
        sent = [r["body"]["messages"][0]["content"] for r in server.requests if r["body"]]
        self.assertTrue(sent and all("ZZ-MY-OWN-RULE" in m for m in sent[:6]))

    def test_a_prompts_dir_that_does_not_fit_the_builder_is_refused_before_anything_is_sent(self):
        folder = self.temp / "bad-prompts"
        folder.mkdir()
        server = self.saga_server()
        (folder / "somebody.else.prompt.md").write_text("---\nname: somebody.else\nversion: 1\n---\nhi", encoding="utf-8")
        self.assertEqual(self.build(server, prompts_dir=str(folder))[0], cli.EXIT_INPUT)
        (folder / "somebody.else.prompt.md").unlink()
        (folder / "knowledge.extract.system.prompt.md").write_text("no front matter at all", encoding="utf-8")
        code, _, err, _ = self.build(server, prompts_dir=str(folder))
        self.assertEqual(code, cli.EXIT_INPUT)
        self.assertIn("front matter", err)
        self.assertEqual(self.build(server, prompts_dir=str(self.temp / "nope"))[0], cli.EXIT_INPUT)
        self.assertFalse([r for r in server.requests if r["body"]])           # no chapter was ever sent

    def test_exit_codes(self):
        server = self.saga_server()
        self.assertEqual(run("build", "--db", str(self.db), "--book-text", str(self.book), "--config", str(self.temp / "missing.json"))[0], cli.EXIT_INPUT)
        bad = self.temp / "bad-book.json"
        bad.write_text('{"volumes": []}', encoding="utf-8")
        self.assertEqual(run("build", "--db", str(self.db), "--book-text", str(bad), "--config", str(self.config(server)))[0], cli.EXIT_INPUT)
        no_endpoint = self.temp / "no-endpoint.json"
        no_endpoint.write_text("{}", encoding="utf-8")
        self.assertEqual(run("build", "--db", str(self.db), "--book-text", str(self.book), "--config", str(no_endpoint))[0], cli.EXIT_INPUT)
        self.assertEqual(run("characters", "--db", str(self.db), "--position", "nonsense")[0], cli.EXIT_INPUT)

        unauthorized = self.serve((401, '{"error": "invalid api key"}'))
        code, out, err, events = self.build(unauthorized, model="m")
        self.assertEqual(code, cli.EXIT_LLM)
        self.assertTrue(events[-1]["fatal"])
        self.assertIn("401", err)

    def test_a_failed_chapter_exits_with_partial_and_the_next_run_resumes(self):
        first = self.serve(completion(json.dumps(ANSWERS[(1, 0)])), completion(json.dumps(ANSWERS[(1, 1)])), completion("not json at all"))
        code, out, err, events = self.build(first)
        self.assertEqual(code, cli.EXIT_PARTIAL)
        self.assertEqual(events[-2]["event"], "chapter_failed")
        self.assertIn("1:2", err)

        second = self.serve(*[completion(json.dumps(ANSWERS[k])) for k in sorted(ANSWERS) if k >= (1, 2)])
        code, out, err, events = self.build(second)
        self.assertEqual(code, cli.EXIT_OK, err)
        self.assertEqual([e["event"] for e in events].count("chapter_skipped"), 2)
        self.assertEqual(len([r for r in second.posts()]), 4)              # chapters 1:2 .. 2:1 only
        self.assertEqual(run("verify", "--db", str(self.db))[0], cli.EXIT_OK)

    def test_characters_and_profile_read_the_stored_snapshots(self):
        self.build(self.saga_server())
        code, out, _, _ = run("characters", "--db", str(self.db), "--position", "1:2")
        names = {c["name"] for c in json.loads(out)}
        self.assertEqual(names, {"ミナ", "リク", "仮面の男"})
        self.assertNotIn(SECRET_NAME, out)
        code, out, _, _ = run("profile", "--db", str(self.db), "--character", "e0001", "--position", "1:2")
        self.assertEqual(code, cli.EXIT_OK)
        self.assertEqual(json.loads(out)["name"], "ミナ")
        self.assertEqual(run("profile", "--db", str(self.db), "--character", "e0004", "--position", "1:2")[0], cli.EXIT_INPUT)

    def test_verify_catches_a_fact_positioned_earlier_than_its_evidence_and_a_leaking_snapshot(self):
        self.build(self.saga_server())
        store = Store(self.db)
        store.conn.execute("UPDATE attributes SET from_key = 1 WHERE id = (SELECT id FROM attributes WHERE key='status')")
        store.conn.execute("INSERT OR REPLACE INTO profile_snapshots VALUES ('e0001', 1, 1, 'ミナ', 1, '{}', ?)", (f"{SECRET_NAME}が犯人",))
        store.conn.commit()
        store.close()
        code, out, _, _ = run("verify", "--db", str(self.db))
        self.assertEqual(code, cli.EXIT_VERIFY)
        problems = "\n".join(json.loads(out)["problems"])
        self.assertIn("I-K1", problems)
        self.assertIn(SECRET_NAME, problems)

    def test_verify_accepts_a_fact_positioned_later_than_its_evidence(self):
        # a fact that had an unverifiable quote is deliberately positioned at the end of the text the model saw: later is safe, only earlier is a leak
        self.build(self.saga_server())
        store = Store(self.db)
        store.conn.execute("UPDATE attributes SET from_key = from_key + 5 WHERE id = (SELECT id FROM attributes WHERE key='status')")
        store.conn.commit()
        store.close()
        code, out, _, _ = run("verify", "--db", str(self.db))
        self.assertEqual(code, cli.EXIT_OK, out)

    def test_a_database_from_another_schema_version_is_refused(self):
        self.build(self.saga_server())
        store = Store(self.db)
        store.conn.execute("UPDATE meta SET value='99' WHERE key='schema_version'")
        store.conn.commit()
        store.close()
        code, out, err, _ = run("verify", "--db", str(self.db))
        self.assertEqual(code, cli.EXIT_INPUT)
        self.assertIn("schema", err)


if __name__ == "__main__":
    unittest.main()
