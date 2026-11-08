using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Abstractions;

public interface IBookCatalog
{
    BookDetail? GetBookDetail(string bookId);
    BookSummary? GetBookSummary(string bookId);
    string? GetEpubFilePath(string bookId);
}

