"""Synthetic series "Test Saga" with a built-in spoiler canary, plus a scripted fake LLM.

Volume 1: ch0 a masked man (仮面の男) meets Mina; ch1 Mina and Riku become friends; ch2 the mask falls: the man IS the
teacher 青木先生 (identity reveal); ch3 青木先生 is the culprit (canary word 真犯人) and the friendship ends.
Volume 2: ch0 and ch1, short.
"""
import json
import re

from immersive_knowledge.ops import ChapterContext, apply_ops
from immersive_knowledge.store import Store

CANARY = "真犯人"
SECRET_NAME = "青木先生"

TEXTS = {
    (1, 0): "春の朝、少女ミナは森の入口で、仮面の男に出会った。\n仮面の男は低い声で「この森には近づくな」と言った。\nミナは勇気を出して「私はミナです。友だちを探しています」と答えた。\n",
    (1, 1): "ミナの友だちのリクは、いつも明るく笑う少年だった。\nリクはミナに「いっしょに森を探そう」と言った。\n二人は固い友情を誓った。\n",
    (1, 2): "風が吹いて仮面が外れ、中から現れたのは担任の青木先生だった。\nミナは「先生が仮面の男だったなんて」と叫んだ。\n",
    (1, 3): "青木先生こそが事件の真犯人だった。\nリクは裏切られたと感じ、ミナとの友情は終わってしまった。\n",
    (2, 0): "新しい学期が始まり、ミナは一人で森へ向かった。\n",
    (2, 1): "森の奥で、ミナはリクと再会した。\n",
}
TITLES = {(1, 0): "出会い", (1, 1): "友情", (1, 2): "正体", (1, 3): "真相", (2, 0): "新学期", (2, 1): "再会"}


def book_text():
    volumes = {}
    for (v, c), text in sorted(TEXTS.items()):
        volumes.setdefault(v, []).append({"index": c, "title": TITLES[(v, c)], "text": text})
    return {"series_key": "test-saga", "title": "Test Saga", "language": "ja",
            "volumes": [{"volume": v, "book_id": f"book-{v}", "title": f"Vol {v}", "series_index": v, "chapters": chs}
                        for v, chs in sorted(volumes.items())]}


def q(text):
    return {"quote": text}


# Answers the scripted model gives per chapter. Entity ids are deterministic (e0001...), so later chapters can use them.
ANSWERS = {
    (1, 0): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "ミナ", "type": "canonical"}], "evidence": [q("少女ミナは森の入口で")]},
        {"op": "add_entity", "ref": "n2", "kind": "character", "names": [{"name": "仮面の男", "type": "canonical"}], "evidence": [q("仮面の男に出会った")]},
        {"op": "set_attribute", "entity": "n2", "key": "speech_sample", "value": "この森には近づくな"},
        {"op": "set_attribute", "entity": "n1", "key": "speech_sample", "value": "私はミナです。友だちを探しています"},
        {"op": "set_attribute", "entity": "n1", "key": "personality", "value": "勇気がある", "evidence": [q("ミナは勇気を出して")]},
        {"op": "add_event", "summary": "ミナは森の入口で仮面の男に出会った", "participants": [{"entity": "n1", "role": "主人公"}, {"entity": "n2", "role": "出会った相手"}],
         "evidence": [q("少女ミナは森の入口で、仮面の男に出会った")]},
    ]},
    (1, 1): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "リク", "type": "canonical"}], "evidence": [q("ミナの友だちのリクは")]},
        {"op": "set_attribute", "entity": "n1", "key": "personality", "value": "いつも明るい少年", "evidence": [q("いつも明るく笑う少年だった")]},
        {"op": "add_relation", "src": "e0001", "dst": "n1", "type": "友だち", "directed": False, "summary": "ミナとリクは友だち", "evidence": [q("ミナの友だちのリク")]},
        {"op": "set_attribute", "entity": "n1", "key": "speech_sample", "value": "いっしょに森を探そう"},
        {"op": "add_event", "summary": "ミナとリクは固い友情を誓った", "participants": [{"entity": "e0001", "role": ""}, {"entity": "n1", "role": ""}],
         "evidence": [q("二人は固い友情を誓った")]},
    ]},
    (1, 2): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": SECRET_NAME, "type": "canonical"}], "evidence": [q("担任の青木先生だった")]},
        {"op": "same_as", "a": "e0002", "b": "n1", "evidence": [q("中から現れたのは担任の青木先生だった")]},
        {"op": "add_event", "summary": "仮面が外れ、仮面の男の正体が青木先生だと分かった", "participants": [{"entity": "e0001", "role": "目撃者"}, {"entity": "n1", "role": "正体"}],
         "evidence": [q("風が吹いて仮面が外れ")]},
    ]},
    (1, 3): {"ops": [
        {"op": "set_attribute", "entity": "e0004", "key": "status", "value": f"事件の{CANARY}", "evidence": [q("青木先生こそが事件の真犯人だった")]},
        {"op": "end_relation", "relation": "r0001", "evidence": [q("ミナとの友情は終わってしまった")]},
        {"op": "add_event", "summary": "リクは裏切られたと感じ、友情は終わった", "participants": [{"entity": "e0003", "role": ""}], "evidence": [q("リクは裏切られたと感じ")]},
        {"op": "add_event", "summary": f"青木先生が{CANARY}だと明らかになった", "participants": [{"entity": "e0004", "role": "犯人"}],
         "evidence": [q("青木先生こそが事件の真犯人だった")]},
    ]},
    (2, 0): {"ops": [
        {"op": "add_event", "summary": "ミナは一人で森へ向かった", "participants": [{"entity": "e0001", "role": ""}], "evidence": [q("ミナは一人で森へ向かった")]},
    ]},
    (2, 1): {"ops": [
        {"op": "add_event", "summary": "ミナはリクと再会した", "participants": [{"entity": "e0001", "role": ""}, {"entity": "e0003", "role": ""}],
         "evidence": [q("ミナはリクと再会した")]},
    ]},
}


def apply_saga(store=None, *, upto=None):
    """Apply the scripted answers directly (bypassing the builder) and return the store."""
    store = store or Store()
    for (v, c), answer in sorted(ANSWERS.items()):
        if upto is not None and (v, c) > upto:
            break
        apply_ops(store, ChapterContext(v, c, TEXTS[(v, c)]), answer["ops"])
    return store


class FakeLlm:
    """Scripted model. Records every request; answers by the chapter named in the prompt."""
    model = "fake-model"

    def __init__(self, answers=None, *, fail=None):
        self.answers = ANSWERS if answers is None else answers
        self.fail = fail or {}
        self.calls = []

    def chat_json(self, system, user, **options):
        match = re.search(r"VOLUME (\d+) CHAPTER (\d+)", user)
        key = (int(match.group(1)), int(match.group(2)))
        self.calls.append({"system": system, "user": user, "key": key, "options": options})
        failure = self.fail.get(key)
        if failure is not None:
            if isinstance(failure, Exception):
                raise failure
            if callable(failure):
                return failure(len([c for c in self.calls if c["key"] == key]))
        answer = self.answers.get(key, {"ops": []})
        return json.loads(json.dumps(answer))
