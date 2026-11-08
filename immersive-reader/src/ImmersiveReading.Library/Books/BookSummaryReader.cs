using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.Folders;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

internal static class BookSummaryReader
{
    public static (string BookId, Func<IReadOnlyList<TagRecord>, IReadOnlyDictionary<string, EffectiveCustomFieldValue>, BookSummary> Builder, string? FolderId, string Title) ReadRow(
        SqliteDataReader r,
        Dictionary<string, RawFolderRow> folderLookup)
    {
        string bookId = r.GetString(0);
        string sha = r.GetString(1);
        string? folderId = r.IsDBNull(2) ? null : r.GetString(2);
        string title = r.GetString(3);
        string sortTitle = r.GetString(4);
        string seriesName = r.GetString(5);
        double? seriesIdx = r.IsDBNull(6) ? null : r.GetDouble(6);
        int customSort = r.GetInt32(7);
        string authors = r.GetString(8);
        string authorSort = r.GetString(9);
        string isbn = r.GetString(10);
        string publisher = r.GetString(11);
        string imprint = r.GetString(12);
        string pubDate = r.GetString(13);
        string lang = r.GetString(14);
        string desc = r.GetString(15);
        bool edited = r.GetInt32(16) != 0;
        string folderPath = r.GetString(17);
        string epubFile = r.GetString(18);
        long fileSize = r.GetInt64(19);
        string? cover = r.IsDBNull(20) ? null : r.GetString(20);
        int chCount = r.GetInt32(21);
        int charCount = r.GetInt32(22);
        bool statsComp = r.GetInt32(23) != 0;
        string writingMode = r.GetString(24);
        string addedAt = r.GetString(25);
        string updatedAt = r.GetString(26);
        string? lastOpened = r.IsDBNull(27) ? null : r.GetString(27);
        bool hasPack = r.GetInt32(28) != 0;
        double completion = r.GetDouble(29);

        string? breadcrumb = FolderService.BuildFolderBreadcrumb(folderId, folderLookup);

        return (
            bookId,
            (tags, cfMap) => new BookSummary(
                BookId: bookId,
                EpubSha256: sha,
                FolderId: folderId,
                FolderPathBreadcrumb: breadcrumb,
                Title: title,
                SortTitle: sortTitle,
                SeriesName: seriesName,
                SeriesIndex: seriesIdx,
                CustomSortOrder: customSort,
                Authors: authors,
                AuthorSort: authorSort,
                Isbn: isbn,
                Publisher: publisher,
                Imprint: imprint,
                PublishedDate: pubDate,
                Language: lang,
                Description: desc,
                IsMetadataEdited: edited,
                FolderPath: folderPath,
                EpubFileName: epubFile,
                FileSizeBytes: fileSize,
                CoverCachePath: cover,
                ChapterCount: chCount,
                TotalCharCount: charCount,
                StatsComputed: statsComp,
                WritingMode: writingMode,
                AddedAtUtc: addedAt,
                UpdatedAtUtc: updatedAt,
                LastOpenedAtUtc: lastOpened,
                HasStudyPack: hasPack,
                OverallCompletion: completion,
                Tags: tags,
                CustomFields: cfMap),
            folderId,
            title);
    }
}

