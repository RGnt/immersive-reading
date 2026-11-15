"""SQLite store (source of truth). Data access only: no gating logic lives here (it lives in ``gate.py``).

One database per series; a standalone book is a series of one. All tables that hold story facts carry ``src_key`` (the key
of the chapter whose processing created the row), which makes "rebuild from chapter N" a plain delete.
"""
import json
import os
import sqlite3
from contextlib import contextmanager

from .position import StoryPosition, VOLUME_SCALE, CHAPTER_SCALE

SCHEMA_VERSION = 2          # 2 = story pass: typed events, item holdings, per-pass origin tags, derived story cards

ENTITY_KINDS = ("character", "location", "organization", "item", "concept", "creature")
NAME_TYPES = ("canonical", "alias", "nickname", "title", "reading")

# Attribute keys. Single-valued keys supersede the previous value; multi-valued keys accumulate.
SINGLE_VALUED_KEYS = ("age", "gender", "appearance", "occupation", "personality", "speech_style", "goal", "status",
                      "background", "motivation", "residence")
# "narrator": this character tells the chapter in the first person, so わたし / 僕 / 俺 in its narration mean them (one row per chapter, the value is
# written by the program). It is not a profile trait; it is how a pronoun is resolved to a person.
MULTI_VALUED_KEYS = ("speech_sample", "ability", "note", "narrator")
ATTRIBUTE_KEYS = SINGLE_VALUED_KEYS + MULTI_VALUED_KEYS

# What happened (story pass). ``other`` exists so that nothing has to be forced into a wrong category.
EVENT_KINDS = ("battle", "hunt", "dungeon", "quest", "item", "growth", "travel", "meeting", "group", "revelation", "death", "other")

# Equipment slots. A slot in EXCLUSIVE_SLOTS holds one item per holder: equipping a new one replaces the old one.
EXCLUSIVE_SLOTS = ("weapon", "shield", "head", "body", "hands", "legs", "feet", "cloak")
SLOTS = EXCLUSIVE_SLOTS + ("accessory", "other")
HOLDING_STATES = ("equipped", "owned")      # ``lost`` is not a state: it closes holdings
HOLDER_KINDS = ("character", "organization", "location")

# Which pass created a row. The story pass can be redone without touching what the character pass built.
ORIGIN_CHARACTERS, ORIGIN_STORY = "c", "s"

