using System.Text;

namespace ImmersiveReading.Plugins.DeckBuilder;

// Adapted from jiten.moe's DeckDownloadService, DeckFormat, DeckOrder and DeckDownloadType
// (https://github.com/Sirush/Jiten, Apache-2.0). See NOTICE-Jiten.md.

/// <summary>Orders, selects and renders a built deck in jiten.moe's CSV / TXT formats.</summary>
public static class DeckExporter
{
    /// <summary>The CSV header of jiten.moe decks, including its historic spelling "Occurences".</summary>
    public const string CsvHeader =
        "\"Word\",\"ReadingFurigana\",\"ReadingKana\",\"Occurences\",\"ReadingFrequency\",\"PitchPositions\",\"Definitions\",\"ExampleSentence\",\"JmDictWordId\"" +
        // Extension columns (not in jiten.moe's format). Readers that only know jiten's columns ignore them.
        ",\"Kind\",\"PartOfSpeech\",\"UsageNote\"";

    public static IReadOnlyList<DeckWord> Select(IReadOnlyList<DeckWord> words, DeckExportOptions options)
    {
        IEnumerable<DeckWord> query = words;
        if (options.ExcludeKana) query = query.Where(w => !IsAllKana(w.Word));
        if (options.ExcludeFunctionWords) query = query.Where(w => !DeckKinds.IsFunctionWord(w.Kind));

        var ordered = options.Order switch
        {
            DeckOrder.DeckFrequency => query.OrderByDescending(w => w.Occurrences).ThenBy(w => w.FirstOrder),
            // Unknown ranks (0) sort last; ties fall back to how often the word occurs in this book.
            DeckOrder.GlobalFrequency => query
                .OrderBy(w => w.FrequencyRank > 0 ? 0 : 1).ThenBy(w => w.FrequencyRank)
                .ThenByDescending(w => w.Occurrences).ThenBy(w => w.FirstOrder),
            _ => query.OrderBy(w => w.FirstOrder)
        };
        var list = ordered.ToList();

        switch (options.Selection)
        {
            case DeckDownloadType.TopN:
                return list.Take(Math.Max(0, (int)options.SelectionValue)).ToList();

            case DeckDownloadType.MinOccurrences:
                return list.Where(w => w.Occurrences >= options.SelectionValue).ToList();

            case DeckDownloadType.TargetCoverage:
            {
                // Words in order until they cover the requested share of all word occurrences in the deck.
                double target = Math.Clamp(options.SelectionValue, 0, 100) / 100.0;
                long total = list.Sum(w => (long)w.Occurrences);
                long running = 0;
                var picked = new List<DeckWord>();
                foreach (var w in list)
                {
                    if (total > 0 && running >= target * total) break;
                    picked.Add(w);
                    running += w.Occurrences;
                }
                return picked;
            }

            default:
                return list;
        }
    }

    public static string Render(IReadOnlyList<DeckWord> words, DeckExportOptions options)
    {
        var selected = Select(words, options);
        var sb = new StringBuilder();

        switch (options.Format)
        {
            case DeckFormat.Txt:
                foreach (var w in selected) sb.Append(w.Word).Append('\n');
                break;

            case DeckFormat.TxtRepeated:
                foreach (var w in selected)
                    for (int i = 0; i < Math.Max(1, w.Occurrences); i++) sb.Append(w.Word).Append('\n');
                break;

            default:
                sb.Append(CsvHeader).Append('\n');
                foreach (var w in selected)
                {
                    string example = options.ExcludeExampleSentences ? "" : w.ExampleSentence;
                    sb.Append(string.Join(",",
                        Csv(w.Word),
                        Csv(w.ReadingFurigana),
                        Csv(w.ReadingKana),
                        Csv(w.Occurrences.ToString()),
                        Csv(w.FrequencyRank.ToString()),
                        Csv(w.PitchPositions),
                        Csv(w.Definitions),
                        Csv(example),
                        Csv(w.Sequence > 0 ? w.Sequence.ToString() : ""),
                        Csv(w.Kind),
                        Csv(w.PartOfSpeech),
                        Csv(w.UsageNote)));
                    sb.Append('\n');
                }
                break;
        }

        return sb.ToString();
    }

    /// <summary>Quotes every field and doubles inner quotes; line breaks become spaces so one word is one record.</summary>
    internal static string Csv(string value)
    {
        string flat = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        return "\"" + flat.Replace("\"", "\"\"") + "\"";
    }

    private static bool IsAllKana(string text) => text.Length > 0 && text.All(c => SentenceSplitter.IsKana(c) || c == 'ー');
}
