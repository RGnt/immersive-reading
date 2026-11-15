"""Synthetic series "Loot Saga" for the story pass (spec 22), with scripted answers for both passes.

Volume 1: ch0 Ren takes a goblin-hunt quest and carries an old knife; ch1 he hunts goblins, a sword drops and replaces the knife;
ch2 Ren and Aya clear a dungeon, a dragon drops a flame sword and a shield, the iron sword goes to Aya; ch3 Aya sells it, Ren finds a
ring. Volume 2 ch0: Ren loses the shield.

Entity ids are deterministic: the character pass makes e0001 Ren and e0002 Aya; the story pass then makes e0003.. in reading order.
Event ids too: the character pass records four untyped ("legacy") events v0001-v0004, the story pass continues at v0005.
"""
import json

from saga import FakeLlm

TEXTS = {
    (1, 0): "冒険者のレンは、街のギルドで受付嬢のアヤに声をかけられた。\nレンは古いナイフを腰に差していた。\nアヤは「ゴブリン討伐の依頼があります」と言った。\n",
    (1, 1): "レンは森でゴブリンを二十匹倒した。\nゴブリンの群れの中から、鉄の剣が落ちた。\nレンは鉄の剣を手に取り、古いナイフの代わりに装備した。\n",
    (1, 2): "レンとアヤは古い迷宮に挑んだ。\n迷宮の主である竜を倒し、迷宮を攻略した。\n竜の宝箱から炎の剣と竜の盾が出た。\nレンは炎の剣と竜の盾を装備し、鉄の剣をアヤに渡した。\n",
    (1, 3): "アヤは鉄の剣を売ってしまった。\nレンは森で黒い指輪を拾い、指にはめた。\n",
    (2, 0): "数年後、レンは竜の盾を川に落として失くしてしまった。\n",
}
TITLES = {(1, 0): "依頼", (1, 1): "初めての討伐", (1, 2): "迷宮攻略", (1, 3): "指輪", (2, 0): "数年後"}


def book_text():
    volumes = {}
    for (v, c), text in sorted(TEXTS.items()):
        volumes.setdefault(v, []).append({"index": c, "title": TITLES[(v, c)], "text": text})
    return {"series_key": "loot-saga", "title": "Loot Saga", "language": "ja",
            "volumes": [{"volume": v, "book_id": f"book-{v}", "title": f"Vol {v}", "series_index": v, "chapters": chs}
                        for v, chs in sorted(volumes.items())]}


def q(text):
    return {"quote": text}


# ---------------------------------------------------------------- character pass
CHARACTER_ANSWERS = {
    (1, 0): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "character", "names": [{"name": "レン", "type": "canonical"}], "evidence": [q("冒険者のレンは")]},
        {"op": "add_entity", "ref": "n2", "kind": "character", "names": [{"name": "アヤ", "type": "canonical"}], "evidence": [q("受付嬢のアヤに")]},
        {"op": "add_event", "summary": "レンはアヤに声をかけられた", "participants": [{"entity": "n1", "role": ""}, {"entity": "n2", "role": ""}],
         "evidence": [q("アヤに声をかけられた")]},
    ]},
    (1, 1): {"ops": [
        {"op": "add_event", "summary": "レンはゴブリンを倒した", "participants": [{"entity": "e0001", "role": ""}], "evidence": [q("ゴブリンを二十匹倒した")]},
    ]},
    (1, 2): {"ops": [
        {"op": "add_event", "summary": "二人は迷宮に挑んだ", "participants": [{"entity": "e0001", "role": ""}, {"entity": "e0002", "role": ""}],
         "evidence": [q("古い迷宮に挑んだ")]},
    ]},
    (1, 3): {"ops": [
        {"op": "add_event", "summary": "アヤは鉄の剣を売った", "participants": [{"entity": "e0002", "role": ""}], "evidence": [q("鉄の剣を売ってしまった")]},
    ]},
    (2, 0): {"ops": []},
}

