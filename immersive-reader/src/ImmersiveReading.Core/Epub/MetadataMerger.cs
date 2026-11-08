using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Epub;

public sealed record EffectiveMetadata(
    string Title,
    string SortTitle,
    string Authors,
    string AuthorSort,
    string Imprint,
    string SeriesName,
    double? SeriesIndex,
    string Isbn
);

internal static class MetadataMerger
{
    public static EffectiveMetadata Merge(
        OpfOriginalMetadata rawOpf,
        FilenameParsedHints fileHints,
        FilenameParsedHints titleHints,
        string fileName)
    {
        string rawTitleClean = !string.IsNullOrWhiteSpace(rawOpf.Title)
            ? rawOpf.Title.Trim()
            : fileHints.CleanTitle;

        string effectiveTitle = rawTitleClean;
        string effectiveAuthors = rawOpf.Authors;
        string effectiveImprint = rawOpf.Imprint;
        string effectiveSeriesName = rawOpf.SeriesName;
        double? effectiveSeriesIndex = rawOpf.SeriesIndex;
        string effectiveIsbn = rawOpf.Isbn;

        // If OPF lacks author or title starts with [Author], enrich from hints
        if (string.IsNullOrWhiteSpace(effectiveAuthors))
        {
            effectiveAuthors = !string.IsNullOrWhiteSpace(titleHints.Authors)
                ? titleHints.Authors
                : fileHints.Authors;
            if (!string.IsNullOrWhiteSpace(titleHints.Authors) && !string.IsNullOrWhiteSpace(titleHints.CleanTitle))
            {
                effectiveTitle = titleHints.CleanTitle;
            }
        }

        if (string.IsNullOrWhiteSpace(effectiveImprint))
        {
            effectiveImprint = !string.IsNullOrWhiteSpace(titleHints.Imprint)
                ? titleHints.Imprint
                : fileHints.Imprint;
        }

        if (string.IsNullOrWhiteSpace(effectiveSeriesName))
        {
            effectiveSeriesName = !string.IsNullOrWhiteSpace(titleHints.SeriesName)
                ? titleHints.SeriesName
                : fileHints.SeriesName;
        }

        effectiveSeriesIndex ??= titleHints.SeriesIndex ?? fileHints.SeriesIndex;

        if (string.IsNullOrWhiteSpace(effectiveIsbn))
        {
            effectiveIsbn = !string.IsNullOrWhiteSpace(titleHints.Isbn)
                ? titleHints.Isbn
                : fileHints.Isbn;
        }

        if (string.IsNullOrWhiteSpace(effectiveTitle))
        {
            effectiveTitle = Path.GetFileNameWithoutExtension(fileName);
        }

        string effectiveSortTitle = !string.IsNullOrWhiteSpace(rawOpf.SortTitle)
            ? rawOpf.SortTitle
            : effectiveTitle;

        string effectiveAuthorSort = !string.IsNullOrWhiteSpace(rawOpf.AuthorSort)
            ? rawOpf.AuthorSort
            : effectiveAuthors;

        return new EffectiveMetadata(
            Title: effectiveTitle,
            SortTitle: effectiveSortTitle,
            Authors: effectiveAuthors,
            AuthorSort: effectiveAuthorSort,
            Imprint: effectiveImprint,
            SeriesName: effectiveSeriesName,
            SeriesIndex: effectiveSeriesIndex,
            Isbn: effectiveIsbn
        );
    }
}

