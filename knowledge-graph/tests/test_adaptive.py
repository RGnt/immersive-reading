"""Behaviour discovered with the real model/server (spike K0): context budget, name sightings, boundary-aware scanning."""
import json
import unittest

from immersive_knowledge.builder import Builder, BuildConfig
from immersive_knowledge.evidence import find_word, normalize
from immersive_knowledge.gate import Gate
from immersive_knowledge.llm import LlmError, OpenAiClient
from immersive_knowledge.position import StoryPosition, as_of
from immersive_knowledge.scanner import hidden_names, scan
from immersive_knowledge.store import Store

from saga import FakeLlm, SECRET_NAME, book_text, q
from test_builder import build
from test_llm import Server, completion


def key(text):
    return normalize(text)[0].casefold()


class FindWordTests(unittest.TestCase):
    def test_a_name_embedded_in_a_longer_word_of_the_same_script_is_not_found(self):
        self.assertEqual(find_word(key("ギルドに行く"), key("ギル")), -1)
        self.assertEqual(find_word(key("リクエストを送る"), key("リク")), -1)
        self.assertEqual(find_word(key("ミナミは笑った"), key("ミナ")), -1)

    def test_a_name_followed_by_another_script_or_an_honorific_is_found(self):
        self.assertGreaterEqual(find_word(key("クマさんは笑った"), key("クマ")), 0)
        self.assertGreaterEqual(find_word(key("リクが来た"), key("リク")), 0)
        self.assertGreaterEqual(find_word(key("担任の青木先生が来た"), key("青木")), 0)       # honorific
        self.assertEqual(find_word(key("青木橋を渡る"), key("青木")), -1)                   # a different kanji word
        self.assertGreaterEqual(find_word(key("「ミナ」と呼んだ"), key("ミナ")), 0)           # brackets are other

    def test_the_first_acceptable_occurrence_is_returned(self):
        text = key("リクエストの後でリクが来た")
        self.assertEqual(find_word(text, key("リク")), text.index("リクが"))

    def test_empty_needle_and_latin_words(self):
        self.assertEqual(find_word("abc", ""), -1)
        self.assertEqual(find_word(key("category, cat"), key("cat")), len("category,"))


class SightingTests(unittest.TestCase):
    BOOK = {"volumes": [{"volume": 1, "chapters": [
        {"index": 0, "title": "一", "text": "ミナはクマのぬいぐるみを抱いていた。\n"},
        {"index": 1, "title": "二", "text": "ある日、クマが動き出した。ドラゴンは現れなかった。\n"},
        {"index": 2, "title": "三", "text": "ミナの前にドラゴンが現れた。\n"}]}]}

    def llm(self):
        answers = {
            (1, 0): {"ops": [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ"}], "evidence": [q("ミナはクマのぬいぐるみ")]}]},
            (1, 1): {"ops": [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "クマ"}], "evidence": [q("クマが動き出した")]}]},
            (1, 2): {"ops": [{"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ドラゴン"}], "evidence": [q("ドラゴンが現れた")]}]},
        }
        return FakeLlm(answers=answers)

    def test_a_name_the_reader_already_read_as_ordinary_text_is_not_hidden(self):
        store, _, report, _ = build(llm=self.llm(), book=self.BOOK)
        self.assertIsNone(report.failed)
        seen = {r["name"]: r["seen_key"] for r in store.query("SELECT name, seen_key FROM names")}
        first_bear = self.BOOK["volumes"][0]["chapters"][0]["text"].index("クマ") + len("クマ")
        self.assertEqual(seen["クマ"], StoryPosition(1, 0, first_bear).key)          # seen in chapter 0, entity created in chapter 1
        view = Gate(store).view(as_of(1, 1))
        self.assertNotIn("クマ", hidden_names(store, view).values())
        self.assertEqual(scan(store, view, "クマさんはミナの友だち"), [])

    def test_a_name_the_reader_has_not_read_stays_hidden_until_it_appears(self):
        store, _, _, _ = build(llm=self.llm(), book=self.BOOK)
        # "ドラゴン" occurs in chapter 1 text ("ドラゴンは現れなかった") so it is no secret from chapter 2 on, but not before
        self.assertIn("ドラゴン", hidden_names(store, Gate(store).view(as_of(1, 1))).values())
        self.assertNotIn("ドラゴン", hidden_names(store, Gate(store).view(as_of(1, 2))).values())
        self.assertEqual(scan(store, Gate(store).view(as_of(1, 1)), "ドラゴンが来る"), ["ドラゴン"])

    def test_the_culprit_of_the_saga_stays_hidden_because_the_name_first_appears_at_the_reveal(self):
        store, _, _, _ = build()
        self.assertEqual(set(hidden_names(store, Gate(store).view(as_of(1, 2))).values()), {SECRET_NAME})
        first = store.query("SELECT seen_key FROM names WHERE name=?", (SECRET_NAME,))[0]["seen_key"]
        self.assertGreaterEqual(first, as_of(1, 2).key)

    def test_names_without_an_indexed_sighting_fall_back_to_the_strict_rule(self):
        store, _, _, _ = build(llm=self.llm(), book=self.BOOK)
        store.conn.execute("UPDATE names SET seen_key=NULL WHERE name='クマ'")
        store.conn.commit()
        self.assertIn("クマ", hidden_names(store, Gate(store).view(as_of(1, 1))).values())


