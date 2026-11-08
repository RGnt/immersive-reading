using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    IRpcEventSink? events = null,
    string? extractorOutputDir = null,
    DictionaryDatabase? dictDb = null) : IRpcModule
{
    private readonly string? _extractorOutputDir = extractorOutputDir;
    private readonly DictionaryDatabase? _dictDb = dictDb;

    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("reader.openBook", (req, _) => Task.FromResult<object?>(OpenBook(req)));
            yield return ("reader.getChapterContent", (req, _) => Task.FromResult<object?>(GetChapterContent(req)));
            yield return ("reader.getChapterInsights", GetChapterInsightsAsync);
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
            var firstValid = detail.Chapters.FirstOrDefault(c => !c.IsExcluded && c.CharCount > 0)
                ?? detail.Chapters.FirstOrDefault(c => !c.IsExcluded)
                ?? detail.Chapters.FirstOrDefault(c => !c.Title.Contains("表紙") && !c.Title.Contains("Cover") && !c.Title.Contains("目次") && !c.Title.Contains("Contents"))
                ?? detail.Chapters[0];
            initialSpineHref = firstValid.SpineHref;
            initialChapterIndex = firstValid.ChapterIndex;
        }
        else if (string.IsNullOrWhiteSpace(requestedSpineHref) && detail.Chapters.Count > 0)
        {
            // If saved reading progress points to an excluded cover/front-matter chapter, advance to first story chapter
            var targetCh = detail.Chapters.FirstOrDefault(c => string.Equals(c.SpineHref, initialSpineHref, StringComparison.OrdinalIgnoreCase));
            if (targetCh is not null && targetCh.IsExcluded)
            {
                var storyCh = detail.Chapters.FirstOrDefault(c => !c.IsExcluded && c.CharCount > 0)
                    ?? detail.Chapters.FirstOrDefault(c => !c.IsExcluded)
                    ?? detail.Chapters.FirstOrDefault(c => !c.Title.Contains("表紙") && !c.Title.Contains("Cover") && !c.Title.Contains("目次") && !c.Title.Contains("Contents"));
                if (storyCh is not null)
                {
                    initialSpineHref = storyCh.SpineHref;
                    initialChapterIndex = storyCh.ChapterIndex;
                }
            }
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

    private static readonly (string RuleId, string Label, string Meaning, Regex Regex)[] JapaneseGrammarRules =
    [
        ("te-iru", "～ている / ～でいる", "Ongoing action or continuous state", new Regex(@"(?:て|で)(?:いる|いた|いて|いない|います|いました)", RegexOptions.Compiled)),
        ("te-kuru", "～てくる / ～でくる", "Movement toward speaker or emerging change", new Regex(@"(?:て|で)(?:くる|きた|きて)", RegexOptions.Compiled)),
        ("te-iku", "～ていく / ～でいく", "Movement away or continuing change", new Regex(@"(?:て|で)(?:いく|いった|いって)", RegexOptions.Compiled)),
        ("te-shimau", "～てしまう / ～でしまう", "Completion or unintended/regretted action", new Regex(@"(?:て|で)しま(?:う|った|って)", RegexOptions.Compiled)),
        ("te-oku", "～ておく / ～でおく", "Action in preparation", new Regex(@"(?:て|で)お(?:く|いた|いて)", RegexOptions.Compiled)),
        ("te-miru", "～てみる / ～でみる", "Try doing something", new Regex(@"(?:て|で)み(?:る|た|て|よう)", RegexOptions.Compiled)),
        ("te-kureru", "～てくれる / ～でくれる", "Action done as a favor for speaker", new Regex(@"(?:て|で)くれ(?:る|た|て)", RegexOptions.Compiled)),
        ("te-morau", "～てもらう / ～でもらう", "Receive a favor", new Regex(@"(?:て|で)もら(?:う|った|って|える)", RegexOptions.Compiled)),
        ("tara", "～たら / ～だら", "Conditional if / when", new Regex(@"(?:った|いた|えた|した|れた|んだ|かった)ら", RegexOptions.Compiled)),
        ("n-da", "～んだ / ～のだ", "Explanatory emphasis", new Regex(@"(?:ん|の)(?:だ|です|だった|でしょ|だね|だよ)", RegexOptions.Compiled)),
        ("kara", "～から (reason)", "Because / gives a reason", new Regex(@"(?:る|た|だ|い|ない|ます|です)から", RegexOptions.Compiled)),
        ("sou-da", "～そう (appearance)", "Looks / seems ...", new Regex(@"(?:し|い|な|れ|け|せ|た|み)そう(?:な|に|だ|です)", RegexOptions.Compiled)),
        ("rashii", "～らしい", "Apparently / characteristic", new Regex(@"らし(?:い|く|かった)", RegexOptions.Compiled)),
        ("kamo", "～かもしれない", "Might / perhaps", new Regex(@"かもしれ(?:ない|なかった|ません)", RegexOptions.Compiled)),
        ("you-ni", "～ように / ～ような", "In such a way that / like ...", new Regex(@"よう[にな]", RegexOptions.Compiled)),
        ("tame", "～ために", "For / in order to", new Regex(@"ために", RegexOptions.Compiled)),
        ("noni", "～のに", "Although / despite", new Regex(@"(?:る|た|い|な)のに", RegexOptions.Compiled)),
        ("hazu", "～はず", "Expected to / should", new Regex(@"はず(?:だ|です|が|な|の)", RegexOptions.Compiled)),
        ("sugiru", "～すぎる", "Do excessively / too much", new Regex(@"すぎ(?:る|た|て)", RegexOptions.Compiled)),
    ];

    private async Task<object?> GetChapterInsightsAsync(RpcRequest req, CancellationToken ct)
    {
        string bookId = req.RequiredString("bookId");
        int chapterIndex = req.OptionalInt("chapterIndex") ?? -1;
        string? spineHref = req.OptionalString("spineHref");

        var detail = library.GetBookDetail(bookId)
            ?? throw new KeyNotFoundException($"Book '{bookId}' not found.");

        if (string.IsNullOrEmpty(spineHref) && chapterIndex >= 0 && chapterIndex < detail.Chapters.Count)
        {
            spineHref = detail.Chapters[chapterIndex].SpineHref;
        }
        else if (!string.IsNullOrEmpty(spineHref) && chapterIndex < 0)
        {
            var match = detail.Chapters.FirstOrDefault(c => string.Equals(c.SpineHref, spineHref, StringComparison.OrdinalIgnoreCase));
            if (match is not null) chapterIndex = match.ChapterIndex;
        }

        if (chapterIndex < 0) chapterIndex = 0;
        if (string.IsNullOrEmpty(spineHref) && detail.Chapters.Count > 0)
        {
            spineHref = detail.Chapters[0].SpineHref;
        }

        string chapterTitle = detail.Chapters.FirstOrDefault(c => c.ChapterIndex == chapterIndex)?.Title
            ?? $"Chapter {chapterIndex + 1}";

        // 1. Try reading pre-generated study_data.json if present
        if (!string.IsNullOrEmpty(_extractorOutputDir))
        {
            string studyDataPath = Path.Combine(_extractorOutputDir, "data", bookId, "study_data.json");
            if (File.Exists(studyDataPath))
            {
                try
                {
                    using var stream = File.OpenRead(studyDataPath);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (doc.RootElement.TryGetProperty("chapters", out var chArray) && chArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var chEl in chArray.EnumerateArray())
                        {
                            int idx = chEl.TryGetProperty("index", out var idxProp) ? idxProp.GetInt32() : -1;
                            string href = chEl.TryGetProperty("spineHref", out var hrefProp) ? (hrefProp.GetString() ?? "") : "";
                            if (idx == chapterIndex || (!string.IsNullOrEmpty(spineHref) && string.Equals(href, spineHref, StringComparison.OrdinalIgnoreCase)))
                            {
                                return new
                                {
                                    bookId,
                                    chapterIndex,
                                    spineHref,
                                    chapterTitle,
                                    vocabulary = chEl.TryGetProperty("vocabulary", out var v) ? v.Clone() : default,
                                    kanji = chEl.TryGetProperty("kanji", out var k) ? k.Clone() : default,
                                    grammar = chEl.TryGetProperty("grammar", out var g) ? g.Clone() : default,
                                };
                            }
                        }
                    }
                }
                catch
                {
                    // Fall back to live extraction
                }
            }
        }

        // 2. Dynamic live extraction from ChapterTokenMap
        var enriched = bookSchemeHandler?.GetEnrichedChapter(bookId, spineHref ?? "");
        var tokenMap = enriched?.TokenMap;

        var vocabList = new List<object>();
        var kanjiList = new List<object>();
        var grammarList = new List<object>();

        if (tokenMap is not null)
        {
            var wordCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var wordInfo = new Dictionary<string, (string Surface, string Reading, string Pos, string Sentence)>(StringComparer.Ordinal);
            var kanjiCounts = new Dictionary<char, int>();
            var kanjiWords = new Dictionary<char, HashSet<string>>();

            foreach (var entry in tokenMap.Tokens)
            {
                var tok = entry.Token;
                if (string.IsNullOrWhiteSpace(tok.Surface) || tok.PartOfSpeech is "補助記号" or "空白" or "Punctuation")
                    continue;

                string key = !string.IsNullOrEmpty(tok.Lemma) ? tok.Lemma : tok.Surface;
                wordCounts[key] = wordCounts.GetValueOrDefault(key, 0) + 1;
                if (!wordInfo.ContainsKey(key))
                {
                    string sampleSent = "";
                    if (entry.ParagraphId >= 0 && entry.ParagraphId < tokenMap.ParagraphTexts.Count)
                    {
                        sampleSent = tokenMap.ParagraphTexts[entry.ParagraphId];
                    }
                    var cat = ImmersiveReading.Plugins.PosHighlighter.PosClassifier.Classify(tok);
                    string posCode = ImmersiveReading.Plugins.PosHighlighter.PosClassifier.ToCode(cat);
                    string posLabel = posCode != "unk" ? posCode : (!string.IsNullOrEmpty(tok.PartOfSpeech) ? tok.PartOfSpeech : "noun");
                    wordInfo[key] = (tok.Surface, tok.Reading ?? "", posLabel, sampleSent);
                }

                foreach (char c in tok.Surface)
                {
                    if (c >= '\u4e00' && c <= '\u9faf')
                    {
                        kanjiCounts[c] = kanjiCounts.GetValueOrDefault(c, 0) + 1;
                        if (!kanjiWords.TryGetValue(c, out var set))
                        {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            kanjiWords[c] = set;
                        }
                        if (set.Count < 4) set.Add(tok.Surface);
                    }
                }
            }

            var csvDeck = LoadCsvDeckForBook(detail.Summary.FolderPath);
            var wordsToEnrich = wordCounts.Keys.Where(k => !csvDeck.ContainsKey(k)).Take(250).ToList();
            var dictDefs = new Dictionary<string, (string Reading, string Meaning)>(StringComparer.OrdinalIgnoreCase);
            EnrichWordsWithDictDb(dictDefs, wordsToEnrich);

            foreach (var kv in wordCounts.OrderByDescending(kv => kv.Value))
            {
                var info = wordInfo[kv.Key];
                string reading = info.Reading;
                string meaning = "";

                if (csvDeck.TryGetValue(kv.Key, out var csvVal) || csvDeck.TryGetValue(info.Surface, out csvVal))
                {
                    if (string.IsNullOrEmpty(reading) && !string.IsNullOrEmpty(csvVal.Reading)) reading = csvVal.Reading;
                    meaning = csvVal.Meaning;
                }
                else if (dictDefs.TryGetValue(kv.Key, out var dVal) || dictDefs.TryGetValue(info.Surface, out dVal))
                {
                    if (string.IsNullOrEmpty(reading) && !string.IsNullOrEmpty(dVal.Reading)) reading = dVal.Reading;
                    meaning = dVal.Meaning;
                }

                vocabList.Add(new
                {
                    lemma = kv.Key,
                    word = info.Surface,
                    reading,
                    partOfSpeech = info.Pos,
                    occurrences = kv.Value,
                    sampleSentence = info.Sentence,
                    definition = meaning
                });
            }

            var kanjiChars = kanjiCounts.Keys.Take(250).ToList();
            var kanjiMeta = QueryKanjiDetails(kanjiChars);

            foreach (var kv in kanjiCounts.OrderByDescending(kv => kv.Value))
            {
                var meta = kanjiMeta.GetValueOrDefault(kv.Key);
                kanjiList.Add(new
                {
                    character = kv.Key.ToString(),
                    occurrences = kv.Value,
                    words = kanjiWords.TryGetValue(kv.Key, out var set) ? set.ToList() : new List<string>(),
                    codepointHex = ((int)kv.Key).ToString("X4"),
                    meanings = meta.Meanings ?? new List<string>(),
                    onyomi = meta.Onyomi ?? new List<string>(),
                    kunyomi = meta.Kunyomi ?? new List<string>(),
                    strokeCount = meta.StrokeCount
                });
            }

            foreach (var (ruleId, label, meaning, rx) in JapaneseGrammarRules)
            {
                int occurrences = 0;
                var examples = new List<string>();
                foreach (var p in tokenMap.ParagraphTexts)
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var matches = rx.Matches(p);
                    if (matches.Count > 0)
                    {
                        occurrences += matches.Count;
                        if (examples.Count < 2) examples.Add(p);
                    }
                }
                if (occurrences > 0)
                {
                    grammarList.Add(new
                    {
                        id = ruleId,
                        label,
                        meaning,
                        occurrences,
                        examples
                    });
                }
            }
        }

        return new
        {
            bookId,
            chapterIndex,
            spineHref,
            chapterTitle,
            vocabulary = vocabList,
            kanji = kanjiList,
            grammar = grammarList
        };
    }

    private static Dictionary<string, (string Reading, string Meaning)> LoadCsvDeckForBook(string folderPath)
    {
        var result = new Dictionary<string, (string Reading, string Meaning)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folderPath)) return result;
        var csvFile = Directory.GetFiles(folderPath, "*.csv").FirstOrDefault();
        if (csvFile is null) return result;

        try
        {
            using var reader = new StreamReader(csvFile, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine)) return result;
            var headers = headerLine.Split(',').Select(h => h.Trim().Trim('"').ToLowerInvariant()).ToArray();
            int wordIdx = Array.FindIndex(headers, h => h is "word" or "expression" or "headword");
            int readingIdx = Array.FindIndex(headers, h => h is "readingkana" or "reading" or "kana");
            int defIdx = Array.FindIndex(headers, h => h is "definitions" or "definition" or "meaning" or "glossary");

            if (wordIdx < 0) return result;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (wordIdx < parts.Length)
                {
                    string w = parts[wordIdx].Trim().Trim('"');
                    string r = readingIdx >= 0 && readingIdx < parts.Length ? parts[readingIdx].Trim().Trim('"') : "";
                    string d = defIdx >= 0 && defIdx < parts.Length ? parts[defIdx].Trim().Trim('"') : "";
                    if (!string.IsNullOrEmpty(w) && !result.ContainsKey(w))
                    {
                        result[w] = (r, d);
                    }
                }
            }
        }
        catch { }
        return result;
    }

    private void EnrichWordsWithDictDb(
        Dictionary<string, (string Reading, string Meaning)> enrichedDefs,
        IReadOnlyList<string> words)
    {
        if (_dictDb is null || words.Count == 0) return;
        try
        {
            using var conn = _dictDb.CreateConnection();
            using var cmd = conn.CreateCommand();
            var pNames = new List<string>();
            for (int i = 0; i < words.Count; i++)
            {
                var p = "$p" + i;
                pNames.Add(p);
                cmd.Parameters.AddWithValue(p, words[i]);
            }
            cmd.CommandText = $"""
                SELECT expression, reading, glossary_text, glossary_json, dictionary_id, score
                FROM dictionary_terms
                WHERE expression IN ({string.Join(",", pNames)})
                ORDER BY (CASE WHEN dictionary_id LIKE 'yomitan%' THEN 1 ELSE 2 END) ASC, score DESC;
            """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string expr = reader.GetString(0);
                string reading = reader.IsDBNull(1) ? "" : reader.GetString(1);
                string glossText = reader.IsDBNull(2) ? "" : reader.GetString(2);
                string glossJson = reader.IsDBNull(3) ? "" : reader.GetString(3);
                string def = !string.IsNullOrWhiteSpace(glossText) ? glossText : glossJson;

                if (!enrichedDefs.TryGetValue(expr, out var cur) || string.IsNullOrEmpty(cur.Meaning))
                {
                    enrichedDefs[expr] = (string.IsNullOrEmpty(cur.Reading) ? reading : cur.Reading, def);
                }
            }
        }
        catch { }
    }

    private Dictionary<char, (List<string> Meanings, List<string> Onyomi, List<string> Kunyomi, int StrokeCount)> QueryKanjiDetails(IReadOnlyList<char> kanjiChars)
    {
        var result = new Dictionary<char, (List<string> Meanings, List<string> Onyomi, List<string> Kunyomi, int StrokeCount)>();
        if (_dictDb is null || kanjiChars.Count == 0) return result;
        try
        {
            using var conn = _dictDb.CreateConnection();
            using var cmd = conn.CreateCommand();
            var pNames = new List<string>();
            for (int i = 0; i < kanjiChars.Count; i++)
            {
                var p = "$k" + i;
                pNames.Add(p);
                cmd.Parameters.AddWithValue(p, kanjiChars[i].ToString());
            }
            cmd.CommandText = $"""
                SELECT character, on_readings_json, kun_readings_json, primary_meanings, stroke_count
                FROM dictionary_kanji
                WHERE character IN ({string.Join(",", pNames)});
            """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string chStr = reader.GetString(0);
                if (chStr.Length == 0) continue;
                char c = chStr[0];
                string onJson = reader.IsDBNull(1) ? "[]" : reader.GetString(1);
                string kunJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2);
                string primMeanings = reader.IsDBNull(3) ? "" : reader.GetString(3);
                int strokes = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);

                var onyomi = ParseJsonStringList(onJson);
                var kunyomi = ParseJsonStringList(kunJson);
                var meanings = ParseMeanings(primMeanings);

                result[c] = (meanings, onyomi, kunyomi, strokes);
            }
        }
        catch { }
        return result;
    }

    private static List<string> ParseJsonStringList(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "[]") return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch { return []; }
    }

    private static List<string> ParseMeanings(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        if (raw.StartsWith('[') && raw.EndsWith(']'))
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
            }
            catch { }
        }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
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

