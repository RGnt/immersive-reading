using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment;
using Xunit;

namespace ImmersiveReading.Enrichment.Tests;

public sealed class ChapterEnrichmentCacheTests
{
    [Fact]
    public void EvictsOldestItemWhenCapacityExceeded()
    {
        var cache = new ChapterEnrichmentCache(maxEntries: 2);

        var map1 = new ChapterTokenMap("book1", "ch1.xhtml", [], []);
        var map2 = new ChapterTokenMap("book1", "ch2.xhtml", [], []);
        var map3 = new ChapterTokenMap("book1", "ch3.xhtml", [], []);

        cache.Set(map1);
        cache.Set(map2);
        Assert.Equal(2, cache.Count);

        // Access map1 to make map2 the LRU
        var retrieved1 = cache.Get("book1", "ch1.xhtml");
        Assert.NotNull(retrieved1);

        // Add map3 -> map2 should be evicted
        cache.Set(map3);
        Assert.Equal(2, cache.Count);

        Assert.NotNull(cache.Get("book1", "ch1.xhtml"));
        Assert.Null(cache.Get("book1", "ch2.xhtml"));
        Assert.NotNull(cache.Get("book1", "ch3.xhtml"));
    }

    [Fact]
    public void SettingExistingKeyUpdatesWithoutIncreasingCount()
    {
        var cache = new ChapterEnrichmentCache(maxEntries: 5);

        var map1 = new ChapterTokenMap("book1", "ch1.xhtml", [], ["first"]);
        var map1Updated = new ChapterTokenMap("book1", "ch1.xhtml", [], ["updated"]);

        cache.Set(map1);
        Assert.Equal(1, cache.Count);

        cache.Set(map1Updated);
        Assert.Equal(1, cache.Count);

        var retrieved = cache.Get("book1", "ch1.xhtml");
        Assert.NotNull(retrieved);
        Assert.Equal(["updated"], retrieved.ParagraphTexts);
    }
}

