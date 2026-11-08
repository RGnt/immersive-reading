using ImmersiveReading.Core.Abstractions;

namespace ImmersiveReading.Plugins.AiCompanion.Schema;

public record AiCompanionDbMigration(string ModuleId, int Version, string Name, string Sql) : IDbMigration;

public static class AiCompanionMigrations
{
    public const string ModuleId = "aiCompanion";

    public static readonly IReadOnlyList<IDbMigration> All =
    [
        new AiCompanionDbMigration(ModuleId, 1, "InitialAiCompanionTables", """
            CREATE TABLE IF NOT EXISTS comprehension_sessions (
                session_id          TEXT PRIMARY KEY,
                book_id             TEXT NOT NULL,
                chapter_index       INTEGER NOT NULL,
                start_token_id      INTEGER NOT NULL,
                end_token_id        INTEGER NOT NULL,
                passage_text        TEXT NOT NULL,
                created_at_utc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_comp_sessions_book ON comprehension_sessions(book_id, created_at_utc DESC);

            CREATE TABLE IF NOT EXISTS comprehension_questions (
                question_id         TEXT PRIMARY KEY,
                session_id          TEXT NOT NULL REFERENCES comprehension_sessions(session_id) ON DELETE CASCADE,
                book_id             TEXT NOT NULL,
                question_mode       TEXT NOT NULL,
                prompt_text         TEXT NOT NULL,
                prompt_html         TEXT NOT NULL DEFAULT '',
                options_json        TEXT,
                correct_option_idx  INTEGER,
                rubric_json         TEXT NOT NULL,
                user_answer         TEXT,
                score               REAL,
                feedback_json       TEXT,
                answered_at_utc     TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_comp_questions_book ON comprehension_questions(book_id, answered_at_utc DESC);

            CREATE TABLE IF NOT EXISTS book_personas (
                persona_id          TEXT PRIMARY KEY,
                book_id             TEXT,
                name                TEXT NOT NULL,
                role_summary        TEXT NOT NULL DEFAULT '',
                speech_style        TEXT NOT NULL DEFAULT '',
                voice_samples_json  TEXT NOT NULL DEFAULT '[]',
                max_spoiler_chapter INTEGER,
                max_spoiler_token   INTEGER,
                created_at_utc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_personas_book ON book_personas(book_id);

            CREATE TABLE IF NOT EXISTS persona_chat_sessions (
                chat_id             TEXT PRIMARY KEY,
                persona_id          TEXT NOT NULL REFERENCES book_personas(persona_id) ON DELETE CASCADE,
                book_id             TEXT,
                n_plus_one_strict   INTEGER NOT NULL DEFAULT 1,
                created_at_utc      TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS persona_chat_messages (
                message_id          TEXT PRIMARY KEY,
                chat_id             TEXT NOT NULL REFERENCES persona_chat_sessions(chat_id) ON DELETE CASCADE,
                sender_role         TEXT NOT NULL,
                raw_text            TEXT NOT NULL,
                enriched_html       TEXT NOT NULL,
                plus_one_words_json TEXT NOT NULL DEFAULT '[]',
                comprehension_ratio REAL,
                created_at_utc      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_chat_messages_session ON persona_chat_messages(chat_id, created_at_utc ASC);
            """)
    ];
}

