"""Causal, incremental builder (spec 18, section 4.3).

Chapters are processed **in reading order, one request per chapter** (long chapters in parts). Each request contains only:
the gated snapshot as of the start of the text being processed (everything from earlier text) and that text. The model can
therefore never see a later chapter, which removes the main leak path of "read the whole book, then tag facts by chapter".
Every answer goes through ``ops.apply_ops`` (verbatim evidence, known ids) before it touches the store.

The server's real limits are discovered, not assumed (spike K0 measured an advertised 262,144-token context but a real budget of
8,241 tokens per request): a prompt that is too big first gets a smaller snapshot, then the text is split; an answer that is cut
off at the output limit makes the text be split. Nothing of a rejected or truncated answer is ever applied.

A second, optional **story pass** (spec 22) runs after the character pass over the same chapters: typed events and item holdings.
It has its own prompt, build state and rollback, so it can be added to an already built series without redoing the characters, and a
failing story chapter never blocks the characters. It sees the entities the character pass found up to the end of the chapter
being processed (the model reads that chapter anyway) and the story facts of earlier chapters, nothing later.
"""
import hashlib
import json
from dataclasses import dataclass
from datetime import datetime, timezone

from .evidence import find_word, normalize
from .gate import Gate
from .llm import LlmError
from .ops import ChapterContext, apply_ops, PASS_CHARACTERS, PASS_STORY
from .position import StoryPosition, as_of, chapter_key
from .profile import PROMPTS_DIR
from .pronouns import is_pronoun, pronoun_names
from .templates import load_directory, render

MAX_SPLIT_DEPTH = 3
MIN_SPLIT_CHARS = 400
MAX_SNAPSHOT_LEVEL = 2

# Progressive snapshot shrinking: (entities, relations, events, values per attribute key); None = take it from the configuration.
SNAPSHOT_LEVELS = ((None, None, None, None), (120, 100, 15, 1), (50, 40, 6, 0))
# Story pass: (characters, other entities, items, recent events); None = take it from the configuration.
STORY_SNAPSHOT_LEVELS = ((None, None, None, None), (60, 30, 40, 5), (25, 12, 20, 0))


@dataclass
class BuildConfig:
    model: str = ""
    output_language: str = "Japanese"
    max_input_chars: int = 90_000       # chapter text per request, an upper bound
    max_ops: int = 120
    min_quote_chars: int = 4
    snapshot_entities: int = 300
    snapshot_relations: int = 300
    snapshot_events: int = 40
    snapshot_values_per_key: int = 3
    context_tokens: int = 0             # the server's real limit (prompt + generation); 0 = unknown, learned from its errors
    chars_per_token: float = 1.6        # conservative for Japanese (measured about 1.8)
    story_pass: bool = False            # events and item holdings (spec 22); the CLI and the plugin switch it on
    story_snapshot_characters: int = 150
    story_snapshot_others: int = 60     # places, groups, monsters
    story_snapshot_items: int = 80
    story_snapshot_events: int = 12


@dataclass
class BuildReport:
    chapters_built: int = 0
    chapters_skipped: int = 0
    ops_applied: int = 0
    ops_rejected: int = 0
    failed: dict = None                  # {"volume", "chapter", "reason"} when the build stopped early
    requests: int = 0
    splits: int = 0                      # pieces split in two (answer cut off, or prompt too big)
    context_tokens: int = 0              # the real limit learned from the server, 0 if never needed
    story_built: int = 0
    story_skipped: int = 0
    story_ops_applied: int = 0
    story_ops_rejected: int = 0
    pronoun_retries: int = 0             # answers that named a character by a pronoun and were asked once more


def text_sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def split_parts(text, max_chars):
    """``[(offset, part_text)]``: whole text if it fits, else cut at line ends (or sentence ends), keeping the separator."""
    if len(text) <= max_chars:
        return [(0, text)]
    parts, start = [], 0
    while start < len(text):
        end = min(start + max_chars, len(text))
        if end < len(text):
            cut = text.rfind("\n", start, end)
            if cut <= start + max_chars // 2:
                cut = max(text.rfind("。", start, end), text.rfind("」", start, end), cut)
            if cut > start:
                end = cut + 1
        parts.append((start, text[start:end]))
        start = end
    return parts


def split_in_half(text):
    """Two non-empty pieces cut near the middle at a line end (else a sentence end, else exactly in the middle)."""
    middle, low, high = len(text) // 2, len(text) // 4, len(text) * 3 // 4
    candidates = [i for i in range(low, high) if text[i] == chr(10)] or [i for i in range(low, high) if text[i] == "。"]
    cut = min(candidates, key=lambda i: abs(i - middle)) + 1 if candidates else middle
    return text[:cut], text[cut:]


