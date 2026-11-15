using ImmersiveReading.Core.Knowledge;

namespace ImmersiveReading.App.Rpc.Modules;

/// <summary>
/// What the characters know about the user (Settings → Memory): list, add, forget one, forget everything. The facts live in the
/// knowledge hub's user memory (spec 24); every one of them can be seen and really deleted here.
/// </summary>
public sealed class MemoryRpcModule(IKnowledgeHub hub) : IRpcModule
{
    public IEnumerable<(string Method, RpcHandler Handler)> Handlers
    {
        get
        {
            yield return ("memory.list", async (req, ct) =>
            {
                bool available = hub.Sources.Any(s => s.Writable && s.ScopeKinds.Contains(KnowledgeScope.UserKind));
                var facts = available ? await List(req.OptionalString("search"), ct) : [];
                return new { available, facts = facts.Select(Dto) };
            });
            yield return ("memory.add", async (req, ct) =>
            {
                try
                {
                    var entry = await hub.RememberAsync(new FactInput(KnowledgeScope.User, req.RequiredString("text"), req.OptionalString("subject"), "settings"), ct);
                    return new { ok = true, error = (string?)null, fact = (object?)Dto(entry) };
                }
                catch (KnowledgeException ex)
                {
                    return new { ok = false, error = (string?)ex.Message, fact = (object?)null };
                }
            });
            yield return ("memory.forget", async (req, ct) => new { ok = await hub.ForgetAsync(UserMemorySourceId, req.RequiredString("id"), ct) });
            yield return ("memory.forgetAll", async (req, ct) =>
            {
                int removed = 0;
                for (var batch = await List(null, ct); batch.Count > 0; batch = await List(null, ct))
                {
                    int before = removed;
                    foreach (var fact in batch)
                        if (await hub.ForgetAsync(fact.SourceId, fact.Id, ct)) removed++;
                    if (removed == before) break;                  // nothing could be removed: do not loop forever
                }
                return new { ok = true, removed };
            });
        }
    }

    private const string UserMemorySourceId = ImmersiveReading.Knowledge.UserMemorySource.Id;

    private async Task<IReadOnlyList<KnowledgeEntry>> List(string? search, CancellationToken ct)
        => await hub.QueryAsync(new KnowledgeQuery(KnowledgeScope.User, search, ["fact"], Limit: 200), ct);

    private static object Dto(KnowledgeEntry e) => new
    {
        id = e.Id,
        sourceId = e.SourceId,
        text = e.Text,
        subject = e.Title,
        origin = e.Attributes?.GetValueOrDefault("origin") ?? "",
        createdUtc = e.Attributes?.GetValueOrDefault("createdUtc") ?? "",
        sharedWith = (e.Attributes?.GetValueOrDefault("sharedWith") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    };
}
