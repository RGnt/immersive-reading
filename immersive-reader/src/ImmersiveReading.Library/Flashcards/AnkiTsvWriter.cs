using System.Text;

namespace ImmersiveReading.Library.Flashcards;

internal static class AnkiTsvWriter
{
    public static void AppendRow(
        string ankiTsvPath,
        string expression,
        string reading,
        string meaning,
        string sentence,
        string bookTitle,
        string chTitle)
    {
        static string CleanTsvField(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();

        string row = string.Join('\t',
            CleanTsvField(expression),
            CleanTsvField(reading),
            CleanTsvField(meaning),
            CleanTsvField(sentence),
            CleanTsvField(bookTitle),
            CleanTsvField(chTitle)) + "\n";

        File.AppendAllText(ankiTsvPath, row, Encoding.UTF8);
    }
}

