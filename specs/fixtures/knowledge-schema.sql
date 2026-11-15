
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