SCHEMA = """
CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL);

CREATE TABLE IF NOT EXISTS chapters (
    volume INTEGER NOT NULL, chapter INTEGER NOT NULL, title TEXT NOT NULL DEFAULT '',
    text_sha TEXT NOT NULL, status TEXT NOT NULL, model TEXT NOT NULL DEFAULT '',
    ops_applied INTEGER NOT NULL DEFAULT 0, ops_rejected INTEGER NOT NULL DEFAULT 0,
    error TEXT NOT NULL DEFAULT '', built_at TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (volume, chapter)
);

CREATE TABLE IF NOT EXISTS evidence (
    id INTEGER PRIMARY KEY AUTOINCREMENT, volume INTEGER NOT NULL, chapter INTEGER NOT NULL,
    start INTEGER NOT NULL, end INTEGER NOT NULL, quote TEXT NOT NULL, end_key INTEGER NOT NULL, origin TEXT NOT NULL DEFAULT 'c'
);

CREATE TABLE IF NOT EXISTS ops_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT, volume INTEGER NOT NULL, chapter INTEGER NOT NULL, seq INTEGER NOT NULL,
    op_json TEXT NOT NULL, status TEXT NOT NULL, reason TEXT NOT NULL DEFAULT '', origin TEXT NOT NULL DEFAULT 'c'
);

CREATE TABLE IF NOT EXISTS entities (
    id TEXT PRIMARY KEY, kind TEXT NOT NULL, first_key INTEGER NOT NULL, src_key INTEGER NOT NULL,
    origin TEXT NOT NULL DEFAULT 'c'
);
CREATE TABLE IF NOT EXISTS names (
    id INTEGER PRIMARY KEY AUTOINCREMENT, entity_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    name TEXT NOT NULL, type TEXT NOT NULL, from_key INTEGER NOT NULL, src_key INTEGER NOT NULL,
    evidence_ids TEXT NOT NULL DEFAULT '[]', preferred INTEGER NOT NULL DEFAULT 0,
    seen_key INTEGER,             -- where the name first occurs in the book text (scanner only); NULL = unknown
    origin TEXT NOT NULL DEFAULT 'c'
);
CREATE TABLE IF NOT EXISTS attributes (
    id INTEGER PRIMARY KEY AUTOINCREMENT, entity_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    key TEXT NOT NULL, value TEXT NOT NULL, from_key INTEGER NOT NULL, to_key INTEGER, to_src INTEGER,
    src_key INTEGER NOT NULL, evidence_ids TEXT NOT NULL DEFAULT '[]', origin TEXT NOT NULL DEFAULT 'c'
);
CREATE TABLE IF NOT EXISTS relations (
    id TEXT PRIMARY KEY, src TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    dst TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, type TEXT NOT NULL, directed INTEGER NOT NULL DEFAULT 0,
    summary TEXT NOT NULL DEFAULT '', from_key INTEGER NOT NULL, to_key INTEGER, to_src INTEGER,
    src_key INTEGER NOT NULL, evidence_ids TEXT NOT NULL DEFAULT '[]'
);
CREATE TABLE IF NOT EXISTS events (
    id TEXT PRIMARY KEY, summary TEXT NOT NULL, from_key INTEGER NOT NULL, location TEXT,
    src_key INTEGER NOT NULL, evidence_ids TEXT NOT NULL DEFAULT '[]',
    kind TEXT NOT NULL DEFAULT '', title TEXT NOT NULL DEFAULT '', parent TEXT, origin TEXT NOT NULL DEFAULT 'c'
);
CREATE TABLE IF NOT EXISTS event_participants (
    event_id TEXT NOT NULL REFERENCES events(id) ON DELETE CASCADE,
    entity_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, role TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS identity_links (
    id INTEGER PRIMARY KEY AUTOINCREMENT, a TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    b TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE, from_key INTEGER NOT NULL, src_key INTEGER NOT NULL,
    evidence_ids TEXT NOT NULL DEFAULT '[]'
);
-- Who has which item, over time (story pass). One open holding per item; one equipped item per holder and exclusive slot.
CREATE TABLE IF NOT EXISTS holdings (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    item_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    holder_id TEXT NOT NULL REFERENCES entities(id) ON DELETE CASCADE,
    state TEXT NOT NULL, slot TEXT NOT NULL DEFAULT '',
    from_key INTEGER NOT NULL, to_key INTEGER, to_src INTEGER, end_reason TEXT NOT NULL DEFAULT '',
    replaces TEXT NOT NULL DEFAULT '',       -- item this one replaced in the slot (set when it was equipped)
    replaced_by TEXT NOT NULL DEFAULT '',    -- item that replaced this one (set when this holding ended as 'replaced')
    src_key INTEGER NOT NULL, evidence_ids TEXT NOT NULL DEFAULT '[]'
);
CREATE TABLE IF NOT EXISTS story_chapters (
    volume INTEGER NOT NULL, chapter INTEGER NOT NULL, text_sha TEXT NOT NULL, status TEXT NOT NULL, model TEXT NOT NULL DEFAULT '',
    ops_applied INTEGER NOT NULL DEFAULT 0, ops_rejected INTEGER NOT NULL DEFAULT 0, error TEXT NOT NULL DEFAULT '',
    built_at TEXT NOT NULL DEFAULT '', PRIMARY KEY (volume, chapter)
);
-- Derived (rebuilt by ``snapshots``): one row per event / item and *change*, valid from the boundary (volume, chapter) on.
CREATE TABLE IF NOT EXISTS story_event_cards (
    event_id TEXT NOT NULL, volume INTEGER NOT NULL, chapter INTEGER NOT NULL, sort_key INTEGER NOT NULL, card_json TEXT NOT NULL,
    PRIMARY KEY (event_id, volume, chapter)
);
CREATE TABLE IF NOT EXISTS story_item_cards (
    item_id TEXT NOT NULL, volume INTEGER NOT NULL, chapter INTEGER NOT NULL, sort_key INTEGER NOT NULL, card_json TEXT NOT NULL,
    PRIMARY KEY (item_id, volume, chapter)
);
CREATE TABLE IF NOT EXISTS profile_snapshots (
    character_id TEXT NOT NULL, volume INTEGER NOT NULL, chapter INTEGER NOT NULL,
    display_name TEXT NOT NULL, prominence INTEGER NOT NULL, profile_json TEXT NOT NULL, block_text TEXT NOT NULL,
    PRIMARY KEY (character_id, volume, chapter)
);
CREATE TABLE IF NOT EXISTS profile_snapshot_members (
    character_id TEXT NOT NULL, volume INTEGER NOT NULL, chapter INTEGER NOT NULL, member_id TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_snapshot_member ON profile_snapshot_members(member_id, volume, chapter);
CREATE INDEX IF NOT EXISTS idx_names_entity ON names(entity_id, from_key);
CREATE INDEX IF NOT EXISTS idx_attr_entity ON attributes(entity_id, key);
CREATE INDEX IF NOT EXISTS idx_rel_src ON relations(src);
CREATE INDEX IF NOT EXISTS idx_rel_dst ON relations(dst);
CREATE INDEX IF NOT EXISTS idx_part_entity ON event_participants(entity_id);
CREATE INDEX IF NOT EXISTS idx_holdings_item ON holdings(item_id);
CREATE INDEX IF NOT EXISTS idx_holdings_holder ON holdings(holder_id);
"""

