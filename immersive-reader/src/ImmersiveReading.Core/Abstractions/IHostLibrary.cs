using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Abstractions;

/// <summary>
/// Controlled read/write view of the library that plugins receive through
/// <see cref="ImmersiveReading.Core.Plugins.IPluginHostContext.Library"/>. It exposes only the
/// metadata operations plugins legitimately need (listing books, tags, custom fields), never the
/// database itself.
/// </summary>
public interface IHostLibrary
{
    PagedBookListResult ListBooks(BookListQuery query);
    BookDetail? GetBookDetail(string bookId);
    BookDetail UpdateBookMetadata(BookMetadataUpdateRequest request);
    IReadOnlyList<TagRecord> ListTags();
    TagRecord SaveTag(string? tagId, string name, string colorHex = "#64748b", int sortOrder = 0);
    /// <summary>The chapter the reader is at in this book (library chapter index), or null if the book was never opened.</summary>
    int? GetReadingChapterIndex(string bookId);
    void SetCustomFieldValue(string entityType, string entityId, string fieldKey, string valueText, double? valueNumber = null);
}
