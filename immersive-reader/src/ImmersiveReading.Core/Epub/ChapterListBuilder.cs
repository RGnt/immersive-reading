using System.IO.Compression;
using AngleSharp.Dom;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Epub;

/// <summary>
/// Builds the chapter list for a book (spec 12): one chapter per ToC entry. Spine files that have no ToC
/// entry of their own (split text parts, illustration pages) become <b>members</b> of the chapter they
/// follow instead of becoming extra "(Part N)" / "(Illustration)" chapters.
/// </summary>
internal static class ChapterListBuilder
{
    private enum Region { Front, Story, Back }

    /// <summary>A chapter under construction; later spine files may be appended to it.</summary>
    private sealed class Draft(Region region, string title, string fragmentId, bool fromToc, bool isExcluded)
    {
        public Region Region { get; } = region;
        public string Title { get; } = title;
        public string FragmentId { get; } = fragmentId;
        public bool FromToc { get; } = fromToc;
        public bool IsExcluded { get; set; } = isExcluded;
        public List<string> Members { get; } = [];
        public int CharCount { get; set; }
        public int StoryNumber { get; set; }
        public int RegionNumber { get; set; }
    }

    public static (List<BookChapterRecord> Chapters, int TotalChars) BuildChapterList(
        ZipArchive archive,
        IDocument opfDoc,
        Dictionary<string, (string Href, string MediaType, string Properties)> manifest,
        List<string> spineHrefs,
        string bookId,
        bool computeDeepChapterStats)
    {
        var navPoints = EpubTocParser.ParseTableOfContents(archive, opfDoc, manifest);
        // "Usable ToC" means at least one story-titled entry. A ToC listing only a cover and a colophon says
        // nothing about the story chapters, so such a book is treated as having no ToC (spec 12 FR-5).
        bool hasStoryToc = navPoints.Any(np =>
            !FrontMatterClassifier.IsFrontMatterTitle(np.Title) &&
            !FrontMatterClassifier.IsBackMatterTitle(np.Title));

        // Map spineHref -> list of ToC entries (the first entry per file names the chapter)
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

        int firstStorySpineIdx = FindFirstStorySpineIndex(archive, spineHrefs, navPoints);

        var drafts = new List<Draft>();
        int storyChapterNumber = 0;
        int frontMatterIndex = 0;
        int backMatterIndex = 0;
        bool hasEnteredBackMatter = false;

        for (int i = 0; i < spineHrefs.Count; i++)
        {
            string spineHref = spineHrefs[i];
            bool hasNav = navBySpine.TryGetValue(spineHref, out var nps) && nps.Count > 0;
            string? navTitle = hasNav ? nps![0].Title : null;
            string fragId = hasNav ? nps![0].FragmentId : "";
            int charCount = ComputeCharCount(archive, spineHref, computeDeepChapterStats);
            var last = drafts.Count > 0 ? drafts[^1] : null;

            // FRONT MATTER: everything before the first story chapter
            if (i < firstStorySpineIdx)
            {
                string title = !string.IsNullOrWhiteSpace(navTitle)
                    ? navTitle
                    : FrontMatterClassifier.IdentifyFrontMatterTitle(archive, spineHref, i);

                // A file without its own ToC entry joins the preceding front record when it is
                //  - a text page under a ToC-titled section (e.g. a multi-file "Introduction"), or
                //  - one more page of an unnamed run of generated illustration pages ("口絵 (Illustrations)").
                if (!hasNav && last is { Region: Region.Front })
                {
                    bool isIllustration = FrontMatterClassifier.IsIllustrationSpineItem(archive, spineHref);
                    bool joinsTocSection = last.FromToc && !isIllustration;
                    bool joinsIllustrationRun = !last.FromToc && last.Title == title && title.Contains("Illustrations", StringComparison.Ordinal);
                    if (joinsTocSection || joinsIllustrationRun)
                    {
                        Append(last, spineHref, charCount);
                        continue;
                    }
                }

                frontMatterIndex++;
                var front = new Draft(Region.Front, title, fragId, hasNav, isExcluded: true) { RegionNumber = frontMatterIndex };
                Append(front, spineHref, charCount);
                drafts.Add(front);
                continue;
            }

            bool isBackMatter = hasEnteredBackMatter || (hasNav && FrontMatterClassifier.IsBackMatterTitle(navTitle!));
            if (isBackMatter)
            {
                hasEnteredBackMatter = true;
                if (!hasNav && last is { Region: Region.Back })
                {
                    Append(last, spineHref, charCount);
                    continue;
                }

                backMatterIndex++;
                string title = !string.IsNullOrWhiteSpace(navTitle) ? navTitle : $"Back Matter {backMatterIndex}";
                var back = new Draft(Region.Back, title, fragId, hasNav, isExcluded: true) { RegionNumber = backMatterIndex };
                Append(back, spineHref, charCount);
                drafts.Add(back);
                continue;
            }

            // STORY
            if (hasNav)
            {
                storyChapterNumber++;
                var chapter = new Draft(Region.Story, navTitle!, fragId, fromToc: true, isExcluded: false) { StoryNumber = storyChapterNumber };
                Append(chapter, spineHref, charCount);
                drafts.Add(chapter);
                continue;
            }

            // No ToC entry of its own: a split text part or an illustration page.
            var openStory = last is { Region: Region.Story } ? last : null;
            bool joinsOpenChapter = openStory is not null &&
                (hasStoryToc || FrontMatterClassifier.IsIllustrationSpineItem(archive, spineHref));
            if (joinsOpenChapter)
            {
                Append(openStory!, spineHref, charCount);
                continue;
            }

            // No usable story ToC (or nothing open yet): each text file is its own chapter (FR-5).
            storyChapterNumber++;
            var synthetic = new Draft(Region.Story, $"Chapter {storyChapterNumber}", "", fromToc: false, isExcluded: false) { StoryNumber = storyChapterNumber };
            Append(synthetic, spineHref, charCount);
            drafts.Add(synthetic);
        }

        return Finalize(drafts, bookId);
    }

