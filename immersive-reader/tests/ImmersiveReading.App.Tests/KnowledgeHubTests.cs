using ImmersiveReading.Core.Knowledge;
using ImmersiveReading.Knowledge;
using Xunit;

namespace ImmersiveReading.App.Tests;

/// <summary>Spec 24: the knowledge hub (routing, gating, failure isolation) and the user's own memory.</summary>
public class KnowledgeHubTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "km-tests-" + Guid.NewGuid().ToString("N"));

    public KnowledgeHubTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private UserMemorySource Memory(string name = "memory.db") => new(Path.Combine(_dir, name));

    private sealed class FakeSource(string id, string kind, KnowledgeGating gating, params KnowledgeEntry[] entries) : IKnowledgeSource
    {
        public int Calls;
        public Exception? Fails;
        public string SourceId => id;
        public string DisplayName => id;
        public IReadOnlyList<string> ScopeKinds { get; } = [kind];
        public KnowledgeGating Gating => gating;
        public Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct)
        {
            Calls++;
            if (Fails is not null) throw Fails;
            return Task.FromResult<IReadOnlyList<KnowledgeEntry>>(entries);
        }
    }

    private static KnowledgeEntry Entry(string source, string id, double relevance, string kind = "concept")
        => new(source, KnowledgeScope.Subject("physics"), id, kind, id, id, relevance);

    // ------------------------------------------------------------------ hub
    [Fact]
    public async Task Hub_MergesSourcesOfTheSameScopeBestFirst_AndHonoursTheLimit()
    {
        var hub = new KnowledgeHub();
        hub.RegisterSource(new FakeSource("a", "subject", KnowledgeGating.None, Entry("a", "low", 0.3), Entry("a", "top", 0.9)));
        hub.RegisterSource(new FakeSource("b", "subject", KnowledgeGating.None, Entry("b", "mid", 0.6)));
        hub.RegisterSource(new FakeSource("other", "user", KnowledgeGating.None, Entry("other", "elsewhere", 1)));

        var found = await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Subject("physics"), Limit: 2));

        Assert.Equal(["top", "mid"], found.Select(e => e.Id).ToArray());     // another scope kind is never asked
    }

    [Fact]
    public async Task Hub_NeverAsksAGatedSourceWithoutAViewpoint()
    {
        var hub = new KnowledgeHub();
        var gated = new FakeSource("story", "book", KnowledgeGating.StoryPosition);
        hub.RegisterSource(gated);

        await Assert.ThrowsAsync<KnowledgeException>(() => hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Book("b"))));
        Assert.Equal(0, gated.Calls);
        await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Book("b"), Viewpoint: KnowledgeViewpoint.AtChapter(2)));
        Assert.Equal(1, gated.Calls);
    }

    [Fact]
    public async Task Hub_OneBrokenSourceDoesNotTakeTheOthersDown()
    {
        var hub = new KnowledgeHub();
        hub.RegisterSource(new FakeSource("broken", "subject", KnowledgeGating.None) { Fails = new InvalidOperationException("disk gone") });
        hub.RegisterSource(new FakeSource("fine", "subject", KnowledgeGating.None, Entry("fine", "ok", 0.5)));

        var found = await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.Subject("physics")));

        Assert.Equal(["ok"], found.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void Hub_RejectsADuplicateSourceId_AndForgetsASourceWhenItsRegistrationIsDisposed()
    {
        var hub = new KnowledgeHub();
        using var first = hub.RegisterSource(new FakeSource("a", "subject", KnowledgeGating.None));

        Assert.Throws<InvalidOperationException>(() => hub.RegisterSource(new FakeSource("a", "subject", KnowledgeGating.None)));
        first.Dispose();
        first.Dispose();                                                    // harmless twice
        Assert.Empty(hub.Sources);
        hub.RegisterSource(new FakeSource("a", "subject", KnowledgeGating.None));   // the id is free again
    }

    [Fact]
    public async Task Hub_RemembersThroughTheWritableSourceOfTheScope_AndSaysSoWhenThereIsNone()
    {
        var hub = new KnowledgeHub();
        await Assert.ThrowsAsync<KnowledgeException>(() => hub.RememberAsync(new FactInput(KnowledgeScope.User, "I like tea")));

        hub.RegisterSource(Memory());
        var stored = await hub.RememberAsync(new FactInput(KnowledgeScope.User, "I like tea"));

        Assert.Equal(UserMemorySource.Id, stored.SourceId);
        Assert.True(await hub.ForgetAsync(UserMemorySource.Id, stored.Id));
        Assert.False(await hub.ForgetAsync(UserMemorySource.Id, stored.Id));
        Assert.False(await hub.ForgetAsync("no-such-source", stored.Id));
        Assert.True(Assert.Single(hub.Sources).Writable);
    }

    // ------------------------------------------------------------------ user memory
    private static Task<IReadOnlyList<KnowledgeEntry>> Ask(IKnowledgeSource source, string? text, string? audience = null, int limit = 10)
        => source.QueryAsync(new KnowledgeQuery(KnowledgeScope.User, text, Audience: audience, Limit: limit), CancellationToken.None);

    [Fact]
    public async Task Memory_RecallsAFactByItsWords_InJapaneseAndEnglish_AndLeavesUnrelatedFactsOut()
    {
        var memory = Memory();
        await memory.RememberAsync(new FactInput(KnowledgeScope.User, "私の妹の名前はユナです"), default);
        await memory.RememberAsync(new FactInput(KnowledgeScope.User, "I work as a nurse at night"), default);
        await memory.RememberAsync(new FactInput(KnowledgeScope.User, "好きな食べ物はラーメンです"), default);

        var sister = await Ask(memory, "妹の名前は？");
        var job = await Ask(memory, "what is my job as a nurse");

        Assert.Equal("私の妹の名前はユナです", sister[0].Text);
        Assert.DoesNotContain(sister, e => e.Text.Contains("ラーメン"));
        Assert.Equal("I work as a nurse at night", job[0].Text);
        Assert.Empty(await Ask(memory, "quantum chromodynamics"));
    }

    [Fact]
    public async Task Memory_TheSameFactToldAgainIsRefreshedNotDuplicated_EvenWithDifferentSpacingAndWidth()
    {
        var memory = Memory();
        var first = await memory.RememberAsync(new FactInput(KnowledgeScope.User, "My cat is called Mochi"), default);
        var again = await memory.RememberAsync(new FactInput(KnowledgeScope.User, "  my cat is called  ＭＯＣＨＩ! ", Subject: "pet"), default);

        Assert.Equal(first.Id, again.Id);
        var all = await Ask(memory, null);
        var only = Assert.Single(all);
        Assert.Equal("pet", only.Title);
    }

    [Fact]
    public async Task Memory_AFactSharedWithOnePersonaIsInvisibleToTheOthers_ButTheOwnerSeesAll()
    {
        var memory = Memory();
        await memory.RememberAsync(new FactInput(KnowledgeScope.User, "My name is Ren"), default);
        await memory.RememberAsync(new FactInput(KnowledgeScope.User, "I am afraid of exams", SharedWith: ["persona:yuna"]), default);

        Assert.Equal(2, (await Ask(memory, null)).Count);                                 // the owner
        Assert.Equal(2, (await Ask(memory, null, "persona:yuna")).Count);
        var kai = await Ask(memory, null, "persona:kai");
        Assert.Equal("My name is Ren", Assert.Single(kai).Text);
        Assert.Empty(await Ask(memory, "afraid of exams", "persona:kai"));                // not even by searching for it
    }

    [Fact]
    public async Task Memory_ListingIsNewestFirst_ForgettingReallyDeletes_AndFactsSurviveARestart()
    {
        var clock = new FakeTime(new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));
        string path = Path.Combine(_dir, "restart.db");
        var memory = new UserMemorySource(path, clock);
        var a = await memory.RememberAsync(new FactInput(KnowledgeScope.User, "first fact"), default);
        clock.Advance(TimeSpan.FromMinutes(5));
        await memory.RememberAsync(new FactInput(KnowledgeScope.User, "second fact"), default);

        var reopened = new UserMemorySource(path, clock);
        Assert.Equal(["second fact", "first fact"], (await Ask(reopened, null)).Select(e => e.Text).ToArray());
        Assert.True(await reopened.ForgetAsync(a.Id, default));
        Assert.Equal(["second fact"], (await Ask(new UserMemorySource(path, clock), null)).Select(e => e.Text).ToArray());
    }

    [Fact]
    public async Task Memory_RefusesWhatItCannotStoreInsteadOfTruncatingOrGuessing()
    {
        var memory = Memory();

        await Assert.ThrowsAsync<KnowledgeException>(() => memory.RememberAsync(new FactInput(KnowledgeScope.User, "   "), default));
        await Assert.ThrowsAsync<KnowledgeException>(() => memory.RememberAsync(new FactInput(KnowledgeScope.User, "!!!"), default));
        await Assert.ThrowsAsync<KnowledgeException>(() => memory.RememberAsync(new FactInput(KnowledgeScope.User, new string('a', UserMemorySource.MaxTextChars + 1)), default));
        await Assert.ThrowsAsync<KnowledgeException>(() => memory.RememberAsync(new FactInput(KnowledgeScope.Book("b1"), "a fact about a book"), default));
        Assert.Empty(await Ask(memory, null));
    }

    [Fact]
    public async Task Memory_OnlyAnswersForFactsAndForTheUserScope()
    {
        var memory = Memory();
        await memory.RememberAsync(new FactInput(KnowledgeScope.User, "I like tea"), default);

        Assert.Empty(await memory.QueryAsync(new KnowledgeQuery(KnowledgeScope.User, Kinds: ["character"]), default));
        Assert.Empty(await memory.QueryAsync(new KnowledgeQuery(KnowledgeScope.Book("b1")), default));
        Assert.Single(await memory.QueryAsync(new KnowledgeQuery(KnowledgeScope.User, Kinds: ["fact"]), default));
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
