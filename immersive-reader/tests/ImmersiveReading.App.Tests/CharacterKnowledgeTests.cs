using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Core.Knowledge;
using ImmersiveReading.Knowledge;
using ImmersiveReading.Plugins.CharacterKnowledge;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.App.Tests;

/// <summary>Spec 18/20: the C# half of the character knowledge graph (series numbering, text export, snapshot lookup, builder process).</summary>
public class CharacterKnowledgeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ck-tests-" + Guid.NewGuid().ToString("N"));

    public CharacterKnowledgeTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------ fixtures
    private static BookSummary Summary(string id, string title, string series = "", double? index = null, int chapters = 2) => new(
        id, "sha", null, null, title, title, series, index, 0, "", "", "", "", "", "", "ja", "", false, "", "", 0, null,
        chapters, 0, true, "horizontal-tb", "", "", null, false, 0, [], new Dictionary<string, EffectiveCustomFieldValue>());

    private sealed class FakeLibrary(params BookSummary[] books) : IHostLibrary
    {
        public Dictionary<string, int?> Reading { get; } = [];
        public PagedBookListResult ListBooks(BookListQuery query)
        {
            var items = books.Where(b => query.SeriesName is null || b.SeriesName == query.SeriesName).ToList();
            return new PagedBookListResult(items, items.Count, 0, query.Limit, 0);
        }
        public BookDetail? GetBookDetail(string bookId)
        {
            var b = books.FirstOrDefault(x => x.BookId == bookId);
            return b is null ? null : new BookDetail(b, null!, [], [], new Dictionary<string, string>());
        }
        public BookDetail UpdateBookMetadata(BookMetadataUpdateRequest request) => throw new NotSupportedException();
        public IReadOnlyList<TagRecord> ListTags() => [];
        public TagRecord SaveTag(string? tagId, string name, string colorHex = "#64748b", int sortOrder = 0) => throw new NotSupportedException();
        public int? GetReadingChapterIndex(string bookId) => Reading.GetValueOrDefault(bookId);
        public void SetCustomFieldValue(string entityType, string entityId, string fieldKey, string valueText, double? valueNumber = null) { }
    }

    private static string FindSchemaFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "specs", "fixtures", "knowledge-schema.sql");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("specs/fixtures/knowledge-schema.sql");
    }

    private static void Exec(SqliteConnection conn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>A knowledge base shaped like the builder's: snapshots at chapters 1 and 3 of volume 1, and 0 of volume 2.</summary>
    private string CreateKnowledgeDb()
    {
        string path = Path.Combine(_tempDir, "knowledge.db");
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            Exec(conn, File.ReadAllText(FindSchemaFile()));
            Exec(conn, "INSERT INTO chapters(volume, chapter, title, text_sha, status, ops_applied, built_at, model) VALUES (1,0,'One','aa','built',3,'t','m'), (1,1,'Two','bb','built',2,'t','m'), (1,2,'Three','cc','failed',0,'t','m')");
            Exec(conn, "INSERT INTO entities(id, kind, first_key, src_key) VALUES ('e1','person',0,0), ('e2','person',0,0)");
            void Snapshot(string id, string name, int volume, int chapter, int prominence, params string[] members)
            {
                Exec(conn, "INSERT INTO profile_snapshots VALUES ($id,$v,$c,$n,$p,$j,$b)",
                    ("$id", id), ("$v", volume), ("$c", chapter), ("$n", name), ("$p", prominence), ("$j", $"{{\"name\":\"{name}\",\"traits\":{{}}}}"), ("$b", $"block of {name} at {volume}:{chapter}"));
                foreach (string m in members.Prepend(id))
                    Exec(conn, "INSERT INTO profile_snapshot_members VALUES ($id,$v,$c,$m)", ("$id", id), ("$v", volume), ("$c", chapter), ("$m", m));
            }
            Snapshot("e1", "Aoi", 1, 1, 9);
            Snapshot("e1", "Aoi", 1, 3, 9);
            Snapshot("e2", "Masked man", 1, 3, 4);
            Snapshot("e1", "Aoi the Teacher", 2, 0, 9, "e2");     // e2 turned out to be the same person
        }
        return path;
    }

    // ------------------------------------------------------------------ KnowledgeDb
    [Fact]
    public void Db_MissingFile_IsEmptyNotAnError()
    {
        var db = new KnowledgeDb(Path.Combine(_tempDir, "nope.db"));
        Assert.Empty(db.ListCharacters(1, 5));
        Assert.Null(db.GetProfile(1, 5, "e1"));
        Assert.Empty(db.GetStatus().Chapters);
    }

    [Fact]
    public void Db_ListCharacters_UsesTheLatestBoundaryAtOrBeforeTheReader_NeverALaterOne()
    {
        var db = new KnowledgeDb(CreateKnowledgeDb());

        Assert.Empty(db.ListCharacters(1, 0));                                                   // nothing is known before chapter 2
        Assert.Equal(["Aoi"], db.ListCharacters(1, 1).Select(c => c.Name));
        Assert.Equal(["Aoi"], db.ListCharacters(1, 2).Select(c => c.Name));                      // falls back to the chapter-1 boundary
        Assert.Equal(["Aoi", "Masked man"], db.ListCharacters(1, 3).Select(c => c.Name));        // ordered by prominence
        Assert.Equal(["Aoi the Teacher"], db.ListCharacters(2, 0).Select(c => c.Name));
        Assert.Equal(["Aoi"], db.ListCharacters(1, 2).Select(c => c.Name));                      // an earlier position never sees volume 2
    }

    [Fact]
    public void Db_GetProfile_FindsAnyMemberOfTheIdentityGroup_AtTheRightTime()
    {
        var db = new KnowledgeDb(CreateKnowledgeDb());

        Assert.Equal("Masked man", db.GetProfile(1, 3, "e2")!.Name);
        Assert.Null(db.GetProfile(1, 2, "e2"));                                                  // the masked man does not exist yet
        var later = db.GetProfile(2, 0, "e2")!;                                                  // the old id still resolves, to the merged profile
        Assert.Equal("Aoi the Teacher", later.Name);
        Assert.Equal("e1", later.CharacterId);
        Assert.Contains("2:0", later.Block);
        Assert.Equal("Aoi the Teacher", later.Profile.GetProperty("name").GetString());
        Assert.Equal("Aoi", db.GetProfile(1, 2, "e1")!.Name);
    }

    [Fact]
    public void Db_Status_ReportsChaptersHashesAndCounts()
    {
        var status = new KnowledgeDb(CreateKnowledgeDb()).GetStatus();

        Assert.Equal(3, status.Chapters.Count);
        Assert.Equal("failed", status.Chapters[2].Status);
        Assert.Equal("aa", status.Chapters[0].TextSha);
        Assert.Equal(2, status.Counts.Entities);
        Assert.Equal(4, status.Counts.Snapshots);
    }

    [Fact]
    public void Db_DoesNotLockTheFile_SoTheFolderCanBeDeleted()
    {
        string path = CreateKnowledgeDb();
        new KnowledgeDb(path).GetStatus();
        File.Delete(path);                                    // would throw on Windows if a pooled handle were left open
        Assert.False(File.Exists(path));
    }

    // ------------------------------------------------------------------ story cards (spec 22)
    private static string Card(string id, string kind, string title, params string[][] participants)
    {
        string people = string.Join(",", participants.Select(ids => $"{{\"id\":\"{ids[0]}\",\"ids\":[{string.Join(",", ids.Select(i => $"\"{i}\""))}],\"name\":\"{ids[0]}\",\"kind\":\"character\",\"role\":\"\"}}"));
        return $"{{\"id\":\"{id}\",\"kind\":\"{kind}\",\"title\":\"{title}\",\"summary\":\"s\",\"participants\":[{people}]}}";
    }

    /// <summary>The loot-saga shape: a quest seen from 1:1 (its card changes at 1:3 when a second person becomes known), a battle from 1:3, items.</summary>
    private string CreateStoryDb()
    {
        string path = CreateKnowledgeDb();
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        void EventCard(string id, int volume, int chapter, int sort, string json)
            => Exec(conn, "INSERT INTO story_event_cards VALUES ($id,$v,$c,$s,$j)", ("$id", id), ("$v", volume), ("$c", chapter), ("$s", sort), ("$j", json));
        EventCard("v1", 1, 1, 10, Card("v1", "quest", "Quest (early)", ["e1"]));
        EventCard("v1", 1, 3, 10, Card("v1", "quest", "Quest (updated)", ["e1"], ["e2"]));
        EventCard("v2", 1, 3, 20, Card("v2", "battle", "Battle", ["e2"]));
        void ItemCard(string id, int volume, int chapter, string holder, string json)
            => Exec(conn, "INSERT INTO story_item_cards VALUES ($id,$v,$c,0,$j)", ("$id", id), ("$v", volume), ("$c", chapter), ("$j", json));
        ItemCard("i1", 1, 1, "e1", "{\"id\":\"i1\",\"name\":\"Knife\",\"holder\":{\"id\":\"e1\",\"ids\":[\"e1\"],\"name\":\"Aoi\"}}");
        ItemCard("i1", 1, 3, "e2", "{\"id\":\"i1\",\"name\":\"Knife\",\"holder\":{\"id\":\"e2\",\"ids\":[\"e2\"],\"name\":\"Masked man\"}}");
        ItemCard("i2", 1, 3, "", "{\"id\":\"i2\",\"name\":\"Ring\",\"holder\":null}");
        Exec(conn, "INSERT INTO story_chapters(volume, chapter, text_sha, status) VALUES (1,0,'aa','built'), (1,1,'bb','failed')");
        return path;
    }

    private static string[] Titles(StoryEventList list) => list.Events.Select(e => e.GetProperty("title").GetString()!).ToArray();

    [Fact]
    public void Db_ListEvents_ShowsEachEventAsItsLatestCardAtOrBeforeTheReader_NewestFirst()
    {
        var db = new KnowledgeDb(CreateStoryDb());

        Assert.Empty(db.ListEvents(1, 0).Events);                                           // nothing is known before chapter 2
        Assert.Equal(["Quest (early)"], Titles(db.ListEvents(1, 1)));
        Assert.Equal(["Quest (early)"], Titles(db.ListEvents(1, 2)));                       // no newer card yet: the older one, never the later one
        Assert.Equal(["Battle", "Quest (updated)"], Titles(db.ListEvents(1, 3)));           // newest first, one card per event
        Assert.Equal(["Quest (early)"], Titles(db.ListEvents(1, 2)));                       // an earlier reader still sees only the old card
    }

    [Fact]
    public void Db_ListEvents_FiltersByKindAndCharacter_AndLimitsAfterFiltering()
    {
        var db = new KnowledgeDb(CreateStoryDb());

        Assert.Equal(["Battle"], Titles(db.ListEvents(1, 3, kind: "battle")));
        Assert.Equal(["Battle", "Quest (updated)"], Titles(db.ListEvents(1, 3, characterId: "e2")));
        Assert.Equal(["Quest (updated)"], Titles(db.ListEvents(1, 3, characterId: "e1")));
        Assert.Equal(["Quest (updated)"], Titles(db.ListEvents(1, 3, characterId: "e1", kind: "quest")));
        var limited = db.ListEvents(1, 3, limit: 1);
        Assert.Equal((2, 1), (limited.Total, limited.Events.Count));
        // after the identity reveal e1 and e2 are one character: either id finds the events of both
        Assert.Equal(["Battle", "Quest (updated)"], Titles(db.ListEvents(2, 0, characterId: "e1")));
        Assert.Equal(["Battle", "Quest (updated)"], Titles(db.ListEvents(2, 0, characterId: "e2")));
    }

    [Fact]
    public void Db_ListItems_FollowsTheHolder_AndFiltersByTheCharacterWhoHasThem()
    {
        var db = new KnowledgeDb(CreateStoryDb());

        Assert.Equal(["Knife"], db.ListItems(1, 1).Select(i => i.GetProperty("name").GetString()));
        Assert.Equal("Aoi", db.ListItems(1, 2)[0].GetProperty("holder").GetProperty("name").GetString());        // older card until 1:3
        var later = db.ListItems(1, 3);
        Assert.Equal(["Knife", "Ring"], later.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal("Masked man", later[0].GetProperty("holder").GetProperty("name").GetString());
        Assert.Equal(["Knife"], db.ListItems(1, 3, "e2").Select(i => i.GetProperty("name").GetString()));
        Assert.Empty(db.ListItems(1, 3, "e1"));                                                                  // Aoi no longer has the knife
        Assert.Equal(["Knife"], db.ListItems(2, 0, "e1").Select(i => i.GetProperty("name").GetString()));         // same character after the reveal
    }

    [Fact]
    public void Db_Status_ReportsTheStoryPass()
    {
        var status = new KnowledgeDb(CreateStoryDb()).GetStatus();

        Assert.Equal(["built", "failed"], status.StoryChapters.Select(c => c.Status));
        Assert.Equal(2, status.Counts.EventCards);
        Assert.Equal(2, status.Counts.ItemCards);
    }

    [Fact]
    public void Db_WithoutStoryTables_IsJustWithoutStoryData()
    {
        string path = CreateKnowledgeDb();
        using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            conn.Open();
            foreach (string table in new[] { "story_event_cards", "story_item_cards", "story_chapters" }) Exec(conn, $"DROP TABLE {table}");
        }
        var db = new KnowledgeDb(path);

        Assert.Empty(db.ListEvents(1, 3).Events);
        Assert.Empty(db.ListItems(1, 3));
        var status = db.GetStatus();
        Assert.Equal(3, status.Chapters.Count);                                       // the character data is unaffected
        Assert.Empty(status.StoryChapters);
        Assert.Equal(0, status.Counts.EventCards);
    }

    // ------------------------------------------------------------------ SeriesResolver
    [Fact]
    public void Series_StandaloneBookIsVolumeOneOfItsOwnKnowledgeBase()
    {
        var series = new SeriesResolver(new FakeLibrary(Summary("b1", "Lonely"))).ResolveForBook("b1");

        Assert.False(series.IsSeries);
        Assert.Equal(SeriesResolver.KeyForBook("b1"), series.SeriesKey);
        Assert.Equal(1, series.VolumeOf("b1"));
    }

    [Fact]
    public void Series_VolumesAreNumberedInReadingOrder_AndTheKeyIsStable()
    {
        var library = new FakeLibrary(Summary("b3", "Saga 3", "Saga", 3), Summary("b1", "Saga 1", "Saga", 1), Summary("b2", "Saga 2", "Saga", 2));
        var series = new SeriesResolver(library).ResolveForBook("b2");

        Assert.Equal(["b1", "b2", "b3"], series.Volumes.Select(v => v.BookId));
        Assert.Equal(2, series.VolumeOf("b2"));
        Assert.Equal(SeriesResolver.KeyForSeries("Saga"), series.SeriesKey);
        Assert.Equal(SeriesResolver.KeyForSeries("Saga"), SeriesResolver.KeyForSeries("  saga "));
        Assert.False(series.OrderChanged);
    }

    [Fact]
    public void Series_AddingALaterVolume_DoesNotRenumberBuiltOnes()
    {
        var stored = new Dictionary<string, int> { ["b1"] = 1, ["b2"] = 2 };
        var library = new FakeLibrary(Summary("b1", "Saga 1", "Saga", 1), Summary("b2", "Saga 2", "Saga", 2), Summary("b3", "Saga 3", "Saga", 3));
        var series = new SeriesResolver(library, _ => stored).ResolveForSeries("Saga");

        Assert.Equal(3, series.VolumeOf("b3"));
        Assert.False(series.OrderChanged);
    }

    [Fact]
    public void Series_ANewVolumeThatSortsBeforeBuiltOnes_KeepsTheirNumbersAndFlagsARebuild()
    {
        var stored = new Dictionary<string, int> { ["b2"] = 1, ["b3"] = 2 };      // built when only volumes 2 and 3 existed
        var library = new FakeLibrary(Summary("b1", "Saga 1", "Saga", 1), Summary("b2", "Saga 2", "Saga", 2), Summary("b3", "Saga 3", "Saga", 3));
        var series = new SeriesResolver(library, _ => stored).ResolveForSeries("Saga");

        Assert.Equal(1, series.VolumeOf("b2"));                                    // built boundaries stay valid...
        Assert.Equal(2, series.VolumeOf("b3"));
        Assert.Equal(3, series.VolumeOf("b1"));
        Assert.True(series.OrderChanged);                                          // ...but the story order no longer matches them
    }

    [Fact]
    public void Series_UnknownBook_Throws() => Assert.Throws<KeyNotFoundException>(() => new SeriesResolver(new FakeLibrary()).ResolveForBook("zzz"));

    // ------------------------------------------------------------------ BookTextExporter
    private sealed class FakeCatalog(Dictionary<string, (BookDetail Detail, string Epub)> books) : IBookCatalog
    {
        public BookDetail? GetBookDetail(string bookId) => books.TryGetValue(bookId, out var b) ? b.Detail : null;
        public BookSummary? GetBookSummary(string bookId) => GetBookDetail(bookId)?.Summary;
        public string? GetEpubFilePath(string bookId) => books.TryGetValue(bookId, out var b) ? b.Epub : null;
    }

    private string MakeEpub(string name, params (string Href, string Body)[] files)
    {
        string path = Path.Combine(_tempDir, name + ".epub");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (href, body) in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(href).Open(), new System.Text.UTF8Encoding(false));
            writer.Write($"<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\"><body>{body}</body></html>");
        }
        return path;
    }

    private static BookChapterRecord Chapter(string book, int index, string title, params string[] members)
        => new(book, index, $"{book}-{index}", title, members[0], MemberSpineHrefs: members);

    [Fact]
    public void Exporter_WritesChapterTextWithoutRuby_AndEachFileOnlyOnce()
    {
        string epub = MakeEpub("b1",
            ("a.xhtml", "<p>こんにちは、<ruby>青木<rt>あおき</rt></ruby>です。</p><p>二行目。</p>"),
            ("b.xhtml", "<p>次の章。</p>"));
        var detail = new BookDetail(Summary("b1", "Book One"), null!,
            [Chapter("b1", 0, "Start", "a.xhtml"), Chapter("b1", 1, "Next", "a.xhtml", "b.xhtml")], [], new Dictionary<string, string>());
        var series = new SeriesResolver(new FakeLibrary(detail.Summary)).ResolveForBook("b1");

        var exported = new BookTextExporter(new FakeCatalog(new() { ["b1"] = (detail, epub) })).Export(series);

        Assert.Equal(2, exported.Chapters);
        var chapters = exported.Json["volumes"]![0]!["chapters"]!.AsArray();
        Assert.Equal("こんにちは、青木です。\n二行目。\n", chapters[0]!["text"]!.GetValue<string>());
        Assert.Equal("次の章。\n", chapters[1]!["text"]!.GetValue<string>());     // a.xhtml already belongs to chapter 0
        Assert.Equal("ja", exported.Json["language"]!.GetValue<string>());
        Assert.Equal(series.SeriesKey, exported.Json["series_key"]!.GetValue<string>());
        Assert.Equal(BookTextExporter.HashOf("次の章。\n"), exported.ChapterHashes[(1, 1)]);
    }

    [Fact]
    public void Exporter_StopsAtTheRequestedVolume_AndSkipsExcludedChapters()
    {
        string e1 = MakeEpub("v1", ("a.xhtml", "<p>一巻。</p>"), ("x.xhtml", "<p>目次</p>"));
        string e2 = MakeEpub("v2", ("a.xhtml", "<p>二巻。</p>"));
        var s1 = Summary("v1", "Saga 1", "Saga", 1);
        var s2 = Summary("v2", "Saga 2", "Saga", 2);
        var d1 = new BookDetail(s1, null!, [Chapter("v1", 0, "A", "a.xhtml"), Chapter("v1", 1, "Toc", "x.xhtml") with { IsExcluded = true }], [], new Dictionary<string, string>());
        var d2 = new BookDetail(s2, null!, [Chapter("v2", 0, "A", "a.xhtml")], [], new Dictionary<string, string>());
        var series = new SeriesResolver(new FakeLibrary(s1, s2)).ResolveForSeries("Saga");
        var exporter = new BookTextExporter(new FakeCatalog(new() { ["v1"] = (d1, e1), ["v2"] = (d2, e2) }));

        var onlyFirst = exporter.Export(series, uptoVolume: 1);
        var both = exporter.Export(series);

        Assert.Single(onlyFirst.Json["volumes"]!.AsArray());
        Assert.Equal(1, onlyFirst.Chapters);                  // the excluded table of contents is not exported
        Assert.Equal(2, both.Chapters);
        Assert.Equal(2, both.ChapterHashes.Keys.Max(k => k.Volume));
    }

    [Fact]
    public void Exporter_HashMatchesThePythonBuilder()
    {
        // sha256("abc") is a published test vector; the builder's text_sha is the same function over the UTF-8 text.
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", BookTextExporter.HashOf("abc"));
    }

    // ------------------------------------------------------------------ BuilderLocator
    [Fact]
    public void Locator_FindsTheBuilderUpwardAndSetupProblemsNameTheCommand()
    {
        string root = Path.Combine(_tempDir, "repo");
        string deep = Path.Combine(root, "immersive-reader", "bin");
        Directory.CreateDirectory(deep);
        var none = BuilderLocator.Locate(new CharacterKnowledgeSettings(), deep);
        Assert.False(none.Found);
        Assert.Contains("knowledge-graph", none.Problem);

        Directory.CreateDirectory(Path.Combine(root, "knowledge-graph", "immersive_knowledge"));
        var noVenv = BuilderLocator.Locate(new CharacterKnowledgeSettings(), deep);
        Assert.False(noVenv.Found);
        Assert.Contains("venv", noVenv.Problem, StringComparison.OrdinalIgnoreCase);

        string python = Path.Combine(root, "knowledge-graph", ".venv", "Scripts");
        Directory.CreateDirectory(python);
        File.WriteAllText(Path.Combine(python, "python.exe"), "");
        var found = BuilderLocator.Locate(new CharacterKnowledgeSettings(), deep);
        Assert.True(found.Found);
        Assert.EndsWith("knowledge-graph", found.Directory);
    }

    // ------------------------------------------------------------------ KnowledgeBuildRunner
    private static BuildJob JobWith(params string[] lines)
    {
        var job = new BuildJob { SeriesKey = "k", Total = 3 };
        foreach (string line in lines) KnowledgeBuildRunner.Handle(job, line);
        return job;
    }

    [Fact]
    public void Runner_ProgressEventsUpdateTheJob()
    {
        var job = JobWith(
            """{"event":"build_start","model":"qwen"}""",
            """{"event":"chapter_start","volume":1,"chapter":0,"title":"One"}""",
            """{"event":"context_limit_learned","tokens":8241}""",
            """{"event":"part_split","volume":1,"chapter":0}""",
            """{"event":"chapter_done","volume":1,"chapter":0,"ops_applied":5,"ops_rejected":1}""",
            """{"event":"chapter_skipped","volume":1,"chapter":1}""",
            """{"event":"snapshots_done","stored":7,"blocked":0}""",
            "not json at all");

        Assert.Equal("qwen", job.Model);
        Assert.Equal(2, job.Done);
        Assert.Equal(1, job.Skipped);
        Assert.Equal(5, job.OpsApplied);
        Assert.Equal(1, job.OpsRejected);
        Assert.Equal(1, job.Splits);
        Assert.Equal(8241, job.ContextTokensLearned);
        Assert.Equal(7, job.SnapshotsStored);
        Assert.Equal("One", job.CurrentTitle);
        Assert.Contains("not json at all", job.Log);        // unknown lines are kept, never fatal
    }

    [Fact]
    public void Runner_FailureEventsSetTheError()
    {
        var job = JobWith("""{"event":"chapter_failed","volume":1,"chapter":2,"reason":"truncated"}""");
        Assert.Contains("1:2", job.Error);
        Assert.Contains("truncated", job.Error);
    }

    [Fact]
    public void Runner_DefaultStartInfo_NeverPutsTheKeyOnTheCommandLine()
    {
        var location = new BuilderLocation(_tempDir, Path.Combine(_tempDir, "python.exe"), null);
        var info = new KnowledgeBuildRunner(location).DefaultStartInfo(new BuildRequest("k", "db", "text.json", "cfg.json", "sk-secret", Rebuild: true, FromPosition: "1:2"));

        Assert.DoesNotContain(info.ArgumentList, a => a.Contains("sk-secret"));
        Assert.Equal("sk-secret", info.Environment["KNOWLEDGE_API_KEY"]);
        Assert.Contains("--no-resume", info.ArgumentList);
        Assert.Contains("1:2", info.ArgumentList);
        Assert.Equal("immersive_knowledge", info.ArgumentList[1]);
    }

    [Fact]
    public async Task Runner_RunsAProcessToTheEnd_AndMapsExitCodes()
    {
        string script = Path.Combine(_tempDir, "fake-builder.ps1");
        File.WriteAllText(script, """
            Write-Output '{"event":"build_start","model":"fake"}'
            Write-Output '{"event":"chapter_done","volume":1,"chapter":0,"ops_applied":2,"ops_rejected":0}'
            exit 0
            """);
        ProcessStartInfo Info(BuildRequest r)
        {
            var info = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-NoProfile", "-File", script }) info.ArgumentList.Add(a);
            return info;
        }

        var job = new KnowledgeBuildRunner(new BuilderLocation(null, null, "x"), Info).Start(new BuildRequest("k", "db", "t", "c", null), 1);
        await WaitFor(() => job.State != "running");

        Assert.Equal("done", job.State);
        Assert.Equal(1, job.Done);
        Assert.Equal("fake", job.Model);
        Assert.Equal(0, job.ExitCode);
    }

    [Fact]
    public async Task Runner_CancelKillsTheProcess_AndTheJobIsCancelledNotFailed()
    {
        ProcessStartInfo Info(BuildRequest r)
        {
            var info = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" }) info.ArgumentList.Add(a);
            return info;
        }

        var job = new KnowledgeBuildRunner(new BuilderLocation(null, null, "x"), Info).Start(new BuildRequest("k", "db", "t", "c", null), 1);
        Assert.Equal("running", job.State);
        KnowledgeBuildRunner.Cancel(job);
        await WaitFor(() => job.State != "running");

        Assert.Equal("cancelled", job.State);
    }

    [Fact]
    public async Task Runner_ABuilderThatCannotStart_FailsTheJobWithAMessage()
    {
        var job = new KnowledgeBuildRunner(new BuilderLocation(null, null, "x"), _ => new ProcessStartInfo(Path.Combine(_tempDir, "missing.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })
            .Start(new BuildRequest("k", "db", "t", "c", null), 1);
        await WaitFor(() => job.State != "running");

        Assert.Equal("failed", job.State);
        Assert.Contains("could not be started", job.Error);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("condition not reached");
            await Task.Delay(50);
        }
    }

    // ------------------------------------------------------------------ plugin helpers
    private sealed class ConfigStub : ImmersiveReading.Core.Config.IConfigurationManager
    {
        public ImmersiveReading.Core.Config.AppConfig CurrentConfig { get; } = ImmersiveReading.Core.Config.AppConfig.CreateDefault();
        public ImmersiveReading.Core.Config.AppConfig GetConfig() => CurrentConfig;
        public Task<ImmersiveReading.Core.Config.AppConfig> UpdateConfigAsync(ImmersiveReading.Core.Config.AppConfig newConfig, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ImmersiveReading.Core.Config.AppConfig> UpdateCategoryJsonAsync(string category, string categoryJson, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ImmersiveReading.Core.Config.AppConfig> ResetDefaultsAsync(string? category = null, CancellationToken ct = default) => throw new NotSupportedException();
        public event EventHandler<ImmersiveReading.Core.Config.AppConfig>? ConfigChanged { add { } remove { } }
    }

    [Fact]
    public void UpToDate_CountsLeadingUnchangedBuiltChapters_ThenStops()
    {
        var chapters = new List<ChapterState>
        {
            new(1, 0, "", "built", 0, 0, "", "", "", "h0"),
            new(1, 1, "", "built", 0, 0, "", "", "", "OLD"),     // changed text: it and everything after must be redone
            new(1, 2, "", "built", 0, 0, "", "", "", "h2")
        };
        var hashes = new Dictionary<(int, int), string> { [(1, 0)] = "h0", [(1, 1)] = "h1", [(1, 2)] = "h2", [(1, 3)] = "h3" };
        var exported = new ExportedBook(new System.Text.Json.Nodes.JsonObject(), 4, 0, hashes);

        Assert.Equal(1, CharacterKnowledgePlugin.UpToDateChapters(new KnowledgeStatus(chapters, new KnowledgeCounts(0, 0, 0, 0)), exported));
    }

    [Fact]
    public void UpToDateStory_NeedsTheCharacterAndTheStoryStateOfEveryChapterBeforeIt()
    {
        var characters = new List<ChapterState>
        {
            new(1, 0, "", "built", 0, 0, "", "", "", "h0"),
            new(1, 1, "", "built", 0, 0, "", "", "", "h1"),
            new(1, 2, "", "built", 0, 0, "", "", "", "h2")
        };
        var story = new List<StoryChapterState>
        {
            new(1, 0, "built", "", "h0"),
            new(1, 1, "failed", "x", "h1"),        // failed: redo it and everything after
            new(1, 2, "built", "", "h2")
        };
        var hashes = new Dictionary<(int, int), string> { [(1, 0)] = "h0", [(1, 1)] = "h1", [(1, 2)] = "h2" };
        var exported = new ExportedBook(new System.Text.Json.Nodes.JsonObject(), 3, 0, hashes);
        var status = new KnowledgeStatus(characters, new KnowledgeCounts(0, 0, 0, 0), story);

        Assert.Equal(1, CharacterKnowledgePlugin.UpToDateStoryChapters(status, exported));
        Assert.Equal(3, CharacterKnowledgePlugin.UpToDateChapters(status, exported));        // the characters themselves are all current

        story[1] = new(1, 1, "built", "", "OLD");                                           // built for other text: stale
        Assert.Equal(1, CharacterKnowledgePlugin.UpToDateStoryChapters(new KnowledgeStatus(characters, new KnowledgeCounts(0, 0, 0, 0), story), exported));
        Assert.Equal(0, CharacterKnowledgePlugin.UpToDateStoryChapters(new KnowledgeStatus(characters, new KnowledgeCounts(0, 0, 0, 0)), exported));   // an old database: no story state
    }

    [Fact]
    public void Runner_StoryProgressCountsTowardsTheSameBar_AndKeepsItsOwnTotals()
    {
        var job = new BuildJob { SeriesKey = "k", Total = 4 };
        foreach (string line in new[]
        {
            """{"event":"chapter_done","volume":1,"chapter":0,"ops_applied":5,"ops_rejected":0}""",
            """{"event":"chapter_skipped","volume":1,"chapter":1}""",
            """{"event":"story_start","volume":1,"chapter":0,"title":"Quest"}""",
            """{"event":"story_done","volume":1,"chapter":0,"ops_applied":4,"ops_rejected":1,"replaced_events":1}""",
            """{"event":"story_skipped","volume":1,"chapter":1}"""
        }) KnowledgeBuildRunner.Handle(job, line);

        Assert.Equal((4, 2, 5, 4, 1), (job.Done, job.Skipped, job.OpsApplied, job.StoryOpsApplied, job.StoryOpsRejected));
        Assert.Equal("story", job.Phase);
        KnowledgeBuildRunner.Handle(job, """{"event":"story_failed","volume":1,"chapter":2,"reason":"boom"}""");
        Assert.Contains("Story pass of chapter 1:2 failed: boom", job.Error);
        KnowledgeBuildRunner.Handle(job, """{"event":"snapshots_done","stored":7,"blocked":1,"events":9,"items":3,"story_blocked":2}""");
        Assert.Equal((7, 3, 9, 3), (job.SnapshotsStored, job.SnapshotsBlocked, job.EventCards, job.ItemCards));
    }

    [Fact]
    public void Config_AsksForTheStoryPassUnlessItIsSwitchedOff()
    {
        var plugin = new CharacterKnowledgePlugin(new ConfigStub());

        Assert.True(plugin.BuilderConfig()["story_pass"]!.GetValue<bool>());
    }

    [Fact]
    public void Config_NeverContainsTheApiKey_AndFallsBackToSettingsAi()
    {
        var plugin = new CharacterKnowledgePlugin(new ConfigStub());
        var config = plugin.BuilderConfig();

        Assert.False(config.ContainsKey("api_key"));
        Assert.DoesNotContain("secret", config.ToJsonString());
        Assert.Equal("http://127.0.0.1:1919/v1", config["endpoint"]!.GetValue<string>());
        Assert.Equal("off", config["thinking"]!.GetValue<string>());
        Assert.Equal("Japanese", config["output_language"]!.GetValue<string>());
        Assert.False(config.ContainsKey("context_tokens"));          // unknown limit: the builder discovers it
    }

    // ------------------------------------------------------------------ the story graph as a knowledge source (spec 24)
    private (KnowledgeHub Hub, StoryKnowledgeSource Source) StoryHub()
    {
        string path = CreateStoryDb();
        var resolver = new SeriesResolver(new FakeLibrary(Summary("b1", "Book one")));
        var source = new StoryKnowledgeSource(resolver.ResolveForBook, _ => new KnowledgeDb(path));
        var hub = new KnowledgeHub();
        hub.RegisterSource(source);
        return (hub, source);
    }

    [Fact]
    public async Task StorySource_RefusesAQueryWithoutAViewpoint_EvenThroughTheHub()
    {
        var (hub, source) = StoryHub();
        var query = new KnowledgeQuery(KnowledgeScope.Book("b1"), "knife");

        await Assert.ThrowsAsync<KnowledgeException>(() => hub.QueryAsync(query));
        await Assert.ThrowsAsync<KnowledgeException>(() => hub.QueryAsync(query with { Viewpoint = KnowledgeViewpoint.Unrestricted }));
        await Assert.ThrowsAsync<KnowledgeException>(() => source.QueryAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task StorySource_ShowsOnlyWhatTheReaderKnowsAtTheViewpoint()
    {
        var (hub, _) = StoryHub();
        KnowledgeQuery At(int chapter, string text) => new(KnowledgeScope.Book("b1"), text, Viewpoint: KnowledgeViewpoint.AtChapter(chapter));

        var early = await hub.QueryAsync(At(1, "knife"));
        var late = await hub.QueryAsync(At(3, "knife"));

        var knifeEarly = Assert.Single(early, e => e.Kind == "item");
        var knifeLate = Assert.Single(late, e => e.Kind == "item");
        Assert.Contains("Aoi", knifeEarly.Text);
        Assert.DoesNotContain("Masked man", knifeEarly.Text);          // the later holder is a spoiler for the early reader
        Assert.Contains("Masked man", knifeLate.Text);
        Assert.Empty(await hub.QueryAsync(At(0, "knife")));            // nothing is known before the first chapter is read
    }

    [Fact]
    public async Task StorySource_ListsEventsNewestFirst_CharactersWithTheirProfileBlock_AndNothingForAnUnknownBook()
    {
        var (hub, _) = StoryHub();
        var view = KnowledgeViewpoint.AtChapter(3);

        var events = await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Book("b1"), Kinds: ["event"], Viewpoint: view));
        var people = await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Book("b1"), "Aoi", ["character"], view));
        var unknown = await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Book("nope"), Viewpoint: view));

        Assert.Equal(["Battle", "Quest (updated)"], events.Select(e => e.Title).ToArray());
        Assert.Contains(people, c => c.Title == "Aoi" && c.Text.StartsWith("block of Aoi"));
        Assert.Empty(unknown);
    }

    [Fact]
    public void Hub_DescribesTheStoryGraphAsGatedAndReadOnly_AndForgetsItWhenUnregistered()
    {
        var hub = new KnowledgeHub();
        Assert.Empty(hub.Sources);
        using var registration = hub.RegisterSource(new StoryKnowledgeSource(_ => throw new KeyNotFoundException(), _ => new KnowledgeDb("none")));

        var story = Assert.Single(hub.Sources);
        Assert.Equal((StoryKnowledgeSource.Id, KnowledgeGating.StoryPosition, false), (story.SourceId, story.Gating, story.Writable));
        registration.Dispose();
        Assert.Empty(hub.Sources);
    }
}
