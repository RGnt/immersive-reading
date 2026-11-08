using ImmersiveReading.Core.Abstractions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment;

/// <summary>
/// Thread-safe, bounded least-recently-used cache of enriched chapter token maps.
/// Both <see cref="Get"/> and <see cref="Set"/> mark an entry as most recently used.
/// </summary>
public sealed class ChapterEnrichmentCache : IChapterTokenMapProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<(string BookId, string ChapterHref), LinkedListNode<CacheEntry>> _map = new();
    private readonly LinkedList<CacheEntry> _order = new(); // First = most recently used
    private readonly int _maxEntries;

    private sealed record CacheEntry((string BookId, string ChapterHref) Key, ChapterTokenMap Value);

    public ChapterEnrichmentCache(int maxEntries = 25)
    {
        _maxEntries = Math.Max(2, maxEntries);
    }

    public int Count
    {
        get { lock (_gate) return _map.Count; }
    }

    public void Set(ChapterTokenMap tokenMap)
    {
        var key = (tokenMap.BookId, tokenMap.ChapterHref);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
            }

            _map[key] = _order.AddFirst(new CacheEntry(key, tokenMap));

            while (_map.Count > _maxEntries && _order.Last is { } oldest)
            {
                _map.Remove(oldest.Value.Key);
                _order.RemoveLast();
            }
        }
    }

    public ChapterTokenMap? Get(string bookId, string chapterHref)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue((bookId, chapterHref), out var node))
            {
                return null;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Value;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
        }
    }
}
