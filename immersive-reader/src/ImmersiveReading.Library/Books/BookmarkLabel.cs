using System.Globalization;

namespace ImmersiveReading.Library.Books;

/// <summary>
/// The one line a bookmark is listed by. Without selected text it names the place: <c>Vol3Ch12</c> (the volume is the book's number in its series,
/// 1 for a book without one; the chapter is counted from 1). With selected text it shows what was selected: <c>[ch12] the selected words</c>.
/// </summary>
public static class BookmarkLabel
{
    public const int MaxTextChars = 60;

    public static string Make(double? seriesIndex, int chapterIndex, string? selectedText)
    {
        int chapter = Math.Max(0, chapterIndex) + 1;
        string text = string.Join(' ', (selectedText ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return $"Vol{Volume(seriesIndex)}Ch{chapter}";
        if (text.Length > MaxTextChars) text = text[..MaxTextChars].TrimEnd() + "…";
        return $"[ch{chapter}] {text}";
    }

    private static string Volume(double? seriesIndex)
    {
        if (seriesIndex is not { } v || v <= 0) return "1";
        return v == Math.Floor(v) ? ((int)v).ToString(CultureInfo.InvariantCulture) : v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
