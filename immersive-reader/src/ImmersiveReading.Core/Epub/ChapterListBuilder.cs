using System.IO.Compression;
using AngleSharp.Dom;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Epub;

internal static class ChapterListBuilder
{
    public static (List<BookChapterRecord> Chapters, int TotalChars) BuildChapterList(
        ZipArchive archive,
        IDocument opfDoc,
        Dictionary<string, (string Href, string MediaType, string Properties)> manifest,
        List<string> spineHrefs,
        string bookId,
        bool computeDeepChapterStats)
    {
        var navPoints = EpubTocParser.ParseTableOfContents(archive, opfDoc, manifest);

        // Map spineHref -> list of ToC entries
        var navBySpine = new Dictionary<string, List<EpubNavPoint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var np in navPoints)
        {
            if (!navBySpine.TryGetValue(np.SpineHref, out var list))
            {
                list = [];
                navBySpine[np.SpineHref] = list;
            }
            list.Add(np);
        }

        // Find the first story ToC entry
        EpubNavPoint? firstStoryNav = navPoints.FirstOrDefault(np => !FrontMatterClassifier.IsFrontMatterTitle(np.Title));
        int firstStorySpineIdx = -1;
        if (firstStoryNav is not null)
        {
            firstStorySpineIdx = spineHrefs.FindIndex(s =>
                string.Equals(s, firstStoryNav.SpineHref, StringComparison.OrdinalIgnoreCase));
        }

        // Fallback if no ToC or firstStoryNav not in spine: scan early spine items for text
        if (firstStorySpineIdx < 0)
        {
            firstStorySpineIdx = FrontMatterClassifier.FindFirstStorySpineIndex(archive, spineHrefs);
        }

        var chapters = new List<BookChapterRecord>();
        int cumulativeChars = 0;
        int storyChapterNumber = 0;
        int frontMatterIndex = 0;
        int backMatterIndex = 0;
        string lastStoryTitle = "";
        int continuationPart = 1;
        bool hasEnteredBackMatter = false;

        for (int i = 0; i < spineHrefs.Count; i++)
        {
            string spineHref = spineHrefs[i];
            bool hasNav = navBySpine.TryGetValue(spineHref, out var nps) && nps.Count > 0;
            string? navTitle = hasNav ? nps![0].Title : null;
            string fragId = hasNav ? nps![0].FragmentId : "";

            int charCount = 0;
            if (computeDeepChapterStats)
            {
                var chEntry = EpubPaths.GetEntryNormalized(archive, spineHref);
                if (chEntry is not null)
                {
                    charCount = FrontMatterClassifier.CountNormalizedChapterCharacters(chEntry);
                }
            }

            // Case A: Before the first story chapter -> FRONT MATTER / INTRODUCTION
            if (i < firstStorySpineIdx)
            {
                frontMatterIndex++;
                string title = !string.IsNullOrWhiteSpace(navTitle)
                    ? navTitle
                    : FrontMatterClassifier.IdentifyFrontMatterTitle(archive, spineHref, i);

                chapters.Add(new BookChapterRecord(
                    BookId: bookId,
                    ChapterIndex: chapters.Count,
                    ChapterId: $"front-{frontMatterIndex:D2}",
                    Title: title,
                    SpineHref: spineHref,
                    FragmentId: fragId,
                    CharCount: charCount,
                    CumulativeChars: cumulativeChars,
                    IsExcluded: true
                ));
                continue;
            }

            // Case B: In or after the story chapters
            bool isBackMatter = hasEnteredBackMatter || (hasNav && FrontMatterClassifier.IsBackMatterTitle(navTitle!));

            if (isBackMatter)
            {
                hasEnteredBackMatter = true;
                backMatterIndex++;
                string title = !string.IsNullOrWhiteSpace(navTitle) ? navTitle : $"Back Matter {backMatterIndex}";

                chapters.Add(new BookChapterRecord(
                    BookId: bookId,
                    ChapterIndex: chapters.Count,
                    ChapterId: $"back-{backMatterIndex:D2}",
                    Title: title,
                    SpineHref: spineHref,
                    FragmentId: fragId,
                    CharCount: charCount,
                    CumulativeChars: cumulativeChars,
                    IsExcluded: true
                ));
            }
            else
            {
                // Story Chapter
                if (hasNav)
                {
                    storyChapterNumber++;
                    lastStoryTitle = navTitle!;
                    continuationPart = 1;

                    chapters.Add(new BookChapterRecord(
                        BookId: bookId,
                        ChapterIndex: chapters.Count,
                        ChapterId: $"chapter-{storyChapterNumber:D2}",
                        Title: navTitle!,
                        SpineHref: spineHref,
                        FragmentId: fragId,
                        CharCount: charCount,
                        CumulativeChars: cumulativeChars,
                        IsExcluded: false
                    ));
                    cumulativeChars += charCount;
                }
                else
                {
                    // Continuation or in-chapter illustration between ToC entries
                    continuationPart++;
                    bool isIllustration = FrontMatterClassifier.IsIllustrationSpineItem(archive, spineHref);
                    string title = !string.IsNullOrWhiteSpace(lastStoryTitle)
                        ? (isIllustration ? $"{lastStoryTitle} (Illustration)" : $"{lastStoryTitle} (Part {continuationPart})")
                        : (isIllustration ? $"Illustration {continuationPart}" : $"Chapter {Math.Max(1, storyChapterNumber)} (Part {continuationPart})");

                    string chId = storyChapterNumber > 0
                        ? $"chapter-{storyChapterNumber:D2}-p{continuationPart}"
                        : $"chapter-01-p{continuationPart}";

                    chapters.Add(new BookChapterRecord(
                        BookId: bookId,
                        ChapterIndex: chapters.Count,
                        ChapterId: chId,
                        Title: title,
                        SpineHref: spineHref,
                        FragmentId: "",
                        CharCount: charCount,
                        CumulativeChars: cumulativeChars,
                        IsExcluded: false
                    ));
                    cumulativeChars += charCount;
                }
            }
        }

        return (chapters, cumulativeChars);
    }
}