class BoundaryScanTests(unittest.TestCase):
    def test_a_hidden_name_inside_a_longer_word_is_not_a_leak_but_the_bare_word_is(self):
        store = Store()
        with store.transaction():
            far = 10 ** 12 * 5
            e = store.add_entity("character", far, 1)
            store.add_name(e, "ギル", "canonical", far, 1, [])
        view = Gate(store).view(StoryPosition(1, 0))
        self.assertEqual(scan(store, view, "冒険者ギルドに行った"), [])
        self.assertEqual(scan(store, view, "ギルは笑った"), ["ギル"])


class ContextBudgetTests(unittest.TestCase):
    """The server advertises 262,144 tokens but enforced 8,241 per request (prompt + generation)."""

    def overflow(self, limit=8241):
        error = LlmError(f"HTTP 400: prompt is too long: {limit + 400} tokens > {limit} maximum (prompt + generation)", fatal=False, truncated=True)
        error.prompt_too_big, error.context_limit = True, limit
        return error

    def crowded_store(self, entities=200):
        store = Store()
        with store.transaction():
            for i in range(entities):
                e = store.add_entity("character", 100 + i, 1)
                store.add_name(e, f"人物{i}", "canonical", 100 + i, 1, [], preferred=True)
                store.add_attribute(e, "personality", f"性格{i}", 100 + i, 1, [], single_valued=True)
        return store

    def book(self, text):
        return {"volumes": [{"volume": 1, "chapters": [{"index": 0, "title": "章", "text": text}]}]}

    def test_a_prompt_that_is_too_big_gets_a_smaller_snapshot_first_and_the_limit_is_learned(self):
        calls, events = [], []

        class Picky(FakeLlm):
            def chat_json(inner, system, user, **options):
                snapshot = json.loads(user.split("<<<SNAPSHOT_JSON\n")[1].split("\nSNAPSHOT_JSON>>>")[0])
                calls.append(len(snapshot["entities"]))
                if len(snapshot["entities"]) > 130:
                    raise self.overflow()
                return {"ops": []}

        store = self.crowded_store()
        builder = Builder(store, Picky(), BuildConfig(), progress=events.append)
        report = builder.run(self.book("ミナは歩いた。\n"))
        self.assertIsNone(report.failed)
        self.assertEqual(calls, [200, 120])                                   # level 0 refused, level 1 accepted
        self.assertEqual(builder.context_tokens, 8241)
        self.assertEqual(builder.snapshot_level, 1)
        self.assertIn("context_limit_learned", [e["event"] for e in events])
        self.assertIn("snapshot_shrunk", [e["event"] for e in events])

    def test_after_learning_the_limit_parts_are_sized_up_front_without_failed_requests(self):
        text = (chr(10).join("ミナは森を歩いた。" * 20 for _ in range(30))) + chr(10)           # about 5,000 characters
        sizes = []

        class Strict(FakeLlm):
            def chat_json(inner, system, user, **options):
                sizes.append(len(user.split("<<<CHAPTER_TEXT\n")[1].split("\nCHAPTER_TEXT>>>")[0]))
                if len(sizes) == 1:
                    raise ContextBudgetTests.overflow(self_, 8241)
                return {"ops": []}

        self_ = self
        builder = Builder(Store(), Strict(), BuildConfig())
        report = builder.run(self.book(text))
        self.assertIsNone(report.failed)
        expected = int(0.25 * 8241 * 1.6)
        self.assertEqual(builder.part_chars(), expected)
        # first request failed at full size; afterwards no part exceeds the learned size (plus one line of slack)
        self.assertTrue(all(size <= expected + 200 for size in sizes[1:]), sizes)

    def test_when_even_a_minimal_snapshot_does_not_fit_the_text_is_split(self):
        texts = []

        class Tiny(FakeLlm):
            def chat_json(inner, system, user, **options):
                chapter = user.split("<<<CHAPTER_TEXT\n")[1].split("\nCHAPTER_TEXT>>>")[0]
                texts.append(len(chapter))
                if len(chapter) > 1500:
                    raise self_.overflow()
                return {"ops": []}

        self_ = self
        text = (chr(10).join("ミナは森を歩いた。" * 10 for _ in range(40))) + chr(10)        # about 3,600 characters
        report = Builder(Store(), Tiny(), BuildConfig(context_tokens=0)).run(self.book(text))
        self.assertIsNone(report.failed)
        self.assertGreaterEqual(report.splits, 1)
        self.assertTrue(any(t <= 1500 for t in texts))

    def test_a_prompt_that_cannot_be_made_smaller_fails_the_chapter_with_the_servers_numbers(self):
        class Never(FakeLlm):
            def chat_json(inner, system, user, **options):
                raise self_.overflow()

        self_ = self
        report = Builder(Store(), Never(), BuildConfig()).run(self.book("短い章。\n"))
        self.assertEqual(report.failed["chapter"], 0)
        self.assertIn("8241 maximum", report.failed["reason"])


class ServerMessageTests(unittest.TestCase):
    def test_the_real_servers_overflow_message_is_understood_not_retried_and_not_fatal(self):
        body = ('{"error":{"message":"prompt is too long: 8630 tokens > 8241 maximum (prompt + generation); shorten the prompt or '
                'increase the KV cache budget","type":"invalid_request_error","param":null,"code":"context_length_exceeded"}}')
        server = Server((400, body))
        self.addCleanup(server.close)
        with self.assertRaises(LlmError) as raised:
            OpenAiClient(server.url, "m", sleep=lambda s: None).chat_json("s", "u")
        error = raised.exception
        self.assertEqual((error.fatal, error.truncated, error.prompt_too_big, error.context_limit), (False, True, True, 8241))
        self.assertEqual(len(server.posts()), 1)


if __name__ == "__main__":
    unittest.main()
