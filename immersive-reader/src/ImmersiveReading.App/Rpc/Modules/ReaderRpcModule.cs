using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Dictionaries;
using ImmersiveReading.Enrichment;
using ImmersiveReading.Library;

namespace ImmersiveReading.App.Rpc.Modules;

public sealed class ReaderRpcModule(
    LibraryManager library,
    BookSchemeHandler? bookSchemeHandler = null,
    DictionaryLookupEngine? lookupEngine = null,
    VocabularyTracker? vocabTracker = null,
    LibraryDatabase? db = null,
    IRpcEventSink? events = null) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("reader.openBook", (req, _) => Task.FromResult<object?>(OpenBook(req)));
            yield return ("reader.getChapterContent", (req, _) => Task.FromResult<object?>(GetChapterContent(req)));
            yield return ("reader.saveProgress", (req, _) => Task.FromResult<object?>(SaveProgress(req)));
            yield return ("reader.setEnrichmentOptions", (req, _) => Task.FromResult<object?>(SetEnrichmentOptions(req)));
            yield return ("reader.addBookmark", (req, _) => Task.FromResult<object?>(AddBookmark(req)));
            yield return ("reader.getBookmarks", (req, _) => Task.FromResult<object?>(GetBookmarks(req)));
            yield return ("reader.removeBookmark", (req, _) => Task.FromResult<object?>(RemoveBookmark(req)));
            yield return ("lookup.query", QueryLookupAsync);
            yield return ("vocab.setStatus", (req, _) => Task.FromResult<object?>(SetVocabStatus(req)));
        }
    }

    private object? OpenBook(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        string? requestedSpineHref = req.OptionalString("spineHref");
        var detail = library.GetBookDetail(bookId)
            ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

        string initialSpineHref = "";
        int initialChapterIndex = 0;
        int locatorTokenId = 0;
        int locatorCharOffset = 0;
        double overallCompletion = 0;

        if (!string.IsNullOrWhiteSpace(requestedSpineHref))
        {
            initialSpineHref = requestedSpineHref;
            var matchingCh = detail.Chapters.FirstOrDefault(c =>
                string.Equals(c.SpineHref, requestedSpineHref, StringComparison.OrdinalIgnoreCase));
            if (matchingCh is not null)
            {
                initialChapterIndex = matchingCh.ChapterIndex;
            }
        }
        else if (db is not null)
        {
            using var conn = db.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT current_chapter_index, current_spine_href, locator_token_id, locator_char_offset, overall_completion FROM reading_progress WHERE book_id = $id;";
            cmd.Parameters.AddWithValue("$id", bookId);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                initialChapterIndex = r.GetInt32(0);
                initialSpineHref = r.GetString(1);
                locatorTokenId = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                locatorCharOffset = r.GetInt32(3);
                overallCompletion = r.GetDouble(4);
            }
        }

        if (string.IsNullOrEmpty(initialSpineHref) && detail.Chapters.Count > 0)
        {
            var firstValid = detail.Chapters.FirstOrDefault(c => !c.IsExcluded) ?? detail.Chapters[0];
            initialSpineHref = firstValid.SpineHref;
            initialChapterIndex = firstValid.ChapterIndex;
        }

        if (bookSchemeHandler is not null)
        {
            string targetWritingMode = detail.Summary.WritingMode == "horizontal-tb" ? "horizontal-tb" : "vertical-rl";
            var cur = bookSchemeHandler.CurrentOptions;
            if (cur.WritingMode != targetWritingMode)
            {
                bookSchemeHandler.UpdateEnrichmentOptions(cur with { WritingMode = targetWritingMode });
            }
        }

        string chapterUrl = $"ir-book://{bookId}/{initialSpineHref}";
        var currentOptions = bookSchemeHandler?.CurrentOptions ?? new EnrichmentOptions();
        var enriched = bookSchemeHandler?.GetEnrichedChapter(bookId, initialSpineHref);

        return new
        {
            book = detail,
            initialChapterIndex,
            initialSpineHref,
            initialUrl = chapterUrl,
            initialChapterHtml = enriched?.EnrichedHtml ?? "",
            locatorTokenId,
            locatorCharOffset,
            overallCompletion,
            enrichmentOptions = new
            {
                furiganaMode = currentOptions.FuriganaMode.ToString(),
                highlightVocabulary = currentOptions.HighlightVocabulary,
                writingMode = currentOptions.WritingMode,
                theme = currentOptions.Theme
            }
        };
    }

    private object? GetChapterContent(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        string spineHref = req.RequiredString("spineHref");
        var enriched = bookSchemeHandler?.GetEnrichedChapter(bookId, spineHref);
        return new
        {
            bookId,
            spineHref,
            url = $"ir-book://{bookId}/{spineHref}",
            html = enriched?.EnrichedHtml ?? "",
            tokenCount = enriched?.TokenMap.Tokens.Count ?? 0
        };
    }

    private object? SaveProgress(RpcRequest req)
    {
        string bookId = req.RequiredString("bookId");
        int chapterIndex = req.OptionalInt("chapterIndex") ?? 0;
        string spineHref = req.RequiredString("spineHref");
        int? tokenId = req.OptionalInt("tokenId");
        int charOffset = req.OptionalInt("charOffset") ?? 0;
        double ratio = req.OptionalDouble("ratio") ?? 0;
        double completion = req.OptionalDouble("completion") ?? 0;

        if (db is not null)
        {
            string now = DateTime.UtcNow.ToString("O");
            using var conn = db.OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO reading_progress (
                    book_id, current_chapter_index, current_spine_href, locator_token_id,
                    locator_char_offset, scroll_or_page_ratio, overall_completion, updated_at_utc
                ) VALUES (
                    $id, $chIdx, $href, $tok, $offset, $ratio, $comp, $now
                )
                ON CONFLICT(book_id) DO UPDATE SET
                    current_chapter_index = excluded.current_chapter_index,
                    current_spine_href = excluded.current_spine_href,
                    locator_token_id = excluded.locator_token_id,
                    locator_char_offset = excluded.locator_char_offset,
                    scroll_or_page_ratio = excluded.scroll_or_page_ratio,
                    overall_completion = excluded.overall_completion,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", bookId);
            cmd.Parameters.AddWithValue("$chIdx", chapterIndex);
            cmd.Parameters.AddWithValue("$href", spineHref);
            cmd.Parameters.AddWithValue("$tok", (object?)tokenId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$offset", charOffset);
            cmd.Parameters.AddWithValue("$ratio", ratio);
            cmd.Parameters.AddWithValue("$comp", completion);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();

            using var updBook = conn.CreateCommand();
            updBook.CommandText = "UPDATE books SET last_opened_at_utc = $now WHERE book_id = $id;";
            updBook.Parameters.AddWithValue("$id", bookId);
            updBook.Parameters.AddWithValue("$now", now);
            updBook.ExecuteNonQuery();
        }

        return new { ok = true };
    }

    private object? SetEnrichmentOptions(RpcRequest req)
    {
        if (bookSchemeHandler is not null)
        {
            string fModeStr = req.OptionalString("furiganaMode") ?? "AllKanji";
            bool highlight = req.OptionalBool("highlightVocabulary") ?? false;
            string writingMode = req.OptionalString("writingMode") ?? "auto";
            string theme = req.OptionalString("theme") ?? "paper";

            Enum.TryParse<FuriganaMode>(fModeStr, ignoreCase: true, out var fMode);
            var newOptions = new EnrichmentOptions(fMode, highlight, writingMode, theme);
            bookSchemeHandler.UpdateEnrichmentOptions(newOptions);
            return new { ok = true, options = newOptions };
        }
        return new { ok = true };
    }

    private async Task<object?> QueryLookupAsync(RpcRequest req, CancellationToken ct)
    {
        if (lookupEngine is null) throw new InvalidOperationException("Lookup engine not available.");
        string bookId = req.RequiredString("bookId");
        string chapterHref = req.RequiredString("chapterHref");
        int tokenId = req.OptionalInt("tokenId") ?? throw new ArgumentException("Missing required parameter 'tokenId'.");
        int charOffset = req.OptionalInt("charOffsetInToken") ?? 0;
        return await lookupEngine.QueryAsync(new LookupQuery(bookId, chapterHref, tokenId, charOffset), ct);
    }

    private object? SetVocabStatus(RpcRequest req)
    {
        if (vocabTracker is null) throw new InvalidOperationException("Vocabulary tracker not available.");
        string lang = req.OptionalString("language") ?? "ja";
        string lemma = req.RequiredString("lemma");
        string reading = req.RequiredString("reading");
        int statusInt = req.OptionalInt("status") ?? throw new ArgumentException("Missing required parameter 'status'.");
        string? gloss = req.OptionalString("customGloss");
        var status = (WordStatus)statusInt;
        vocabTracker.SetStatus(lang, lemma, reading, status, gloss);
        events?.Emit("vocab.statusChanged", new { language = lang, lemma, reading, status = statusInt });
        return new { ok = true, lemma, status = statusInt };
    }

    private object? AddBookmark(RpcRequest req)
    {
        if (db is null) throw new InvalidOperationException("Database not available.");
        string bookId = req.RequiredString("bookId");
        string spineHref = req.RequiredString("spineHref");
        int chIdx = req.OptionalInt("chapterIndex") ?? 0;
        int? startTokenId = req.OptionalInt("startTokenId");
        int? endTokenId = req.OptionalInt("endTokenId");
        int startOffset = req.OptionalInt("startCharOffset") ?? 0;
        int endOffset = req.OptionalInt("endCharOffset") ?? 0;
        string text = req.OptionalString("selectedText") ?? "Bookmark";
        string note = req.OptionalString("userNote") ?? "";
        string? color = req.OptionalString("highlightColor") ?? "#38bdf8";
        string bookmarkId = Guid.NewGuid().ToString("N");
        string now = DateTime.UtcNow.ToString("o");

        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO bookmarks (
                bookmark_id, book_id, chapter_index, spine_href,
                start_token_id, end_token_id, start_char_offset, end_char_offset,
                selected_text, user_note, highlight_color, created_at_utc
            ) VALUES (
                $bId, $bookId, $chIdx, $href,
                $startTok, $endTok, $startOffset, $endOffset,
                $text, $note, $color, $now
            );
        """;
        cmd.Parameters.AddWithValue("$bId", bookmarkId);
        cmd.Parameters.AddWithValue("$bookId", bookId);
        cmd.Parameters.AddWithValue("$chIdx", chIdx);
        cmd.Parameters.AddWithValue("$href", spineHref);
        cmd.Parameters.AddWithValue("$startTok", (object?)startTokenId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$endTok", (object?)endTokenId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startOffset", startOffset);
        cmd.Parameters.AddWithValue("$endOffset", endOffset);
        cmd.Parameters.AddWithValue("$text", text);
        cmd.Parameters.AddWithValue("$note", note);
        cmd.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        var item = new BookmarkItem(
            bookmarkId, bookId, chIdx, spineHref,
            startTokenId, endTokenId, startOffset, endOffset,
            text, note, color, now
        );

        events?.Emit("reader.bookmarkAdded", item);
        return item;
    }

    private object? GetBookmarks(RpcRequest req)
    {
        if (db is null) return new { bookmarks = Array.Empty<BookmarkItem>() };
        string bookId = req.RequiredString("bookId");
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bookmark_id, book_id, chapter_index, spine_href,
                   start_token_id, end_token_id, start_char_offset, end_char_offset,
                   selected_text, user_note, highlight_color, created_at_utc
            FROM bookmarks
            WHERE book_id = $bookId
            ORDER BY chapter_index ASC, start_char_offset ASC, created_at_utc ASC;
        """;
        cmd.Parameters.AddWithValue("$bookId", bookId);

        var list = new List<BookmarkItem>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BookmarkItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetString(11)
            ));
        }
        return new { bookId, bookmarks = list, count = list.Count };
    }

    private object? RemoveBookmark(RpcRequest req)
    {
        if (db is null) throw new InvalidOperationException("Database not available.");
        string bookmarkId = req.RequiredString("bookmarkId");
        using var conn = db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM bookmarks WHERE bookmark_id = $bId;";
        cmd.Parameters.AddWithValue("$bId", bookmarkId);
        int affected = cmd.ExecuteNonQuery();
        events?.Emit("reader.bookmarkRemoved", new { bookmarkId });
        return new { ok = affected > 0, bookmarkId };
    }
}

public sealed record BookmarkItem(
    string BookmarkId,
    string BookId,
    int ChapterIndex,
    string SpineHref,
    int? StartTokenId,
    int? EndTokenId,
    int StartCharOffset,
    int EndCharOffset,
    string SelectedText,
    string UserNote,
    string? HighlightColor,
    string CreatedAtUtc
);

