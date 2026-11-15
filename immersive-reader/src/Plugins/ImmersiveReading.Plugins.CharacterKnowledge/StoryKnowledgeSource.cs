using System.Text.Json;
using ImmersiveReading.Core.Knowledge;

namespace ImmersiveReading.Plugins.CharacterKnowledge;

/// <summary>
/// The spoiler-gated story graph of a book or series as a <see cref="IKnowledgeSource"/> (spec 24): characters, events and items
/// as the reader knows them at a chapter. It adds no gating of its own, it only looks up the precomputed snapshots and cards, and it
/// refuses to answer without a viewpoint.
/// </summary>
public sealed class StoryKnowledgeSource(Func<string, SeriesInfo> resolve, Func<string, KnowledgeDb> db) : IKnowledgeSource
{
    public const string Id = "story";
    private static readonly string[] AllKinds = ["character", "event", "item"];

    public string SourceId => Id;
    public string DisplayName => "The story so far";
    public IReadOnlyList<string> ScopeKinds { get; } = [KnowledgeScope.BookKind];
    public KnowledgeGating Gating => KnowledgeGating.StoryPosition;

    private sealed record Candidate(string Kind, string Id, string Title, string Text, string Search, string? Detail = null);

    public Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (query.Scope.Kind != KnowledgeScope.BookKind) return Task.FromResult<IReadOnlyList<KnowledgeEntry>>([]);
        if (query.Viewpoint?.ChapterIndex is not int chapter)
            throw new KnowledgeException("The story graph only shows what the reader has read: a viewpoint (chapter) is required.");

        SeriesInfo series;
        try { series = resolve(query.Scope.Id); }
        catch (KeyNotFoundException) { return Task.FromResult<IReadOnlyList<KnowledgeEntry>>([]); }
        int volume = series.VolumeOf(query.Scope.Id);
        var graph = db(series.SeriesKey);
        var kinds = (query.Kinds is { Count: > 0 } ? query.Kinds : AllKinds).ToHashSet(StringComparer.Ordinal);
        int limit = Math.Clamp(query.Limit, 1, 200);

        var candidates = new List<Candidate>();
        if (kinds.Contains("character"))
            candidates.AddRange(graph.ListCharacters(volume, chapter).Select(c => new Candidate("character", c.CharacterId, c.Name, c.Name, c.Name)));
        if (kinds.Contains("event"))
            candidates.AddRange(graph.ListEvents(volume, chapter, limit: 2000).Events.Select(EventCandidate));
        if (kinds.Contains("item"))
            candidates.AddRange(graph.ListItems(volume, chapter).Select(ItemCandidate));

        string text = KnowledgeMatch.Clean(query.Text);
        List<(Candidate C, double Score)> chosen;
        if (text.Length == 0)
        {
            chosen = candidates.Take(limit).Select(c => (c, 0.0)).ToList();      // listing order: characters by prominence, events newest first, items as stored
        }
        else
        {
            var scores = KnowledgeMatch.Score(text, candidates.Select(c => c.Search).ToList());
            chosen = candidates.Select((c, i) => (C: c, Score: scores[i]))
                .Where(x => x.Score >= KnowledgeMatch.MinScore)
                .OrderByDescending(x => x.Score)
                .Take(limit)
                .ToList();
        }

        var entries = chosen.Select(x =>
        {
            string body = x.C.Text;
            if (x.C.Kind == "character" && graph.GetProfile(volume, chapter, x.C.Id) is { } profile && profile.Block.Length > 0) body = profile.Block;
            var attributes = new Dictionary<string, string> { ["volume"] = volume.ToString(), ["chapter"] = chapter.ToString() };
            if (x.C.Detail is not null) attributes["detail"] = x.C.Detail;
            return new KnowledgeEntry(Id, query.Scope, x.C.Id, x.C.Kind, x.C.Title, body, Math.Round(x.Score, 4), attributes);
        }).ToList();
        return Task.FromResult<IReadOnlyList<KnowledgeEntry>>(entries);
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static IEnumerable<string> Names(JsonElement e, string array)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(array, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(p => Str(p, "name")).Where(n => n.Length > 0)
            : [];

    private static Candidate EventCandidate(JsonElement card)
    {
        string title = Str(card, "title"), summary = Str(card, "summary"), kind = Str(card, "kind");
        var who = Names(card, "participants").ToList();
        string text = summary.Length > 0 ? $"{title}: {summary}" : title;
        if (who.Count > 0) text += $" ({string.Join(", ", who)})";
        return new Candidate("event", Str(card, "id"), title, text, $"{title} {summary} {string.Join(' ', who)}", kind);
    }

    private static Candidate ItemCandidate(JsonElement card)
    {
        string name = Str(card, "name");
        var description = new List<string>();
        if (card.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Object)
            foreach (var prop in d.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    description.AddRange(prop.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
        string holder = card.TryGetProperty("holder", out var h) ? Str(h, "name") : "";
        string state = Str(card, "state"), slot = Str(card, "slot");
        string held = holder.Length == 0 ? "" : $" Held by {holder}" + (state.Length > 0 ? $" ({state}{(slot.Length > 0 ? ", " + slot : "")})" : "") + ".";
        string text = $"{name}: {string.Join("; ", description)}.{held}".Replace(": .", ":");
        return new Candidate("item", Str(card, "id"), name, text, $"{name} {string.Join(' ', description)} {holder}");
    }
}
