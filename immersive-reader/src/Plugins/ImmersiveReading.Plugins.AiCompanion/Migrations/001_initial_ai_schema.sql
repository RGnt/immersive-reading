CREATE TABLE IF NOT EXISTS ai_chat_messages (
    message_id TEXT PRIMARY KEY,
    book_id TEXT NOT NULL,
    persona_name TEXT NOT NULL,
    role TEXT NOT NULL,
    raw_text TEXT NOT NULL,
    enriched_html TEXT NOT NULL,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE INDEX IF NOT EXISTS idx_ai_chat_book ON ai_chat_messages(book_id, created_at);

CREATE TABLE IF NOT EXISTS ai_quiz_sessions (
    quiz_id TEXT PRIMARY KEY,
    book_id TEXT NOT NULL,
    chapter_index INTEGER NOT NULL,
    viewport_token_id INTEGER NOT NULL,
    questions_json TEXT NOT NULL,
    score INTEGER,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
