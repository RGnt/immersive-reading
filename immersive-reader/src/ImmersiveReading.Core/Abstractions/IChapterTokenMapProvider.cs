using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Core.Abstractions;

public interface IChapterTokenMapProvider
{
    ChapterTokenMap? Get(string bookId, string chapterHref);
}

