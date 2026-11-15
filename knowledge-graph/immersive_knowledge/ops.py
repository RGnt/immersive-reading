"""Operations returned by the model: validation and application (spec 18, FR-11/FR-12, contract 6.2).

Nothing the model says is trusted. An operation is applied only if (1) it has the right shape, (2) every quote it cites is
found verbatim in the text the model was given, (3) every entity it refers to exists and was visible to the model.
Positions come from the evidence, computed here; a fact is as late as the latest quote that supports it.
"""
import re
import unicodedata
from dataclasses import dataclass, field

from .evidence import locate, normalize, MIN_QUOTE_CHARS
from .position import StoryPosition, chapter_key
from .pronouns import is_pronoun
from .store import (ENTITY_KINDS, NAME_TYPES, SINGLE_VALUED_KEYS, MULTI_VALUED_KEYS, ATTRIBUTE_KEYS, EVENT_KINDS, SLOTS,  # noqa: F401
                    HOLDER_KINDS, HOLDING_STATES, ORIGIN_CHARACTERS, ORIGIN_STORY)

PASS_CHARACTERS, PASS_STORY = "characters", "story"
CHARACTER_OPS = ("add_entity", "add_name", "set_attribute", "add_relation", "end_relation", "add_event", "same_as")
STORY_OPS = ("add_entity", "add_name", "set_attribute", "add_event", "item_state")
PASS_OPS = {PASS_CHARACTERS: CHARACTER_OPS, PASS_STORY: STORY_OPS}

_ID = {"e": re.compile(r"^e\d+$"), "r": re.compile(r"^r\d+$"), "v": re.compile(r"^v\d+$")}
_REF = re.compile(r"^[A-Za-z0-9_]{1,24}$")
MAX_EVIDENCE = 3
MIN_ENTITY_QUOTE_CHARS = 2     # a name introduced in a short phrase ("ユナ様") is fine; the quote must contain the name


@dataclass(frozen=True)
class _Evidence:
    found: list          # [(start, end, quote)] verified quotes, absolute offsets in the chapter
    failed: int = 0      # how many quotes could not be verified


class Reject(Exception):
    """An operation that must not be applied; the message is stored in ``ops_log`` and shown in reports."""


PRONOUN_REASON = ("{name!r} is a pronoun, not a name: refer to the person it stands for by id (the narrator in first-person narration, "
                  "the speaker inside quotation marks, the person just mentioned for あの人 / 彼 / 彼女); create an entity only for a name the chapter gives")
PRONOUN_ENTITY_REASON = "{ref} is called only by a pronoun: refer to the person it stands for (the narrator, the speaker) by their own id"


@dataclass(frozen=True)
class ChapterContext:
    """The text the model was shown: one chapter, or one part of a long chapter starting at ``part_offset``.

    ``pass_name`` selects which operations are allowed and how rows are tagged. The **story pass** runs after the character pass
    of the same chapter and may use every entity the character pass found *in this chapter* (it is shown the same text), so its
    ``horizon_key`` is the end of the chapter instead of the start of the part.
    """
    volume: int
    chapter: int
    text: str
    part_offset: int = 0
    pass_name: str = PASS_CHARACTERS
    horizon_key: int = None

    @property
    def src_key(self):
        return chapter_key(self.volume, self.chapter)

    @property
    def part_start_key(self):
        return StoryPosition(self.volume, self.chapter, self.part_offset).key

    @property
    def known_key(self):
        """Entities with ``first_key`` up to here were part of what the model was shown."""
        return self.part_start_key if self.horizon_key is None else self.horizon_key


@dataclass
class OpResult:
    seq: int
    op: object
    status: str            # applied | noop | rejected
    reason: str = ""
    created: list = field(default_factory=list)


def name_key(name):
    """Comparison form of a name: NFKC, no whitespace, case-folded."""
    return "".join(unicodedata.normalize("NFKC", name).split()).casefold()