# ---------------------------------------------------------------- story pass
STORY_ANSWERS = {
    (1, 0): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "item", "names": [{"name": "古いナイフ", "type": "canonical"}], "evidence": [q("古いナイフを腰に差していた")]},
        {"op": "add_event", "kind": "quest", "title": "ゴブリン討伐の依頼", "summary": "レンはゴブリン討伐の依頼を知らされた",
         "participants": [{"entity": "e0001", "role": "受注者"}, {"entity": "e0002", "role": "依頼人"}],
         "evidence": [q("ゴブリン討伐の依頼があります")]},
        {"op": "item_state", "item": "n1", "holder": "e0001", "state": "equipped", "slot": "weapon", "evidence": [q("古いナイフを腰に差していた")]},
        {"op": "set_attribute", "entity": "n1", "key": "appearance", "value": "古びたナイフ", "evidence": [q("古いナイフ")]},
    ]},
    (1, 1): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "creature", "names": [{"name": "ゴブリン", "type": "canonical"}], "evidence": [q("森でゴブリンを二十匹倒した")]},
        {"op": "add_entity", "ref": "n2", "kind": "item", "names": [{"name": "鉄の剣", "type": "canonical"}], "evidence": [q("鉄の剣が落ちた")]},
        {"op": "add_event", "kind": "hunt", "title": "ゴブリン狩り", "summary": "レンは森でゴブリンを二十匹倒した", "parent": "v0005",
         "participants": [{"entity": "e0001", "role": "討伐者"}, {"entity": "n1", "role": "獲物"}], "evidence": [q("森でゴブリンを二十匹倒した")]},
        {"op": "add_event", "kind": "item", "title": "鉄の剣が落ちた", "summary": "ゴブリンの群れから鉄の剣が落ちた",
         "participants": [{"entity": "n2", "role": "dropped"}, {"entity": "e0001", "role": "拾った"}], "evidence": [q("鉄の剣が落ちた")]},
        {"op": "item_state", "item": "n2", "holder": "e0001", "state": "equipped", "slot": "weapon",
         "evidence": [q("古いナイフの代わりに装備した")]},
    ]},
    (1, 2): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "location", "names": [{"name": "古い迷宮", "type": "canonical"}], "evidence": [q("古い迷宮に挑んだ")]},
        {"op": "add_entity", "ref": "n2", "kind": "creature", "names": [{"name": "竜", "type": "canonical"}], "evidence": [q("迷宮の主である竜を倒し")]},
        {"op": "add_entity", "ref": "n3", "kind": "item", "names": [{"name": "炎の剣", "type": "canonical"}], "evidence": [q("炎の剣と竜の盾が出た")]},
        {"op": "add_entity", "ref": "n4", "kind": "item", "names": [{"name": "竜の盾", "type": "canonical"}], "evidence": [q("炎の剣と竜の盾が出た")]},
        {"op": "add_event", "ref": "d", "kind": "dungeon", "title": "古い迷宮の攻略", "summary": "レンとアヤは古い迷宮を攻略した", "location": "n1",
         "participants": [{"entity": "e0001", "role": ""}, {"entity": "e0002", "role": ""}], "evidence": [q("迷宮を攻略した")]},
        {"op": "add_event", "kind": "battle", "title": "竜との戦い", "summary": "二人は迷宮の主の竜を倒した", "parent": "d", "location": "n1",
         "participants": [{"entity": "e0001", "role": ""}, {"entity": "e0002", "role": ""}, {"entity": "n2", "role": "敵"}],
         "evidence": [q("迷宮の主である竜を倒し")]},
        {"op": "add_event", "kind": "item", "title": "竜の宝箱", "summary": "竜の宝箱から炎の剣と竜の盾が出た", "parent": "d",
         "participants": [{"entity": "n3", "role": "dropped"}, {"entity": "n4", "role": "dropped"}], "evidence": [q("炎の剣と竜の盾が出た")]},
        {"op": "item_state", "item": "n3", "holder": "e0001", "state": "equipped", "slot": "weapon", "evidence": [q("レンは炎の剣と竜の盾を装備し")]},
        {"op": "item_state", "item": "n4", "holder": "e0001", "state": "equipped", "slot": "shield", "evidence": [q("レンは炎の剣と竜の盾を装備し")]},
        {"op": "item_state", "item": "e0005", "holder": "e0002", "state": "owned", "evidence": [q("鉄の剣をアヤに渡した")]},
    ]},
    (1, 3): {"ops": [
        {"op": "add_entity", "ref": "n1", "kind": "item", "names": [{"name": "黒い指輪", "type": "canonical"}], "evidence": [q("黒い指輪を拾い")]},
        {"op": "item_state", "item": "e0005", "state": "lost", "evidence": [q("鉄の剣を売ってしまった")]},
        {"op": "item_state", "item": "n1", "holder": "e0001", "state": "equipped", "slot": "accessory", "evidence": [q("指にはめた")]},
    ]},
    (2, 0): {"ops": [
        {"op": "item_state", "item": "e0009", "state": "lost", "evidence": [q("竜の盾を川に落として失くしてしまった")]},
    ]},
}


class StoryLlm(FakeLlm):
    """Scripted model for both passes; the story pass is recognised by its system prompt."""

    def __init__(self, characters=None, story=None, *, fail=None, story_fail=None):
        super().__init__(CHARACTER_ANSWERS if characters is None else characters, fail=fail)
        self.story_answers = STORY_ANSWERS if story is None else story
        self.story_fail = story_fail or {}
        self.story_calls = []

    def chat_json(self, system, user, **options):
        if "STORY PASS" not in system:
            return super().chat_json(system, user, **options)
        import re
        match = re.search(r"VOLUME (\d+) CHAPTER (\d+)", user)
        key = (int(match.group(1)), int(match.group(2)))
        self.story_calls.append({"system": system, "user": user, "key": key})
        failure = self.story_fail.get(key)
        if failure is not None:
            if isinstance(failure, Exception):
                raise failure
            return failure(len([c for c in self.story_calls if c["key"] == key]))
        return json.loads(json.dumps(self.story_answers.get(key, {"ops": []})))
