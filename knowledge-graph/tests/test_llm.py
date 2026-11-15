import json
import socket
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from immersive_knowledge.llm import BACKOFF, LlmError, OpenAiClient, extract_json


def completion(content):
    return 200, json.dumps({"choices": [{"message": {"content": content}}]})


class Server:
    """Loopback OpenAI-compatible server answering from a script (last entry repeats)."""

    def __init__(self, *script, delay=0.0, models=("fake-model",)):
        self.script, self.requests, self.delay, self.models = list(script), [], delay, models
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def _send(self, status, body):
                data = body.encode("utf-8")
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                self.wfile.write(data)

            def do_GET(self):
                outer.requests.append({"path": self.path, "body": None, "auth": self.headers.get("Authorization")})
                self._send(200, json.dumps({"data": [{"id": m} for m in outer.models]}))

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                outer.requests.append({"path": self.path, "body": body, "auth": self.headers.get("Authorization")})
                if outer.delay:
                    time.sleep(outer.delay)
                index = min(len(outer.requests) - 1 - sum(1 for r in outer.requests if r["body"] is None), len(outer.script) - 1)
                try:
                    self._send(*outer.script[index])
                except OSError:
                    pass

        self.httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.url = f"http://127.0.0.1:{self.httpd.server_address[1]}/v1"
        threading.Thread(target=lambda: self.httpd.serve_forever(poll_interval=0.01), daemon=True).start()

    def posts(self):
        return [r for r in self.requests if r["body"] is not None]

    def close(self):
        self.httpd.shutdown()
        self.httpd.server_close()


