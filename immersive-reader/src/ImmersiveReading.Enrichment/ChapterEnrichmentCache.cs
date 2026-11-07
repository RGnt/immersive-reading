using System.Collections.Concurrent;

namespace ImmersiveReading.Enrichment;

public sealed class ChapterEnrichmentCache
{
    private readonly ConcurrentDictionary<(string BookId, string ChapterHref), ChapterTokenMap> _cache = new();
    private readonly ConcurrentQueue<(string BookId, string ChapterHref)> _lruKeys = new();
    private readonly int _maxEntries;

    public ChapterEnrichmentCache(int maxEntries = 25)
    {
        _maxEntries = Math.Max(5, maxEntries);
    }

    public void Set(ChapterTokenMap tokenMap)
    {
        var key = (tokenMap.BookId, tokenMap.ChapterHref);
        _cache[key] = tokenMap;
        _lruKeys.Enqueue(key);

        while (_cache.Count > _maxEntries && _lruKeys.TryDequeue(out var oldKey))
        {
            _cache.TryRemove(oldKey, out _);
        }
    }

    public ChapterTokenMap? Get(string bookId, string chapterHref)
    {
        return _cache.GetValueOrDefault((bookId, chapterHref));
    }

    public void Clear()
    {
        _cache.Clear();
    }
}