class _Applier:
    def __init__(self, store, ctx, min_quote_chars):
        self.store, self.ctx, self.min_chars = store, ctx, min_quote_chars
        self.story = ctx.pass_name == PASS_STORY
        self.origin = ORIGIN_STORY if self.story else ORIGIN_CHARACTERS
        self.normalized = normalize(ctx.text)
        self.locals = {}        # ref -> entity id declared earlier in this answer
        self.event_refs = {}    # ref -> event id declared earlier in this answer
        self.names = {}         # (kind, name_key) -> entity id, loaded once and kept current
        named_by = {}
        for row in store.query("SELECT n.name, e.kind, e.id FROM names n JOIN entities e ON e.id = n.entity_id"):
            self.names[(row["kind"], name_key(row["name"]))] = row["id"]
            named_by.setdefault(row["id"], []).append((row["kind"], row["name"]))
        # characters of an earlier build that are called only by a pronoun ("わたし"): nothing may be added to them any more
        self.pronoun_only = {eid for eid, names in named_by.items() if names[0][0] == "character" and all(is_pronoun(n) for _, n in names)}

    # ---------------------------------------------------------------- shared checks
    def _text(self, op, field_name, limit, *, required=True):
        value = op.get(field_name)
        if value is None and not required:
            return ""
        if not isinstance(value, str) or not value.strip():
            raise Reject(f"missing or empty '{field_name}'")
        value = value.strip()
        if len(value) > limit:
            raise Reject(f"'{field_name}' is longer than {limit} characters")
        return value

    def _locate_evidence(self, op, fallback_quote=None, *, min_chars=None):
        """Verified quotes of an operation. At least one must verify; unverifiable ones are dropped and counted.

        A failed quote means the model paraphrased or invented part of its support, so the fact is positioned at the **end of
        the text the model was shown** (the most conservative point inside this chapter part) instead of at its last verified quote.
        """
        raw = op.get("evidence")
        if raw is None and fallback_quote is not None:
            raw = [fallback_quote]
        if not isinstance(raw, list) or not raw:
            raise Reject("no evidence")
        if len(raw) > MAX_EVIDENCE:
            raise Reject(f"more than {MAX_EVIDENCE} evidence quotes")
        found, failed, first_failure = [], 0, ""
        for item in raw:
            quote = item.get("quote") if isinstance(item, dict) else item
            if not isinstance(quote, str) or not quote.strip():
                raise Reject("evidence without a quote")
            loc = locate(self.ctx.text, quote, min_chars=min_chars or self.min_chars, normalized=self.normalized)
            if loc is None:
                failed += 1
                first_failure = first_failure or quote.strip()[:40]
                continue
            found.append((self.ctx.part_offset + loc.start, self.ctx.part_offset + loc.end, quote.strip()))
        if not found:
            raise Reject(f"evidence not found in chapter text (or too short): {first_failure!r}")
        return _Evidence(found, failed)

    def _commit_evidence(self, evidence):
        ids, key = [], 0
        for start, end, quote in evidence.found:
            ids.append(self.store.add_evidence(self.ctx.volume, self.ctx.chapter, start, end, quote, self.origin))
            key = max(key, StoryPosition(self.ctx.volume, self.ctx.chapter, end).key)
        if evidence.failed:
            key = max(key, StoryPosition(self.ctx.volume, self.ctx.chapter, self.ctx.part_offset + len(self.ctx.text)).key)
        return ids, key

    def _require_in_text(self, name, type_):
        """A name the text never uses is invented. Readings (ruby) are not part of the plain text, so they are exempt."""
        if type_ != "reading" and name_key(name) not in self.normalized[0].casefold():
            raise Reject(f"the name {name!r} does not occur in the chapter text")

    @staticmethod
    def _require_quote_with(evidence, names):
        """At least one verified quote must contain one of the names: the quote that introduces the entity."""
        keys = [name_key(n) for n in names]
        if not any(k in normalize(quote)[0].casefold() for _, _, quote in evidence.found for k in keys):
            raise Reject("no evidence quote contains the name")

    def _entity(self, ref):
        if not isinstance(ref, str):
            raise Reject("unknown entity")
        if ref in self.locals:
            return self.locals[ref]
        if _ID["e"].match(ref):
            row = self.store.query("SELECT first_key FROM entities WHERE id=?", (ref,))
            # it must exist AND have been part of what the model was shown
            if row and row[0]["first_key"] <= self.ctx.known_key:
                if ref in self.pronoun_only:
                    raise Reject(PRONOUN_ENTITY_REASON.format(ref=ref))
                return ref
        raise Reject(f"unknown entity {ref!r}")

    def _check_name(self, name, kind, *, entity=None):
        owner = self.names.get((kind, name_key(name)))
        if owner is not None and owner != entity:
            raise Reject(f"name {name!r} already belongs to {owner}; reuse that id (or use same_as)")

    # ---------------------------------------------------------------- operations
    def add_entity(self, op):
        ref = op.get("ref")
        if not isinstance(ref, str) or not _REF.match(ref) or ref in self.locals or _ID["e"].match(ref):
            raise Reject("invalid or repeated 'ref'")
        kind = op.get("kind")
        if kind not in ENTITY_KINDS:
            raise Reject(f"invalid kind {kind!r}")
        if self.story and kind == "character":
            raise Reject("characters are recorded by the character pass; the story pass can only refer to them")
        names = op.get("names")
        if not isinstance(names, list) or not 1 <= len(names) <= 6:
            raise Reject("'names' must list 1 to 6 names")
        cleaned = []
        for item in names:
            name = (item.get("name") if isinstance(item, dict) else item)
            type_ = item.get("type", "canonical") if isinstance(item, dict) else "canonical"
            if not isinstance(name, str) or not name.strip() or len(name.strip()) > 40:
                raise Reject("invalid name")
            if type_ not in NAME_TYPES:
                raise Reject(f"invalid name type {type_!r}")
            cleaned.append((name.strip(), type_))
        if kind == "character":
            # a pronoun is never a name: a character keeps the real names it was given, and one with only pronouns is not recorded
            real = [(n, t) for n, t in cleaned if not is_pronoun(n)]
            if not real:
                raise Reject(PRONOUN_REASON.format(name=cleaned[0][0]))
            cleaned = real
        if len({name_key(n) for n, _ in cleaned}) != len(cleaned):
            raise Reject("the same name is listed twice")

        owners = {self.names[(kind, name_key(n))] for n, _ in cleaned if (kind, name_key(n)) in self.names}
        if len(owners) > 1:
            raise Reject(f"the names belong to different entities ({', '.join(sorted(owners))})")
        if owners:
            return self._reuse_entity(ref, kind, owners.pop(), cleaned, op)

        for name, type_ in cleaned:
            self._require_in_text(name, type_)
        evidence = self._locate_evidence(op, min_chars=MIN_ENTITY_QUOTE_CHARS)
        self._require_quote_with(evidence, [name for name, _ in cleaned])
        evidence_ids, key = self._commit_evidence(evidence)
        eid = self.store.add_entity(kind, key, self.ctx.src_key, self.origin)
        has_canonical = any(t == "canonical" for _, t in cleaned)
        for index, (name, type_) in enumerate(cleaned):
            if not has_canonical and index == 0:
                type_ = "canonical"
            preferred = type_ == "canonical" and not any(t == "canonical" for _, t in cleaned[:index])
            self.store.add_name(eid, name, type_, key, self.ctx.src_key, evidence_ids, preferred=preferred, origin=self.origin)
            self.names[(kind, name_key(name))] = eid
        self.locals[ref] = eid
        return "applied", [eid]

    def _reuse_entity(self, ref, kind, existing, cleaned, op):
        """The model "created" someone who already exists (its snapshot showed them): bind the ref to the existing entity.

        Rejecting it would also reject every operation that uses the ref (the real model does this when a chapter is told from
        another character's point of view). Only names the model was shown are reused; new names become aliases of the existing
        entity and need the usual evidence.
        """
        row = self.store.query("SELECT first_key FROM entities WHERE id=?", (existing,))
        if existing not in self.locals.values() and (not row or row[0]["first_key"] > self.ctx.known_key):
            raise Reject(f"name already belongs to {existing}, which the model was not shown; reuse that id (or use same_as)")
        added = [(n, t) for n, t in cleaned if (kind, name_key(n)) not in self.names]
        if added:
            for name, type_ in added:
                self._require_in_text(name, type_)
            evidence = self._locate_evidence(op, min_chars=MIN_ENTITY_QUOTE_CHARS)
            self._require_quote_with(evidence, [n for n, _ in added])
            evidence_ids, key = self._commit_evidence(evidence)
            for name, type_ in added:
                self.store.add_name(existing, name, "alias" if type_ == "canonical" else type_, key, self.ctx.src_key, evidence_ids,
                                    origin=self.origin)
                self.names[(kind, name_key(name))] = existing
        self.locals[ref] = existing
        return ("applied" if added else "noop"), []

    def add_name(self, op):
        eid = self._entity(op.get("entity"))
        name = self._text(op, "name", 40)
        type_ = op.get("type", "alias")
        if type_ not in NAME_TYPES:
            raise Reject(f"invalid name type {type_!r}")
        kind = self.store.query("SELECT kind FROM entities WHERE id=?", (eid,))[0]["kind"]
        if kind == "character" and is_pronoun(name):
            raise Reject(PRONOUN_REASON.format(name=name))
        owner = self.names.get((kind, name_key(name)))
        if owner == eid:
            return "noop", []
        self._check_name(name, kind, entity=eid)
        self._require_in_text(name, type_)
        evidence = self._locate_evidence(op, min_chars=MIN_ENTITY_QUOTE_CHARS)
        self._require_quote_with(evidence, [name])
        evidence_ids, key = self._commit_evidence(evidence)
        self.store.add_name(eid, name, type_, key, self.ctx.src_key, evidence_ids, origin=self.origin)
        self.names[(kind, name_key(name))] = eid
        return "applied", []

    def set_attribute(self, op):
        eid = self._entity(op.get("entity"))
        key_name = op.get("key")
        if key_name not in ATTRIBUTE_KEYS:
            raise Reject(f"invalid attribute key {key_name!r}")
        if key_name == "narrator":
            # who tells this chapter in the first person; the value is the program's, only the evidence is the model's
            if self.store.query("SELECT kind FROM entities WHERE id=?", (eid,))[0]["kind"] != "character":
                raise Reject("only a character can be the narrator")
            value = f"first-person narrator (volume {self.ctx.volume}, chapter {self.ctx.chapter})"
        else:
            value = self._text(op, "value", 600)
        fallback = value if key_name == "speech_sample" else None
        if key_name == "speech_sample" and locate(self.ctx.text, value, min_chars=self.min_chars, normalized=self.normalized) is None:
            raise Reject("speech_sample is not verbatim chapter text")
        evidence_ids, key = self._commit_evidence(self._locate_evidence(op, fallback_quote=fallback))
        row = self.store.add_attribute(eid, key_name, value, key, self.ctx.src_key, evidence_ids,
                                       single_valued=key_name in SINGLE_VALUED_KEYS, origin=self.origin)
        return ("noop" if row is None else "applied"), []

    def add_relation(self, op):
        src, dst = self._entity(op.get("src")), self._entity(op.get("dst"))
        if src == dst:
            raise Reject("a relation needs two different entities")
        type_ = self._text(op, "type", 40)
        directed = bool(op.get("directed", False))
        summary = self._text(op, "summary", 300, required=False)
        pairs = [(src, dst)] if directed else [(src, dst), (dst, src)]
        for a, b in pairs:
            dup = self.store.query("SELECT id FROM relations WHERE src=? AND dst=? AND type=? AND to_key IS NULL", (a, b, type_))
            if dup:
                return "noop", []
        evidence_ids, key = self._commit_evidence(self._locate_evidence(op))
        rid = self.store.add_relation(src, dst, type_, directed, summary, key, self.ctx.src_key, evidence_ids)
        return "applied", [rid]

    def end_relation(self, op):
        rid = op.get("relation")
        row = self.store.query("SELECT from_key, to_key FROM relations WHERE id=?", (rid,)) if isinstance(rid, str) and _ID["r"].match(rid) else []
        if not row or row[0]["to_key"] is not None or row[0]["from_key"] > self.ctx.part_start_key:
            raise Reject(f"unknown or already closed relation {rid!r}")
        evidence = self._locate_evidence(op)
        key = max(StoryPosition(self.ctx.volume, self.ctx.chapter, end).key for _, end, _ in evidence.found)
        if evidence.failed:
            key = max(key, StoryPosition(self.ctx.volume, self.ctx.chapter, self.ctx.part_offset + len(self.ctx.text)).key)
        if key < row[0]["from_key"]:
            raise Reject("a relation cannot end before it starts")
        self._commit_evidence(evidence)
        self.store.end_relation(rid, key, self.ctx.src_key)
        return "applied", []

    def _event(self, ref):
        """A parent event: declared earlier in this answer, or an earlier event the model was shown."""
        if isinstance(ref, str) and ref in self.event_refs:
            return self.event_refs[ref]
        if isinstance(ref, str) and _ID["v"].match(ref):
            row = self.store.query("SELECT from_key FROM events WHERE id=?", (ref,))
            if row and row[0]["from_key"] <= self.ctx.known_key:
                return ref
        raise Reject(f"unknown parent event {ref!r}")

    def add_event(self, op):
        summary = self._text(op, "summary", 400)
        kind, title, parent, ref = "", "", None, None
        if self.story:
            kind = op.get("kind")
            if kind not in EVENT_KINDS:
                raise Reject(f"invalid event kind {kind!r} (one of {', '.join(EVENT_KINDS)})")
            title = self._text(op, "title", 60)
            ref = op.get("ref")
            if ref is not None and (not isinstance(ref, str) or not _REF.match(ref) or ref in self.event_refs or _ID["v"].match(ref)):
                raise Reject("invalid or repeated event 'ref'")
            if op.get("parent") not in (None, ""):
                parent = self._event(op["parent"])
        participants = op.get("participants", [])
        if not isinstance(participants, list):
            raise Reject("'participants' must be a list")
        resolved = []
        for item in participants:
            if not isinstance(item, dict):
                raise Reject("invalid participant")
            role = item.get("role", "")
            resolved.append((self._entity(item.get("entity")), role if isinstance(role, str) else ""))
        location = op.get("location")
        location_id = self._entity(location) if location else None
        evidence_ids, key = self._commit_evidence(self._locate_evidence(op))
        vid = self.store.add_event(summary, key, location_id, self.ctx.src_key, evidence_ids, resolved,
                                   kind=kind, title=title, parent=parent, origin=self.origin)
        if ref:
            self.event_refs[ref] = vid
        return "applied", [vid]

    def item_state(self, op):
        """Who has an item: ``equipped`` (worn or wielded, with a slot), ``owned`` (has it, does not wear it) or ``lost``
        (nobody has it any more). Newer gear in the same exclusive slot replaces older gear automatically (store.set_holding)."""
        item = self._entity(op.get("item"))
        if self.store.query("SELECT kind FROM entities WHERE id=?", (item,))[0]["kind"] != "item":
            raise Reject("item_state needs an entity of kind item")
        state = op.get("state")
        if state not in HOLDING_STATES + ("lost",):
            raise Reject(f"invalid state {state!r} (equipped, owned or lost)")
        holder = None
        if op.get("holder") not in (None, ""):
            holder = self._entity(op["holder"])
            if self.store.query("SELECT kind FROM entities WHERE id=?", (holder,))[0]["kind"] not in HOLDER_KINDS:
                raise Reject("the holder must be a character, an organization or a place")
        elif state != "lost":
            raise Reject("an item_state needs the holder")
        slot = op.get("slot")
        if state == "equipped" and slot not in SLOTS:
            raise Reject(f"an equipped item needs a slot ({', '.join(SLOTS)})")
        evidence_ids, key = self._commit_evidence(self._locate_evidence(op))
        if state == "lost":
            if not self.store.end_holdings(item, holder, key, self.ctx.src_key):
                raise Reject("nobody is recorded as holding that item at this point")
            return "applied", []
        created = self.store.set_holding(item, holder, state, slot, key, self.ctx.src_key, evidence_ids)
        return ("noop", []) if created is None else ("applied", [])

    def same_as(self, op):
        a, b = self._entity(op.get("a")), self._entity(op.get("b"))
        if a == b:
            raise Reject("same_as needs two different entities")
        dup = self.store.query("SELECT 1 FROM identity_links WHERE (a=? AND b=?) OR (a=? AND b=?)", (a, b, b, a))
        if dup:
            return "noop", []
        evidence_ids, key = self._commit_evidence(self._locate_evidence(op))
        self.store.add_identity_link(a, b, key, self.ctx.src_key, evidence_ids)
        return "applied", []


