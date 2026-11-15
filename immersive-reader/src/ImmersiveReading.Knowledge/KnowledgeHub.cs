using System.Diagnostics;
using ImmersiveReading.Core.Knowledge;

namespace ImmersiveReading.Knowledge;

/// <summary>
/// Routes questions to the registered sources. Owns no data and no gating rules of its own beyond one: a source that hides
/// what the reader has not read is never asked without a viewpoint, so no caller can get "everything" by forgetting to say where
/// the reader is.
/// </summary>
public sealed class KnowledgeHub : IKnowledgeHub
{
    private readonly List<IKnowledgeSource> _sources = [];
    private readonly Lock _lock = new();

    public IReadOnlyList<KnowledgeSourceInfo> Sources
    {
        get
        {
            lock (_lock)
            {
                return _sources.Select(s => new KnowledgeSourceInfo(s.SourceId, s.DisplayName, s.ScopeKinds, s.Gating, s is IWritableKnowledgeSource)).ToList();
            }
        }
    }

    public IDisposable RegisterSource(IKnowledgeSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.SourceId)) throw new ArgumentException("A knowledge source needs an id.", nameof(source));
        lock (_lock)
        {
            if (_sources.Any(s => s.SourceId == source.SourceId))
                throw new InvalidOperationException($"A knowledge source '{source.SourceId}' is already registered.");
            _sources.Add(source);
        }
        return new Registration(() => { lock (_lock) { _sources.Remove(source); } });
    }

    private List<IKnowledgeSource> For(string scopeKind)
    {
        lock (_lock)
        {
            return _sources.Where(s => s.ScopeKinds.Contains(scopeKind, StringComparer.Ordinal)).ToList();
        }
    }

    public async Task<IReadOnlyList<KnowledgeEntry>> QueryAsync(KnowledgeQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Scope.Kind)) throw new KnowledgeException("A knowledge query needs a scope.");
        int limit = Math.Clamp(query.Limit, 1, 200);
        var sources = For(query.Scope.Kind);

        foreach (var source in sources)
        {
            if (source.Gating == KnowledgeGating.StoryPosition && query.Viewpoint is not { IsRestricted: true })
                throw new KnowledgeException($"'{source.DisplayName}' only shows what the reader has read: say which chapter the reader is at (viewpoint).");
        }

        var entries = new List<KnowledgeEntry>();
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                entries.AddRange(await source.QueryAsync(query with { Limit = limit }, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not KnowledgeException)
            {
                // Knowledge only enriches the asker's context: one broken source must not take the conversation down, but it must not be silent.
                Trace.TraceWarning($"Knowledge source '{source.SourceId}' failed: {ex.Message}");
            }
        }
        return entries.OrderByDescending(e => e.Relevance).Take(limit).ToList();
    }

    public Task<KnowledgeEntry> RememberAsync(FactInput fact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var target = For(fact.Scope.Kind).OfType<IWritableKnowledgeSource>().FirstOrDefault()
            ?? throw new KnowledgeException($"Nothing can store facts about '{fact.Scope.Kind}'.");
        return target.RememberAsync(fact, ct);
    }

    public async Task<bool> ForgetAsync(string sourceId, string factId, CancellationToken ct = default)
    {
        IWritableKnowledgeSource? target;
        lock (_lock)
        {
            target = _sources.OfType<IWritableKnowledgeSource>().FirstOrDefault(s => s.SourceId == sourceId);
        }
        return target is not null && await target.ForgetAsync(factId, ct).ConfigureAwait(false);
    }

    private sealed class Registration(Action onDispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) onDispose();
        }
    }
}
