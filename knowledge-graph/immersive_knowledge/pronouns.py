"""Japanese personal pronouns, which are never the name of a character.

A first-person story says わたし / 僕 / 俺 all the way through, and a model that is not told otherwise records "わたし" as a character
(the extraction of a whole series did: one entity called わたし held 2,392 event roles and 539 relations while the protagonist
had a separate entity). A pronoun stands for someone; it is **resolved to that person**: the narrator in first-person narration, the
speaker inside quotation marks, the last person mentioned for あの人 / 彼 / 彼女. This module only recognises pronouns; deciding who
they stand for is the model's job (its prompt says how, and the snapshot names the narrator), and ``ops`` refuses to store one as a name.

Only whole names are matched: "私の友達" is not a pronoun, and a real name that merely contains one (キミコ) is untouched.
"""
import unicodedata

FIRST_PERSON = frozenset("""
私 わたし ワタシ わたくし ワタクシ 僕 ぼく ボク 俺 おれ オレ 俺様 おれさま あたし アタシ あたい アタイ あっし わし ワシ うち ウチ
自分 じぶん 余 我 われ 吾 吾輩 わがはい 拙者 せっしゃ おいら オイラ おら オラ わっち 手前 てまえ 小生 しょうせい 当方 とうほう
私達 私たち わたしたち わたくしたち 僕ら 僕達 僕たち ぼくら 俺ら 俺達 俺たち おれたち おれら あたしたち 我々 われわれ 我ら われら
""".split())

SECOND_PERSON = frozenset("""
あなた アナタ 貴方 貴女 あんた アンタ お前 おまえ オマエ 君 きみ キミ 貴様 きさま お主 おぬし 汝 なんじ そなた おぬしら てめえ てめぇ 手前
あなたたち あなた達 お前ら おまえら お前たち お前達 君たち 君達 きみたち あんたたち あんたら 貴方達 貴方たち
""".split())

THIRD_PERSON = frozenset("""
彼 かれ カレ 彼女 かのじょ カノジョ 彼ら 彼等 かれら 彼女ら 彼女たち 彼女達 あいつ アイツ こいつ コイツ そいつ ソイツ やつ 奴 あの人 この人 その人
あの方 このかた あのかた そのかた その方 この方 あいつら こいつら そいつら あの子 この子 その子 あのひと このひと そのひと
""".split())

_ALL = {}
for _category, _names in (("first", FIRST_PERSON), ("second", SECOND_PERSON), ("third", THIRD_PERSON)):
    for _name in _names:
        _ALL.setdefault("".join(unicodedata.normalize("NFKC", _name).split()).casefold(), _category)


def _key(name):
    return "".join(unicodedata.normalize("NFKC", name or "").split()).casefold()


def pronoun_kind(name):
    """``"first"``, ``"second"`` or ``"third"`` when the whole name is a personal pronoun, else ``None``."""
    return _ALL.get(_key(name))


def is_pronoun(name):
    return pronoun_kind(name) is not None


def pronoun_names(op, kind_of=None):
    """Names an operation would store for a **character** that are pronouns (``add_entity`` names, ``add_name`` name).

    ``kind_of(entity_id)`` gives the kind of an existing entity (None when unknown, e.g. a ref made in the same answer, which is
    treated as a character); entities that are not characters (an item called 余) are not checked.
    """
    if not isinstance(op, dict):
        return []
    names = []
    if op.get("op") == "add_entity" and op.get("kind") == "character" and isinstance(op.get("names"), list):
        for item in op["names"]:
            name = item.get("name") if isinstance(item, dict) else item
            if isinstance(name, str) and is_pronoun(name):
                names.append(name.strip())
    elif op.get("op") == "add_name" and isinstance(op.get("name"), str) and is_pronoun(op["name"]):
        kind = kind_of(op.get("entity")) if kind_of else None
        if kind in (None, "character"):
            names.append(op["name"].strip())
    return names