def _pronoun_only(entity):
    """A character of an earlier build that is called only by a pronoun ("わたし"): the model must not be offered it as someone to add to."""
    return entity.kind == "character" and all(is_pronoun(n["name"]) for n in entity.names)


def narrator_of(view, shown=None):
    """The character who narrated most recently as of this view (id), if the model said so and the snapshot shows them."""
    best = None
    for eid, entity in view.entities.items():
        for a in entity.attributes:
            if a["key"] == "narrator" and (best is None or (a["from_key"], eid) > best[0]):
                best = ((a["from_key"], eid), eid)
    return best[1] if best and (shown is None or best[1] in shown) else None


def causal_snapshot(view, config, level=0):
    """Compact JSON of everything visible at ``view.position``: what the model is allowed to know. Built from the gate only."""
    entities_cap, relations_cap, events_cap, values_cap = (
        default if override is None else override
        for default, override in zip((config.snapshot_entities, config.snapshot_relations, config.snapshot_events,
                                      config.snapshot_values_per_key), SNAPSHOT_LEVELS[level]))
    ranked = sorted((e for e in view.entities if not _pronoun_only(view.entities[e])), key=lambda e: (-view.richness(e), e))[:entities_cap]
    same = {}
    for a, b in view.links:
        same.setdefault(a, []).append(b)
        same.setdefault(b, []).append(a)
    entities = []
    for eid in sorted(ranked):
        entity = view.entities[eid]
        attributes = {}
        for a in entity.attributes:
            attributes.setdefault(a["key"], []).append(a["value"])
        record = {"id": eid, "kind": entity.kind, "names": [n["name"] for n in entity.names]}
        if values_cap:
            record["attributes"] = {k: v[-values_cap:] for k, v in attributes.items() if k not in ("speech_sample", "narrator")}
        if eid in same:
            record["same_as"] = sorted(same[eid])
        entities.append(record)
    shown = {e["id"] for e in entities}
    relations = [{"id": r["id"], "src": r["src"], "dst": r["dst"], "type": r["type"], "directed": r["directed"], "summary": r["summary"]}
                 for r in view.relations if r["src"] in shown and r["dst"] in shown][:relations_cap]
    events = [{"id": e["id"], "summary": e["summary"], "participants": [p["entity"] for p in e["participants"]]}
              for e in view.events][-events_cap:] if events_cap else []
    snapshot = {"as_of": str(view.position)}
    narrator = narrator_of(view, shown)
    if narrator:
        snapshot["narrator"] = narrator         # who "わたし" means in first-person narration
    snapshot.update({"entities": entities, "relations": relations, "recent_events": events})
    return json.dumps(snapshot, ensure_ascii=False, separators=(",", ":"))


def story_snapshot(view, config, level=0):
    """Compact JSON of what the story pass may know at ``view.position``: entities with ids, items with their holders, recent events.

    Built from the gate only. Characters first (by how much the reader has seen of them), then places, groups and monsters.
    """
    characters_cap, others_cap, items_cap, events_cap = (
        default if override is None else override
        for default, override in zip((config.story_snapshot_characters, config.story_snapshot_others, config.story_snapshot_items,
                                      config.story_snapshot_events), STORY_SNAPSHOT_LEVELS[level]))

    def record(eid):
        return {"id": eid, "names": [n["name"] for n in view.entities[eid].names[:2]]}

    by_kind = {}
    for eid, entity in view.entities.items():
        by_kind.setdefault(entity.kind, []).append(eid)
    rank = lambda e: (-view.richness(e), e)                                       # noqa: E731
    characters = sorted(sorted([e for e in by_kind.get("character", []) if not _pronoun_only(view.entities[e])], key=rank)[:characters_cap])
    others = sorted(sorted([e for k, ids in by_kind.items() if k not in ("character", "item") for e in ids], key=rank)[:others_cap])
    holder_of = {h["item"]: h for h in view.holdings}
    items = sorted(by_kind.get("item", []), key=lambda e: (e not in holder_of, -view.entities[e].names[0]["from_key"], e))[:items_cap]
    item_records = []
    for eid in sorted(items):
        entry = record(eid)
        if eid in holder_of:
            h = holder_of[eid]
            entry.update({"held_by": h["holder"], "state": h["state"], **({"slot": h["slot"]} if h["slot"] else {})})
        item_records.append(entry)
    # typed events only: they are the story pass's own (earlier chapters and earlier parts of this chapter); untyped ones are
    # the character pass's and are replaced once this chapter's story pass has recorded its events
    events = [{"id": e["id"], "kind": e["kind"], "title": e["title"]} for e in view.events if e["kind"]][-events_cap:] if events_cap else []
    snapshot = {"as_of": str(view.position)}
    narrator = narrator_of(view, set(characters))
    if narrator:
        snapshot["narrator"] = narrator
    snapshot.update({"characters": [record(e) for e in characters], "others": [dict(record(e), kind=view.entities[e].kind) for e in others],
                     "items": item_records, "recent_events": events})
    return json.dumps(snapshot, ensure_ascii=False, separators=(",", ":"))


