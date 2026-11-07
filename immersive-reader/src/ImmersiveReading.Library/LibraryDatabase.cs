using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library;

public sealed class LibraryDatabase : IDisposable
{
    private readonly string _connectionString;

    public string DatabasePath { get; }
    public string CoversCacheDirectory { get; }

    public LibraryDatabase(string databasePath, string? coversCacheDirectory = null)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        string dbDir = Path.GetDirectoryName(DatabasePath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dbDir);

        CoversCacheDirectory = coversCacheDirectory is not null
            ? Path.GetFullPath(coversCacheDirectory)
            : Path.Combine(dbDir, "covers");
        Directory.CreateDirectory(CoversCacheDirectory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        _connectionString = builder.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA temp_store = MEMORY;
            """;
        pragmaCmd.ExecuteNonQuery();
        return conn;
    }

    public void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = SchemaSql;
        cmd.ExecuteNonQuery();

        SeedDefaultCustomFields(conn);
    }

    private static void SeedDefaultCustomFields(SqliteConnection conn)
    {
        string now = DateTime.UtcNow.ToString("O");
        (string Key, string Name, string Type, int Order)[] defaults =
        [
            ("jiten_moe_url", "jiten.moe Media Link", "url", 10),
            ("vocab_deck_csv", "Vocabulary Deck CSV", "file_path", 20),
            ("jiten_difficulty", "Jiten Difficulty", "number", 30)
        ];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO custom_field_definitions (
                field_key, display_name, field_type, target_scope, inherit_to_children, options_json, show_on_card, sort_order, created_at_utc
            ) VALUES (
                $key, $name, $type, 'any', 1, '[]', 1, $order, $now
            );
            """;
        var pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        var pName = cmd.Parameters.Add("$name", SqliteType.Text);
        var pType = cmd.Parameters.Add("$type", SqliteType.Text);
        var pOrder = cmd.Parameters.Add("$order", SqliteType.Integer);
        var pNow = cmd.Parameters.Add("$now", SqliteType.Text);
        pNow.Value = now;

        foreach (var d in defaults)
        {
            pKey.Value = d.Key;
            pName.Value = d.Name;
            pType.Value = d.Type;
            pOrder.Value = d.Order;
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        // Pooled connections are managed by Microsoft.Data.Sqlite
    }

    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS library_folders (
            folder_id           TEXT PRIMARY KEY,
            parent_folder_id    TEXT REFERENCES library_folders(folder_id) ON DELETE CASCADE,
            name                TEXT NOT NULL,
            folder_kind         TEXT NOT NULL DEFAULT 'folder',
            description         TEXT NOT NULL DEFAULT '',
            icon                TEXT NOT NULL DEFAULT 'folder',
            color_hex           TEXT,
            sort_order          INTEGER NOT NULL DEFAULT 0,
            default_book_sort   TEXT NOT NULL DEFAULT 'series_or_title',
            created_at_utc      TEXT NOT NULL,
            updated_at_utc      TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_folders_parent ON library_folders(parent_folder_id, sort_order);

        CREATE TABLE IF NOT EXISTS books (
            book_id             TEXT PRIMARY KEY,
            epub_sha256         TEXT NOT NULL UNIQUE,
            folder_id           TEXT REFERENCES library_folders(folder_id) ON DELETE SET NULL,
            title               TEXT NOT NULL,
            sort_title          TEXT NOT NULL,
            series_name         TEXT NOT NULL DEFAULT '',
            series_index        REAL,
            custom_sort_order   INTEGER NOT NULL DEFAULT 0,
            authors             TEXT NOT NULL DEFAULT '',
            author_sort         TEXT NOT NULL DEFAULT '',
            isbn                TEXT NOT NULL DEFAULT '',
            publisher           TEXT NOT NULL DEFAULT '',
            imprint             TEXT NOT NULL DEFAULT '',
            published_date      TEXT NOT NULL DEFAULT '',
            language            TEXT NOT NULL DEFAULT 'ja',
            description         TEXT NOT NULL DEFAULT '',
            opf_identifier      TEXT NOT NULL DEFAULT '',
            opf_original_json   TEXT NOT NULL DEFAULT '{}',
            is_metadata_edited  INTEGER NOT NULL DEFAULT 0,
            folder_path         TEXT NOT NULL,
            epub_file_name      TEXT NOT NULL,
            file_size_bytes     INTEGER NOT NULL DEFAULT 0,
            cover_cache_path    TEXT,
            chapter_count       INTEGER NOT NULL DEFAULT 0,
            total_char_count    INTEGER NOT NULL DEFAULT 0,
            stats_computed      INTEGER NOT NULL DEFAULT 0,
            writing_mode        TEXT NOT NULL DEFAULT 'auto',
            added_at_utc        TEXT NOT NULL,
            updated_at_utc      TEXT NOT NULL,
            last_opened_at_utc  TEXT,
            has_study_pack      INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_books_folder_sort ON books(folder_id, series_name, series_index, custom_sort_order, sort_title);
        CREATE INDEX IF NOT EXISTS idx_books_series ON books(series_name, series_index);
        CREATE INDEX IF NOT EXISTS idx_books_author ON books(author_sort, authors);
        CREATE INDEX IF NOT EXISTS idx_books_isbn ON books(isbn) WHERE isbn != '';
        CREATE INDEX IF NOT EXISTS idx_books_added ON books(added_at_utc DESC);
        CREATE INDEX IF NOT EXISTS idx_books_last_opened ON books(last_opened_at_utc DESC);

        CREATE VIRTUAL TABLE IF NOT EXISTS books_fts USING fts5(
            book_id UNINDEXED,
            title,
            sort_title,
            series_name,
            authors,
            author_sort,
            publisher,
            imprint,
            isbn,
            description,
            custom_fields_text,
            tokenize = 'unicode61'
        );

        CREATE TABLE IF NOT EXISTS custom_field_definitions (
            field_key           TEXT PRIMARY KEY,
            display_name        TEXT NOT NULL,
            field_type          TEXT NOT NULL,
            target_scope        TEXT NOT NULL DEFAULT 'any',
            inherit_to_children INTEGER NOT NULL DEFAULT 1,
            options_json        TEXT NOT NULL DEFAULT '[]',
            show_on_card        INTEGER NOT NULL DEFAULT 1,
            sort_order          INTEGER NOT NULL DEFAULT 0,
            created_at_utc      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS custom_field_values (
            entity_type         TEXT NOT NULL,
            entity_id           TEXT NOT NULL,
            field_key           TEXT NOT NULL REFERENCES custom_field_definitions(field_key) ON DELETE CASCADE,
            value_text          TEXT NOT NULL DEFAULT '',
            value_number        REAL,
            updated_at_utc      TEXT NOT NULL,
            PRIMARY KEY (entity_type, entity_id, field_key)
        );
        CREATE INDEX IF NOT EXISTS idx_custom_values_lookup ON custom_field_values(field_key, value_text);
        CREATE INDEX IF NOT EXISTS idx_custom_values_num ON custom_field_values(field_key, value_number);
        CREATE INDEX IF NOT EXISTS idx_custom_values_entity ON custom_field_values(entity_type, entity_id);

        CREATE TABLE IF NOT EXISTS tags (
            tag_id              TEXT PRIMARY KEY,
            name                TEXT NOT NULL UNIQUE COLLATE NOCASE,
            color_hex           TEXT NOT NULL DEFAULT '#64748b',
            sort_order          INTEGER NOT NULL DEFAULT 0,
            created_at_utc      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS book_tags (
            book_id             TEXT NOT NULL REFERENCES books(book_id) ON DELETE CASCADE,
            tag_id              TEXT NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
            assigned_at_utc     TEXT NOT NULL,
            PRIMARY KEY (book_id, tag_id)
        );
        CREATE INDEX IF NOT EXISTS idx_book_tags_tag ON book_tags(tag_id, book_id);

        CREATE TABLE IF NOT EXISTS folder_tags (
            folder_id           TEXT NOT NULL REFERENCES library_folders(folder_id) ON DELETE CASCADE,
            tag_id              TEXT NOT NULL REFERENCES tags(tag_id) ON DELETE CASCADE,
            assigned_at_utc     TEXT NOT NULL,
            PRIMARY KEY (folder_id, tag_id)
        );

        CREATE TABLE IF NOT EXISTS book_chapters (
            book_id             TEXT NOT NULL REFERENCES books(book_id) ON DELETE CASCADE,
            chapter_index       INTEGER NOT NULL,
            chapter_id          TEXT NOT NULL,
            title               TEXT NOT NULL,
            spine_href          TEXT NOT NULL,
            fragment_id         TEXT NOT NULL DEFAULT '',
            end_spine_href      TEXT,
            end_fragment_id     TEXT,
            char_count          INTEGER NOT NULL DEFAULT 0,
            cumulative_chars    INTEGER NOT NULL DEFAULT 0,
            is_excluded         INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (book_id, chapter_index)
        );

        CREATE TABLE IF NOT EXISTS reading_progress (
            book_id                 TEXT PRIMARY KEY REFERENCES books(book_id) ON DELETE CASCADE,
            current_chapter_index   INTEGER NOT NULL DEFAULT 0,
            current_spine_href      TEXT NOT NULL,
            locator_token_id        INTEGER,
            locator_char_offset     INTEGER NOT NULL DEFAULT 0,
            scroll_or_page_ratio    REAL NOT NULL DEFAULT 0.0,
            overall_completion      REAL NOT NULL DEFAULT 0.0,
            total_reading_seconds   INTEGER NOT NULL DEFAULT 0,
            updated_at_utc          TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS bookmarks (
            bookmark_id         TEXT PRIMARY KEY,
            book_id             TEXT NOT NULL REFERENCES books(book_id) ON DELETE CASCADE,
            chapter_index       INTEGER NOT NULL,
            spine_href          TEXT NOT NULL,
            start_token_id      INTEGER,
            end_token_id        INTEGER,
            start_char_offset   INTEGER NOT NULL,
            end_char_offset     INTEGER NOT NULL,
            selected_text       TEXT NOT NULL,
            user_note           TEXT NOT NULL DEFAULT '',
            highlight_color     TEXT,
            created_at_utc      TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS vocabulary_state (
            language            TEXT NOT NULL,
            lemma               TEXT NOT NULL,
            reading             TEXT NOT NULL,
            status              INTEGER NOT NULL,
            custom_gloss        TEXT,
            times_looked_up     INTEGER NOT NULL DEFAULT 0,
            first_seen_book_id  TEXT,
            updated_at_utc      TEXT NOT NULL,
            PRIMARY KEY (language, lemma, reading)
        );
        CREATE INDEX IF NOT EXISTS idx_vocab_state_lemma ON vocabulary_state(language, lemma);

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
        """;
}