    private static void Append(Draft draft, string spineHref, int charCount)
    {
        draft.Members.Add(spineHref);
        draft.CharCount += charCount;
    }

    private static int ComputeCharCount(ZipArchive archive, string spineHref, bool computeDeepChapterStats)
    {
        if (!computeDeepChapterStats) return 0;
        var entry = EpubPaths.GetEntryNormalized(archive, spineHref);
        return entry is null ? 0 : FrontMatterClassifier.CountNormalizedChapterCharacters(entry);
    }

    private static int FindFirstStorySpineIndex(ZipArchive archive, List<string> spineHrefs, List<EpubNavPoint> navPoints)
    {
        // First story ToC entry (must not be front matter or back matter)
        var firstStoryNav = navPoints.FirstOrDefault(np =>
            !FrontMatterClassifier.IsFrontMatterTitle(np.Title) &&
            !FrontMatterClassifier.IsBackMatterTitle(np.Title));

        int idx = -1;
        if (firstStoryNav is not null)
        {
            idx = spineHrefs.FindIndex(s => string.Equals(s, firstStoryNav.SpineHref, StringComparison.OrdinalIgnoreCase));
        }

        // Fallback if no ToC or the entry is not in the spine: scan early spine items for text
        return idx >= 0 ? idx : FrontMatterClassifier.FindFirstStorySpineIndex(archive, spineHrefs);
    }

    private static (List<BookChapterRecord> Chapters, int TotalChars) Finalize(List<Draft> drafts, string bookId)
    {
        var chapters = new List<BookChapterRecord>(drafts.Count);
        int cumulativeChars = 0;

        foreach (var d in drafts)
        {
            string chapterId = d.Region switch
            {
                Region.Front => $"front-{d.RegionNumber:D2}",
                Region.Back => $"back-{d.RegionNumber:D2}",
                _ => $"chapter-{d.StoryNumber:D2}"
            };

            chapters.Add(new BookChapterRecord(
                BookId: bookId,
                ChapterIndex: chapters.Count,
                ChapterId: chapterId,
                Title: d.Title,
                SpineHref: d.Members[0],
                FragmentId: d.FragmentId,
                EndSpineHref: d.Members.Count > 1 ? d.Members[^1] : null,
                CharCount: d.CharCount,
                CumulativeChars: cumulativeChars,
                IsExcluded: d.IsExcluded,
                MemberSpineHrefs: d.Members.ToArray()));

            if (!d.IsExcluded)
            {
                cumulativeChars += d.CharCount;
            }
        }

        // Safety guard: if ALL chapters ended up excluded, ensure non-front/non-back chapters
        // (or at least chapter 0) are readable so books are never locked to cover pages
        if (chapters.Count > 0 && chapters.All(c => c.IsExcluded))
        {
            var candidate = chapters.FirstOrDefault(c =>
                !FrontMatterClassifier.IsFrontOrBackMatter(c.Title, c.SpineHref))
                ?? chapters[0];

            for (int k = 0; k < chapters.Count; k++)
            {
                if (chapters[k].ChapterIndex == candidate.ChapterIndex ||
                    !FrontMatterClassifier.IsFrontOrBackMatter(chapters[k].Title, chapters[k].SpineHref))
                {
                    chapters[k] = chapters[k] with { IsExcluded = false };
                }
            }
        }

        return (chapters, cumulativeChars);
    }
}