class Builder:
    def __init__(self, store, llm, config=None, *, templates=None, progress=None):
        self.store, self.llm = store, llm
        self.config = config or BuildConfig()
        self.templates = templates or load_directory(PROMPTS_DIR)
        self.progress = progress or (lambda event: None)
        self.gate = Gate(store)
        self.context_tokens = self.config.context_tokens
        self.snapshot_level = 0             # character pass: how far the snapshot had to shrink for the server
        self.story_snapshot_level = 0       # the same for the story pass
        self._chapters = []
        self._normalized = {}

    def _emit(self, event, **data):
        self.progress({"event": event, **data})

    def part_chars(self):
        """Text per request: the configured maximum, reduced to a quarter of the real context once that is known."""
        cfg = self.config
        if not self.context_tokens:
            return cfg.max_input_chars
        return max(MIN_SPLIT_CHARS, min(cfg.max_input_chars, int(0.25 * self.context_tokens * cfg.chars_per_token)))

    def run(self, book_text, *, resume=True, from_position=None):
        """Build (or continue) the knowledge base. Stops at the first chapter that fails: later chapters depend on it."""
        report = BuildReport(context_tokens=self.context_tokens)
        self._chapters = [(vol["volume"], ch["index"], ch.get("title", ""), ch["text"])
                          for vol in sorted(book_text["volumes"], key=lambda v: v["volume"])
                          for ch in sorted(vol["chapters"], key=lambda c: c["index"])]
        self._normalized = {}
        forced_from = from_position.chapter_start().key if from_position else None
        dirty = False

        for volume, chapter, title, text in self._chapters:
            key, sha = chapter_key(volume, chapter), text_sha(text)
            state = self.store.chapter_state(volume, chapter)
            unchanged = state is not None and state["status"] == "built" and state["text_sha"] == sha
            if not dirty and resume and unchanged and (forced_from is None or key < forced_from):
                report.chapters_skipped += 1
                self._emit("chapter_skipped", volume=volume, chapter=chapter)
                continue
            if not dirty:
                # Everything from here on was (or would be) built on facts that are about to change: start over from this chapter.
                self.store.rollback_from(key)
                dirty = True

            self._emit("chapter_start", volume=volume, chapter=chapter, title=title)
            outcome = self._build_chapter(volume, chapter, title, text, sha, report)
            if outcome is not None:
                report.failed = {"volume": volume, "chapter": chapter, "reason": outcome}
                self._emit("chapter_failed", volume=volume, chapter=chapter, reason=outcome)
                return report

        if self.config.story_pass:
            self._run_story(report, resume=resume, forced_from=forced_from)
        report.context_tokens = self.context_tokens
        self._emit("build_done", **{k: v for k, v in report.__dict__.items() if k != "failed"})
        return report

    def _run_story(self, report, *, resume, forced_from):
        """The story pass over every chapter the character pass built. Stops at the first chapter that fails (later story state
        depends on it); the character data is never touched. A chapter is up to date when its story state is built for the same text."""
        dirty = False
        for volume, chapter, title, text in self._chapters:
            key, sha = chapter_key(volume, chapter), text_sha(text)
            built = self.store.chapter_state(volume, chapter)
            if built is None or built["status"] != "built":
                break                                   # the character pass did not get this far
            state = self.store.story_state(volume, chapter)
            current = state is not None and state["status"] == "built" and state["text_sha"] == sha
            if not dirty and resume and current and (forced_from is None or key < forced_from):
                report.story_skipped += 1
                self._emit("story_skipped", volume=volume, chapter=chapter)
                continue
            if not dirty:
                self.store.rollback_story_from(key)    # everything after this chapter was built on story facts that are about to change
                dirty = True
            self._emit("story_start", volume=volume, chapter=chapter, title=title)
            outcome = self._build_chapter(volume, chapter, title, text, sha, report, PASS_STORY)
            if outcome is not None:
                report.failed = {"volume": volume, "chapter": chapter, "reason": outcome, "pass": PASS_STORY}
                self._emit("story_failed", volume=volume, chapter=chapter, reason=outcome)
                return

    # ------------------------------------------------------------------
    def _build_chapter(self, volume, chapter, title, text, sha, report, kind=PASS_CHARACTERS):
        cfg = self.config
        story = kind == PASS_STORY
        applied = rejected = 0
        parts = split_parts(text, self.part_chars()) if text.strip() else []
        model = cfg.model or getattr(self.llm, "model", "")
        try:
            for index, (offset, part_text) in enumerate(parts):
                label = f"(part {index + 1} of {len(parts)})" if len(parts) > 1 else ""
                a, r = self._run_part(volume, chapter, title, offset, part_text, label, report, kind=kind)
                applied, rejected = applied + a, rejected + r
        except LlmError as error:
            if error.fatal:
                raise
            error_text = str(error) + (f" | end of the unusable answer: {error.raw}" if getattr(error, "raw", "") else "")
            if story:
                self.store.set_story_state(volume, chapter, text_sha=sha, status="failed", model=model, ops_applied=applied,
                                           ops_rejected=rejected, error=error_text)
            else:
                self.store.set_chapter_state(volume, chapter, title=title, text_sha=sha, status="failed", model=model,
                                             ops_applied=applied, ops_rejected=rejected, error=error_text)
            return str(error)

        built_at = datetime.now(timezone.utc).isoformat(timespec="seconds")
        if story:
            self.store.set_story_state(volume, chapter, text_sha=sha, status="built", model=model, ops_applied=applied,
                                       ops_rejected=rejected, built_at=built_at)
            replaced = self.store.replace_untyped_events(chapter_key(volume, chapter))
            self.store.conn.commit()
            # a monster or item the reader met as an ordinary word before the model turned it into an entity is no secret (scanner semantics)
            self._index_sightings(volume, chapter)
            report.story_built += 1
            report.story_ops_applied += applied
            report.story_ops_rejected += rejected
            self._emit("story_done", volume=volume, chapter=chapter, title=title, ops_applied=applied, ops_rejected=rejected,
                       parts=max(1, len(parts)), replaced_events=replaced)
            return None
        self.store.set_chapter_state(volume, chapter, title=title, text_sha=sha, status="built", model=model,
                                     ops_applied=applied, ops_rejected=rejected, built_at=built_at)
        self._index_sightings(volume, chapter)
        report.chapters_built += 1
        report.ops_applied += applied
        report.ops_rejected += rejected
        self._emit("chapter_done", volume=volume, chapter=chapter, title=title, ops_applied=applied, ops_rejected=rejected,
                   parts=max(1, len(parts)))
        return None

    def _run_part(self, volume, chapter, title, offset, part_text, label, report, depth=0, kind=PASS_CHARACTERS):
        """One request for one piece of text, adapting to what the server can really take.

        * prompt too big for the server's budget: learn the limit, then try a smaller snapshot, then split the text;
        * answer cut off at the output limit: split the text.
        Nothing of a failed attempt was applied, so retrying is always safe.
        """
        cfg = self.config
        story = kind == PASS_STORY
        make_snapshot = story_snapshot if story else causal_snapshot
        max_level = len(STORY_SNAPSHOT_LEVELS if story else SNAPSHOT_LEVELS) - 1
        level = self.story_snapshot_level if story else self.snapshot_level
        prompt = "story" if story else "knowledge"
        while True:
            # character pass: everything before this piece of text; story pass: everything the character pass found up to the end
            # of this chapter (the model reads the chapter anyway) and the story facts before it. Never anything from a later chapter.
            view = self.gate.view(as_of(volume, chapter + 1) if story else StoryPosition(volume, chapter, offset))
            snapshot = make_snapshot(view, cfg, level)
            system = render(self.templates[f"{prompt}.extract.system"], {"output_language": cfg.output_language, "max_ops": cfg.max_ops})
            user = render(self.templates[f"{prompt}.extract.user"], {
                "volume": volume, "chapter": chapter, "chapter_title": title, "part_label": label,
                "snapshot_json": snapshot, "chapter_text": part_text})
            report.requests += 1
            try:
                answer = self.llm.chat_json(system, user)
                break
            except LlmError as error:
                if error.fatal or not error.truncated:
                    raise
                if error.context_limit and error.context_limit != self.context_tokens:
                    self.context_tokens = error.context_limit
                    self._emit("context_limit_learned", tokens=self.context_tokens)
                if getattr(error, "prompt_too_big", False) and level < max_level:
                    # a smaller snapshot only helps if it really is smaller (an almost empty one cannot shrink further)
                    if len(make_snapshot(view, cfg, level + 1)) < 0.9 * len(snapshot):
                        level += 1
                        if story:
                            self.story_snapshot_level = max(self.story_snapshot_level, level)
                        else:
                            self.snapshot_level = max(self.snapshot_level, level)
                        self._emit("snapshot_shrunk", level=level, **({"pass": PASS_STORY} if story else {}))
                        continue
                if depth < MAX_SPLIT_DEPTH and len(part_text) >= 2 * MIN_SPLIT_CHARS:
                    first, second = split_in_half(part_text)
                    report.splits += 1
                    self._emit("part_split", volume=volume, chapter=chapter, offset=offset, chars=len(part_text))
                    a1, r1 = self._run_part(volume, chapter, title, offset, first, f"{label} (first half)".strip(), report, depth + 1, kind)
                    a2, r2 = self._run_part(volume, chapter, title, offset + len(first), second,
                                            f"{label} (second half)".strip(), report, depth + 1, kind)
                    return a1 + a2, r1 + r2
                raise

        ops = answer.get("ops") if isinstance(answer, dict) else None
        if not isinstance(ops, list):
            raise LlmError("the model's answer has no 'ops' list", fatal=False)
        ops = self._without_pronoun_names(ops, prompt, system, volume, chapter, title, label, snapshot, part_text, report)
        context = (ChapterContext(volume, chapter, part_text, offset, PASS_STORY, as_of(volume, chapter + 1).key) if story
                   else ChapterContext(volume, chapter, part_text, offset))
        results = apply_ops(self.store, context, ops, max_ops=cfg.max_ops, min_quote_chars=cfg.min_quote_chars)
        return sum(r.status == "applied" for r in results), sum(r.status == "rejected" for r in results)

    def _without_pronoun_names(self, ops, prompt, system, volume, chapter, title, label, snapshot, part_text, report):
        """Ask once more when the answer names a character by a pronoun ("わたし").

        ``ops`` would refuse those operations one by one, and every later operation about that character (its ref) would fall with
        them, so the whole narrator's chapter would be lost. One retry tells the model what went wrong. If the retry fails or
        repeats the mistake, the first answer is kept and ``ops`` refuses the offending operations: nothing is guessed or repaired.
        """
        def character_kind(entity):
            row = self.store.query("SELECT kind FROM entities WHERE id=?", (entity,)) if isinstance(entity, str) else []
            return row[0]["kind"] if row else None

        found = sorted({name for op in ops for name in pronoun_names(op, character_kind)})
        if not found:
            return ops
        note = (f"you named a character {', '.join(repr(n) for n in found)}, which is a pronoun and not a name. A pronoun stands for someone: use the id of the person "
                "it stands for (the narrator in first-person narration, the speaker inside quotation marks) and never create or name an entity with it. "
                "Answer again in full.")
        user = render(self.templates[f"{prompt}.extract.user"], {
            "volume": volume, "chapter": chapter, "chapter_title": title, "part_label": label,
            "snapshot_json": snapshot, "chapter_text": part_text, "retry_note": note})
        report.requests += 1
        report.pronoun_retries += 1
        try:
            again = self.llm.chat_json(system, user)
        except LlmError as error:
            if error.fatal:
                raise
            return ops
        retried = again.get("ops") if isinstance(again, dict) else None
        return retried if isinstance(retried, list) else ops

    # ------------------------------------------------------------------
    def _normalized_text(self, volume, chapter, text):
        key = (volume, chapter)
        if key not in self._normalized:
            self._normalized[key] = normalize(text)
        return self._normalized[key]

    def _index_sightings(self, volume, chapter):
        """Record, for each name created by this chapter, where it first occurs in the book text (spec 18, scanner semantics).

        A name the reader has already read as ordinary text (a place or object called "クマ", "ギルド") is not a secret even if the
        model only turned it into an entity later. The scanner uses this position; the gate does not (entities still appear when
        the model established them).
        """
        src = chapter_key(volume, chapter)
        rows = self.store.query("SELECT id, name FROM names WHERE src_key=? AND seen_key IS NULL", (src,))
        if not rows:
            return
        with self.store.transaction():
            for row in rows:
                needle = normalize(row["name"])[0].casefold()
                seen = None
                for v, c, _, text in self._chapters:
                    haystack, origin = self._normalized_text(v, c, text)
                    at = find_word(haystack.casefold(), needle)
                    if at >= 0:
                        seen = StoryPosition(v, c, origin[at + len(needle) - 1] + 1).key
                        break
                    if (v, c) == (volume, chapter):
                        break
                self.store.conn.execute("UPDATE names SET seen_key=? WHERE id=?", (seen, row["id"]))