class ClientTests(unittest.TestCase):
    def serve(self, *script, **kw):
        server = Server(*script, **kw)
        self.addCleanup(server.close)
        return server

    def client(self, server, **kw):
        self.sleeps = []
        return OpenAiClient(server.url, kw.pop("model", "fake-model"), kw.pop("api_key", None), sleep=self.sleeps.append, **kw)

    def test_a_json_answer_is_returned_and_the_request_is_well_formed(self):
        server = self.serve(completion('{"ops": []}'))
        result = self.client(server, api_key="secret", temperature=0.1, max_tokens=123).chat_json("sys", "usr")
        self.assertEqual(result, {"ops": []})
        request = server.posts()[0]
        self.assertEqual(request["path"], "/v1/chat/completions")
        self.assertEqual(request["auth"], "Bearer secret")
        self.assertEqual(request["body"]["model"], "fake-model")
        self.assertEqual(request["body"]["messages"], [{"role": "system", "content": "sys"}, {"role": "user", "content": "usr"}])
        self.assertEqual((request["body"]["temperature"], request["body"]["max_tokens"]), (0.1, 123))
        self.assertEqual(request["body"]["response_format"], {"type": "json_object"})
        self.assertNotIn("chat_template_kwargs", request["body"])

    def test_a_full_chat_completions_url_works_too(self):
        server = self.serve(completion("{}"))
        OpenAiClient(server.url + "/chat/completions", "m").chat_json("s", "u")
        self.assertEqual(server.posts()[0]["path"], "/v1/chat/completions")

    def test_a_bare_host_gets_the_v1_base(self):
        server = self.serve(completion("{}"))
        bare = server.url[: -len("/v1")]
        OpenAiClient(bare, "m").chat_json("s", "u")
        OpenAiClient(bare + "/", "m").chat_json("s", "u")
        self.assertEqual([p["path"] for p in server.posts()], ["/v1/chat/completions"] * 2)

    def test_the_model_is_discovered_when_not_configured(self):
        server = self.serve(completion("{}"), models=("Huihui-Qwen3.6-35B-A3B.gguf",))
        client = self.client(server, model="")
        client.chat_json("s", "u")
        self.assertEqual(server.posts()[0]["body"]["model"], "Huihui-Qwen3.6-35B-A3B.gguf")

    def test_thinking_can_be_forced_on_or_off(self):
        for mode, expected in (("off", False), ("on", True)):
            server = self.serve(completion("{}"))
            self.client(server, thinking=mode).chat_json("s", "u")
            self.assertEqual(server.posts()[0]["body"]["chat_template_kwargs"], {"enable_thinking": expected})

    def test_think_blocks_and_markdown_fences_are_stripped(self):
        self.assertEqual(extract_json('<think>{"x": 1} maybe</think>\n```json\n{"ops": [1]}\n```'), {"ops": [1]})
        self.assertIsNone(extract_json("no json"))
        self.assertIsNone(extract_json("[1, 2]"))
        self.assertIsNone(extract_json("{broken"))

    def test_invalid_json_gets_one_repair_attempt(self):
        server = self.serve(completion("Sure! here you go"), completion('{"ops": []}'))
        self.assertEqual(self.client(server).chat_json("s", "u"), {"ops": []})
        second = server.posts()[1]["body"]["messages"]
        self.assertEqual([m["role"] for m in second], ["system", "user", "assistant", "user"])
        self.assertIn("not a valid JSON", second[-1]["content"])

    def test_invalid_json_twice_is_a_chapter_level_failure_not_a_fatal_one(self):
        server = self.serve(completion("nope"))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertFalse(raised.exception.fatal)
        self.assertEqual(len(server.posts()), 2)

    def test_an_answer_cut_off_by_hidden_reasoning_is_explained_and_not_retried(self):
        body = json.dumps({"choices": [{"finish_reason": "length", "message": {"content": "", "reasoning_content": "Here is my thinking..."}}]})
        server = self.serve((200, body))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertFalse(raised.exception.fatal)
        self.assertIn('thinking to "off"', str(raised.exception))
        self.assertEqual(len(server.posts()), 1)

    def test_a_truncated_answer_suggests_smaller_requests(self):
        body = json.dumps({"choices": [{"finish_reason": "length", "message": {"content": '{"ops": [{"op": "add_en'}}]})
        server = self.serve((200, body))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertIn("max_ops", str(raised.exception))

    def test_an_empty_answer_that_stopped_normally_is_a_json_failure(self):
        body = json.dumps({"choices": [{"finish_reason": "stop", "message": {"content": ""}}]})
        server = self.serve((200, body))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertIn("valid JSON", str(raised.exception))

    def test_server_errors_and_rate_limits_are_retried_with_backoff(self):
        server = self.serve((429, "slow down"), (503, "busy"), completion('{"ok": true}'))
        self.assertEqual(self.client(server).chat_json("s", "u"), {"ok": True})
        self.assertEqual(self.sleeps, list(BACKOFF[:2]))

    def test_persistent_rate_limiting_fails_the_chapter_not_the_build(self):
        server = self.serve((429, "slow down"))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertFalse(raised.exception.fatal)
        self.assertIn("429", str(raised.exception))
        self.assertEqual(len(server.posts()), len(BACKOFF) + 1)

    def test_a_bad_key_is_fatal_not_retried_and_shows_the_body(self):
        server = self.serve((401, '{"error": "invalid api key"}'))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertTrue(raised.exception.fatal)
        self.assertIn("401", str(raised.exception))
        self.assertIn("invalid api key", str(raised.exception))
        self.assertEqual(len(server.posts()), 1)

    def test_a_context_overflow_is_chapter_level_and_not_retried(self):
        server = self.serve((400, '{"error": "request exceeds the available context size"}'))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertFalse(raised.exception.fatal)
        self.assertEqual(len(server.posts()), 1)

    def test_a_server_that_rejects_response_format_is_retried_without_it(self):
        server = self.serve((400, '{"error": "response_format is not supported"}'), completion('{"ok": 1}'))
        self.assertEqual(self.client(server).chat_json("s", "u"), {"ok": 1})
        self.assertIn("response_format", server.posts()[0]["body"])
        self.assertNotIn("response_format", server.posts()[1]["body"])

    def test_an_unreachable_endpoint_is_fatal(self):
        with socket.socket() as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]
        with self.assertRaises(LlmError) as raised:
            OpenAiClient(f"http://127.0.0.1:{port}/v1", "m", sleep=lambda s: None).chat_json("s", "u")
        self.assertTrue(raised.exception.fatal)
        self.assertIn("cannot reach", str(raised.exception))

    def test_a_slow_model_fails_that_chapter_only(self):
        server = self.serve(completion("{}"), delay=0.6)
        with self.assertRaises(LlmError) as raised:
            self.client(server, timeout=0.15).chat_json("s", "u")
        self.assertFalse(raised.exception.fatal)
        self.assertIn("did not answer", str(raised.exception))

    def test_an_unexpected_response_shape_is_fatal(self):
        server = self.serve((200, '{"not": "a completion"}'))
        with self.assertRaises(LlmError) as raised:
            self.client(server).chat_json("s", "u")
        self.assertTrue(raised.exception.fatal)

    def test_configuration_errors_are_fatal_and_there_is_never_a_fallback(self):
        for bad in ("", "offline://heuristic"):
            with self.assertRaises(LlmError) as raised:
                OpenAiClient(bad, "m")
            self.assertTrue(raised.exception.fatal)
        with self.assertRaises(LlmError):
            OpenAiClient("http://127.0.0.1:1/v1", "m", thinking="maybe")


if __name__ == "__main__":
    unittest.main()