def apply_ops(store, ctx, raw_ops, *, max_ops=120, min_quote_chars=MIN_QUOTE_CHARS):
    """Validate and apply the operations of one answer. Returns one ``OpResult`` per operation, in order.

    The whole answer is one transaction (a crash leaves no half-applied answer); rejected operations write only to
    ``ops_log``. Nothing is repaired or guessed: an operation is applied exactly as valid, or rejected with a reason.
    """
    results = []
    with store.transaction():
        applier = _Applier(store, ctx, min_quote_chars)
        for seq, op in enumerate(raw_ops):
            if seq >= max_ops:
                result = OpResult(seq, op, "rejected", f"over the limit of {max_ops} operations")
            elif not isinstance(op, dict) or not isinstance(op.get("op"), str):
                result = OpResult(seq, op, "rejected", "malformed operation")
            else:
                allowed = PASS_OPS.get(ctx.pass_name, CHARACTER_OPS)
                handler = getattr(applier, op["op"], None) if op["op"] in allowed else None
                if handler is None:
                    result = OpResult(seq, op, "rejected", f"unknown op {op['op']!r}" if op["op"] not in CHARACTER_OPS + STORY_OPS
                                      else f"the {ctx.pass_name} pass does not accept {op['op']!r}")
                else:
                    # A savepoint per operation: an operation that fails half-way must not leave evidence or rows behind.
                    store.conn.execute("SAVEPOINT op")
                    try:
                        status, created = handler(op)
                        store.conn.execute("RELEASE op")
                        result = OpResult(seq, op, status, created=created)
                    except Reject as reject:
                        store.conn.execute("ROLLBACK TO op")
                        store.conn.execute("RELEASE op")
                        result = OpResult(seq, op, "rejected", str(reject))
            store.log_op(ctx.volume, ctx.chapter, seq, op, result.status, result.reason, applier.origin)
            results.append(result)
    return results