# Columns schema 2 adds to tables that already exist in a version 1 database: (table, column, definition).
_V2_COLUMNS = (
    ("entities", "origin", "TEXT NOT NULL DEFAULT 'c'"), ("names", "origin", "TEXT NOT NULL DEFAULT 'c'"),
    ("attributes", "origin", "TEXT NOT NULL DEFAULT 'c'"), ("events", "origin", "TEXT NOT NULL DEFAULT 'c'"),
    ("events", "kind", "TEXT NOT NULL DEFAULT ''"), ("events", "title", "TEXT NOT NULL DEFAULT ''"), ("events", "parent", "TEXT"),
    ("evidence", "origin", "TEXT NOT NULL DEFAULT 'c'"), ("ops_log", "origin", "TEXT NOT NULL DEFAULT 'c'"),
)

# Tables whose rows are created while processing a chapter (``src_key`` column).
_SRC_TABLES = ("names", "attributes", "relations", "events", "identity_links", "entities")


class Store:
    def __init__(self, path=":memory:"):
        self.path = str(path)
        if self.path != ":memory:":
            os.makedirs(os.path.dirname(os.path.abspath(self.path)), exist_ok=True)
        self.conn = sqlite3.connect(self.path)
        self.conn.row_factory = sqlite3.Row
        try:
            self._open()
        except BaseException:
            self.conn.close()          # a database we refuse to open must not stay locked (Windows cannot delete an open file)
            raise

    def _open(self):
        self.conn.execute("PRAGMA foreign_keys = ON")
        row = self.conn.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone() if self._has_table("meta") else None
        if row is not None and int(row["value"]) not in (1, SCHEMA_VERSION):
            raise ValueError(f"{self.path}: unsupported knowledge database schema {row['value']} (expected {SCHEMA_VERSION})")
        self.conn.executescript(SCHEMA)
        if row is None:
            self.conn.execute("INSERT INTO meta(key, value) VALUES ('schema_version', ?)", (str(SCHEMA_VERSION),))
            self.conn.commit()
        elif int(row["value"]) == 1:
            self._migrate_v1()

    def _has_table(self, name):
        return self.conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (name,)).fetchone() is not None

    def _migrate_v1(self):
        """Schema 1 -> 2, additive only. Every existing row was made by the character pass (origin 'c', untyped events)."""
        with self.transaction():
            for table, column, definition in _V2_COLUMNS:
                if column not in {r["name"] for r in self.conn.execute(f"PRAGMA table_info({table})")}:
                    self.conn.execute(f"ALTER TABLE {table} ADD COLUMN {column} {definition}")
            self.conn.execute("UPDATE meta SET value=? WHERE key='schema_version'", (str(SCHEMA_VERSION),))

    def close(self):
        self.conn.close()

    @contextmanager
    def transaction(self):
        """All-or-nothing unit of work (one answer of the model, one rollback, one snapshot generation)."""
        if self.conn.in_transaction:
            yield self.conn  # nested: the outer transaction decides
            return
        self.conn.execute("BEGIN")
        try:
            yield self.conn
        except BaseException:
            self.conn.rollback()
            raise
        else:
            self.conn.commit()

    # ------------------------------------------------------------------ small helpers
    def get_meta(self, key, default=None):
        row = self.conn.execute("SELECT value FROM meta WHERE key=?", (key,)).fetchone()
        return row["value"] if row else default

    def set_meta(self, key, value):
        self.conn.execute("INSERT INTO meta(key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value=excluded.value", (key, str(value)))
        self.conn.commit()

    def next_id(self, prefix):
        """Opaque ids: ``e0001``, ``r0001``, ``v0001``. Ids never contain names and are never reused."""
        row = self.conn.execute("SELECT value FROM counters WHERE name=?", (prefix,)).fetchone()
        value = (row["value"] if row else 0) + 1
        self.conn.execute("INSERT INTO counters(name, value) VALUES (?, ?) ON CONFLICT(name) DO UPDATE SET value=excluded.value", (prefix, value))
        return f"{prefix}{value:04d}"

    def query(self, sql, params=()):
        return self.conn.execute(sql, params).fetchall()

    def narrator_before(self, key):
        """The character who narrated most recently before position ``key`` (id), or None. Used to tell the model who "わたし" is."""
        row = self.conn.execute("SELECT entity_id FROM attributes WHERE key='narrator' AND from_key < ? ORDER BY from_key DESC, id DESC LIMIT 1",
                                (key,)).fetchone()
        return row["entity_id"] if row else None

    # ------------------------------------------------------------------ chapters (build state)
    def chapter_state(self, volume, chapter):
        return self.conn.execute("SELECT * FROM chapters WHERE volume=? AND chapter=?", (volume, chapter)).fetchone()

    def set_chapter_state(self, volume, chapter, *, title="", text_sha, status, model="", ops_applied=0, ops_rejected=0, error="", built_at=""):
        self.conn.execute(
            "INSERT INTO chapters(volume, chapter, title, text_sha, status, model, ops_applied, ops_rejected, error, built_at) "
            "VALUES (?,?,?,?,?,?,?,?,?,?) ON CONFLICT(volume, chapter) DO UPDATE SET title=excluded.title, text_sha=excluded.text_sha, "
            "status=excluded.status, model=excluded.model, ops_applied=excluded.ops_applied, ops_rejected=excluded.ops_rejected, "
            "error=excluded.error, built_at=excluded.built_at",
            (volume, chapter, title, text_sha, status, model, ops_applied, ops_rejected, error, built_at))
        self.conn.commit()

    def chapters(self):
        return self.conn.execute("SELECT * FROM chapters ORDER BY volume, chapter").fetchall()

    # ------------------------------------------------------------------ story pass: build state
    def story_state(self, volume, chapter):
        return self.conn.execute("SELECT * FROM story_chapters WHERE volume=? AND chapter=?", (volume, chapter)).fetchone()

    def set_story_state(self, volume, chapter, *, text_sha, status, model="", ops_applied=0, ops_rejected=0, error="", built_at=""):
        self.conn.execute(
            "INSERT INTO story_chapters(volume, chapter, text_sha, status, model, ops_applied, ops_rejected, error, built_at) "
            "VALUES (?,?,?,?,?,?,?,?,?) ON CONFLICT(volume, chapter) DO UPDATE SET text_sha=excluded.text_sha, status=excluded.status, "
            "model=excluded.model, ops_applied=excluded.ops_applied, ops_rejected=excluded.ops_rejected, error=excluded.error, "
            "built_at=excluded.built_at",
            (volume, chapter, text_sha, status, model, ops_applied, ops_rejected, error, built_at))
        self.conn.commit()

    def story_chapters(self):
        return self.conn.execute("SELECT * FROM story_chapters ORDER BY volume, chapter").fetchall()

    # ------------------------------------------------------------------ rollback
    def _drop_derived(self):
        for table in ("profile_snapshots", "profile_snapshot_members", "story_event_cards", "story_item_cards"):
            self.conn.execute(f"DELETE FROM {table}")

    def rollback_from(self, start_key):
        """Remove everything created by chapters at or after ``start_key`` and reopen what those chapters closed.

        Used when a chapter is rebuilt or its text changed: later chapters were built from a snapshot that included the old
        facts, so they are removed too (causal consistency). Story-pass data of those chapters goes with it (it was built on
        those entities). Derived snapshots are dropped; they are regenerated after a build.
        """
        with self.transaction():
            self.conn.execute("UPDATE attributes SET to_key=NULL, to_src=NULL WHERE to_src >= ?", (start_key,))
            self.conn.execute("UPDATE relations SET to_key=NULL, to_src=NULL WHERE to_src >= ?", (start_key,))
            self.conn.execute("UPDATE holdings SET to_key=NULL, to_src=NULL, end_reason='', replaced_by='' WHERE to_src >= ?", (start_key,))
            self.conn.execute("DELETE FROM event_participants WHERE event_id IN (SELECT id FROM events WHERE src_key >= ?)", (start_key,))
            self.conn.execute("DELETE FROM holdings WHERE src_key >= ?", (start_key,))
            for table in _SRC_TABLES:
                self.conn.execute(f"DELETE FROM {table} WHERE src_key >= ?", (start_key,))
            for table in ("evidence", "ops_log", "chapters", "story_chapters"):
                self.conn.execute(f"DELETE FROM {table} WHERE (volume * ? + chapter * ?) >= ?", (VOLUME_SCALE, CHAPTER_SCALE, start_key))
            self._drop_derived()

    def rollback_story_from(self, start_key):
        """Remove only what the **story pass** created at or after ``start_key``; the character pass stays as it is.

        Used when a story chapter failed, went stale or is rebuilt. Rows are identified by their ``origin`` tag. Story-pass
        attribute values may have closed older single-valued values (an item's ``status``); those closures are recomputed
        from the rows that remain, because a plain "reopen what chapter N closed" would also reopen values closed by the character pass.
        Evidence rows of the removed chapters are removed with them, so the next run does not pile up duplicates.
        """
        with self.transaction():
            touched = self.conn.execute("SELECT DISTINCT entity_id, key FROM attributes WHERE origin='s' AND src_key >= ?", (start_key,)).fetchall()
            self.conn.execute("UPDATE holdings SET to_key=NULL, to_src=NULL, end_reason='', replaced_by='' WHERE to_src >= ?", (start_key,))
            self.conn.execute("DELETE FROM holdings WHERE src_key >= ?", (start_key,))
            self.conn.execute("DELETE FROM event_participants WHERE event_id IN (SELECT id FROM events WHERE origin='s' AND src_key >= ?)", (start_key,))
            for table in ("events", "attributes", "names", "entities"):
                self.conn.execute(f"DELETE FROM {table} WHERE origin='s' AND src_key >= ?", (start_key,))
            for row in touched:
                if row["key"] in SINGLE_VALUED_KEYS:
                    self._recompute_closures(row["entity_id"], row["key"])
            for table in ("evidence", "ops_log"):
                self.conn.execute(f"DELETE FROM {table} WHERE origin='s' AND (volume * ? + chapter * ?) >= ?", (VOLUME_SCALE, CHAPTER_SCALE, start_key))
            self.conn.execute("DELETE FROM story_chapters WHERE (volume * ? + chapter * ?) >= ?", (VOLUME_SCALE, CHAPTER_SCALE, start_key))
            self._drop_derived()

    def _recompute_closures(self, entity_id, key):
        """A single-valued attribute is valid until the next value of the same key starts (rows ordered by position)."""
        rows = self.conn.execute("SELECT id, from_key, src_key FROM attributes WHERE entity_id=? AND key=? ORDER BY from_key, id",
                                 (entity_id, key)).fetchall()
        for index, row in enumerate(rows):
            follower = rows[index + 1] if index + 1 < len(rows) else None
            self.conn.execute("UPDATE attributes SET to_key=?, to_src=? WHERE id=?",
                              (follower["from_key"] if follower else None, follower["src_key"] if follower else None, row["id"]))

    # ------------------------------------------------------------------ writes used by ops.py
    def add_evidence(self, volume, chapter, start, end, quote, origin=ORIGIN_CHARACTERS):
        key = StoryPosition(volume, chapter, end).key
        cur = self.conn.execute("INSERT INTO evidence(volume, chapter, start, end, quote, end_key, origin) VALUES (?,?,?,?,?,?,?)",
                                (volume, chapter, start, end, quote, key, origin))
        return cur.lastrowid

    def log_op(self, volume, chapter, seq, op, status, reason="", origin=ORIGIN_CHARACTERS):
        self.conn.execute("INSERT INTO ops_log(volume, chapter, seq, op_json, status, reason, origin) VALUES (?,?,?,?,?,?,?)",
                          (volume, chapter, seq, json.dumps(op, ensure_ascii=False), status, reason, origin))

    def add_entity(self, kind, first_key, src_key, origin=ORIGIN_CHARACTERS):
        eid = self.next_id("e")
        self.conn.execute("INSERT INTO entities(id, kind, first_key, src_key, origin) VALUES (?,?,?,?,?)", (eid, kind, first_key, src_key, origin))
        return eid

    def add_name(self, entity_id, name, type_, from_key, src_key, evidence_ids, preferred=False, origin=ORIGIN_CHARACTERS):
        self.conn.execute("INSERT INTO names(entity_id, name, type, from_key, src_key, evidence_ids, preferred, origin) VALUES (?,?,?,?,?,?,?,?)",
                          (entity_id, name, type_, from_key, src_key, json.dumps(evidence_ids), int(preferred), origin))

    def add_attribute(self, entity_id, key, value, from_key, src_key, evidence_ids, *, single_valued, origin=ORIGIN_CHARACTERS):
        """Single-valued keys supersede the previous value; multi-valued keys (speech samples, notes) accumulate."""
        if single_valued:
            open_rows = self.conn.execute("SELECT id, value, from_key FROM attributes WHERE entity_id=? AND key=? AND to_key IS NULL",
                                          (entity_id, key)).fetchall()
            for row in open_rows:
                if row["value"] == value:
                    return None  # the same claim again: nothing to record
            to_key = None
            for row in open_rows:
                if row["from_key"] < from_key:
                    self.conn.execute("UPDATE attributes SET to_key=?, to_src=? WHERE id=?", (from_key, src_key, row["id"]))
                else:
                    # the new claim is *earlier* in the book than the current one: it is valid only until that one starts
                    to_key = row["from_key"] if to_key is None else min(to_key, row["from_key"])
            cur = self.conn.execute(
                "INSERT INTO attributes(entity_id, key, value, from_key, to_key, to_src, src_key, evidence_ids, origin) VALUES (?,?,?,?,?,?,?,?,?)",
                (entity_id, key, value, from_key, to_key, src_key if to_key is not None else None, src_key, json.dumps(evidence_ids), origin))
            return cur.lastrowid
        exists = self.conn.execute("SELECT 1 FROM attributes WHERE entity_id=? AND key=? AND value=?", (entity_id, key, value)).fetchone()
        if exists:
            return None
        cur = self.conn.execute(
            "INSERT INTO attributes(entity_id, key, value, from_key, src_key, evidence_ids, origin) VALUES (?,?,?,?,?,?,?)",
            (entity_id, key, value, from_key, src_key, json.dumps(evidence_ids), origin))
        return cur.lastrowid

    def add_relation(self, src, dst, type_, directed, summary, from_key, src_key, evidence_ids):
        rid = self.next_id("r")
        self.conn.execute("INSERT INTO relations(id, src, dst, type, directed, summary, from_key, src_key, evidence_ids) VALUES (?,?,?,?,?,?,?,?,?)",
                          (rid, src, dst, type_, int(directed), summary, from_key, src_key, json.dumps(evidence_ids)))
        return rid

    def end_relation(self, relation_id, to_key, src_key):
        self.conn.execute("UPDATE relations SET to_key=?, to_src=? WHERE id=?", (to_key, src_key, relation_id))

    def add_event(self, summary, from_key, location, src_key, evidence_ids, participants, *, kind="", title="", parent=None,
                  origin=ORIGIN_CHARACTERS):
        vid = self.next_id("v")
        self.conn.execute("INSERT INTO events(id, summary, from_key, location, src_key, evidence_ids, kind, title, parent, origin) VALUES (?,?,?,?,?,?,?,?,?,?)",
                          (vid, summary, from_key, location, src_key, json.dumps(evidence_ids), kind, title, parent, origin))
        for entity_id, role in participants:
            self.conn.execute("INSERT INTO event_participants(event_id, entity_id, role) VALUES (?,?,?)", (vid, entity_id, role))
        return vid

    def replace_untyped_events(self, src_key):
        """The story pass owns events: once it recorded events for a chapter, the character pass's untyped events of that
        chapter are dropped (they describe the same happenings). Returns how many were dropped; 0 if the story pass found none."""
        if not self.conn.execute("SELECT 1 FROM events WHERE origin='s' AND src_key=? LIMIT 1", (src_key,)).fetchone():
            return 0
        old = "SELECT id FROM events WHERE origin='c' AND kind='' AND src_key=?"
        count = len(self.conn.execute(old, (src_key,)).fetchall())
        self.conn.execute(f"DELETE FROM event_participants WHERE event_id IN ({old})", (src_key,))
        self.conn.execute("DELETE FROM events WHERE origin='c' AND kind='' AND src_key=?", (src_key,))
        return count

    # ------------------------------------------------------------------ holdings (who has which item)
    def set_holding(self, item_id, holder_id, state, slot, from_key, src_key, evidence_ids):
        """Record that ``holder_id`` has ``item_id`` from ``from_key`` on. Returns the new row id, or None if there is nothing to record.

        An item has one holder at a time, so any other open holding of the item ends here (``transferred``; ``changed`` when it is
        the same holder, e.g. carried -> equipped). Equipping into an exclusive slot ends the holder's previous item in that slot
        (``replaced``): newer gear overwrites older gear. The replaced item is *not* assumed to still be carried; the text has to say so.
        A claim positioned **before** a holding that is already recorded (the model listed operations out of order) is valid only
        until that holding starts, exactly like single-valued attributes.
        """
        slot = slot if state == "equipped" else ""
        exclusive = state == "equipped" and slot in EXCLUSIVE_SLOTS
        rows = self.conn.execute(
            "SELECT * FROM holdings WHERE to_key IS NULL AND (item_id=? OR (? AND state='equipped' AND holder_id=? AND slot=?))",
            (item_id, int(exclusive), holder_id, slot)).fetchall()
        bound, bound_reason, bound_row = None, "", None
        for row in rows:
            if row["item_id"] == item_id and row["holder_id"] == holder_id and row["state"] == state and row["slot"] == slot:
                return None      # the same claim again (or it already holds from an earlier point): nothing new
        replaces = ""
        for row in rows:
            if row["item_id"] == item_id:
                reason = "changed" if row["holder_id"] == holder_id else "transferred"
            else:
                reason = "replaced"
            if row["from_key"] < from_key:
                self.conn.execute("UPDATE holdings SET to_key=?, to_src=?, end_reason=?, replaced_by=? WHERE id=?",
                                  (from_key, src_key, reason, item_id if reason == "replaced" else "", row["id"]))
                if reason == "replaced":
                    replaces = row["item_id"]          # the new item is tagged with what it replaced
            elif bound is None or row["from_key"] < bound:
                bound, bound_reason, bound_row = row["from_key"], reason, row
        if bound is not None and bound <= from_key:
            return None
        replaced_by = bound_row["item_id"] if bound_row is not None and bound_reason == "replaced" else ""
        cur = self.conn.execute(
            "INSERT INTO holdings(item_id, holder_id, state, slot, from_key, to_key, to_src, end_reason, replaces, replaced_by, src_key, evidence_ids) "
            "VALUES (?,?,?,?,?,?,?,?,?,?,?,?)",
            (item_id, holder_id, state, slot, from_key, bound, src_key if bound is not None else None, bound_reason, replaces, replaced_by,
             src_key, json.dumps(evidence_ids)))
        if replaced_by:
            # listed out of order: the holding that already existed is the newer gear, so it replaces this one
            self.conn.execute("UPDATE holdings SET replaces=? WHERE id=? AND replaces=''", (item_id, bound_row["id"]))
        return cur.lastrowid

    def end_holdings(self, item_id, holder_id, to_key, src_key, reason="lost"):
        """Nobody (or ``holder_id``) has the item any more from ``to_key`` on. Returns how many holdings were closed."""
        sql = "SELECT id, from_key FROM holdings WHERE item_id=? AND to_key IS NULL" + (" AND holder_id=?" if holder_id else "")
        params = (item_id, holder_id) if holder_id else (item_id,)
        closable = [r for r in self.conn.execute(sql, params).fetchall() if r["from_key"] < to_key]
        for row in closable:
            self.conn.execute("UPDATE holdings SET to_key=?, to_src=?, end_reason=? WHERE id=?", (to_key, src_key, reason, row["id"]))
        return len(closable)

    def add_identity_link(self, a, b, from_key, src_key, evidence_ids):
        self.conn.execute("INSERT INTO identity_links(a, b, from_key, src_key, evidence_ids) VALUES (?,?,?,?,?)",
                          (a, b, from_key, src_key, json.dumps(evidence_ids)))
