using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImmersiveReading.Library.Flashcards;
using ImmersiveReading.Library.Internal;

namespace ImmersiveReading.Library;

public sealed record FlashcardRubySpan(
    int Start,
    int End,
    string Surface,
    string Reading,
    string Source = "reader"
);

public sealed record MineFlashcardRequest(
    string BookId,
    string BookTitle,
    string ChapterHref,
    int ChapterNumber,
    string ChapterTitle,
    string Expression,
    string Reading,
    string Meaning,
    string SentenceText,
    IReadOnlyList<FlashcardRubySpan>? SentenceRuby = null,
    string? Notes = null
);

public sealed record FlashcardSummaryItem(
    string Id,
    string Kind,
    string Prompt,
    string Reading,
    string Meaning,
    string Example,
    string ChapterId,
    int ChapterNumber,
    string ChapterTitle,
    int SetNumber
);

public sealed record MineFlashcardResult(
    string CardId,
    string LibraryJsonPath,
    string AnkiTsvPath,
    int TotalBookCards,
    FlashcardSummaryItem Card
);

/// <summary>
/// Exports 1-click sentence flashcards mined from the Reader Dictionary Popover into
/// output/data/flashcards/library.json and output/data/flashcards/mined-anki.tsv.
/// Preserves Hard Invariant #2: Never translates story sentences; only word dictionary glosses are used.
/// </summary>
public sealed class FlashcardMiningService
{
    private readonly string _outputRoot;
    private readonly object _syncLock = new();

    private static readonly JsonSerializerOptions WriteJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string LibraryJsonPath => Path.Combine(_outputRoot, "data", "flashcards", "library.json");
    public string AnkiTsvPath => Path.Combine(_outputRoot, "data", "flashcards", "mined-anki.tsv");

    public FlashcardMiningService(string outputRoot)
    {
        _outputRoot = Path.GetFullPath(outputRoot);
    }

    public MineFlashcardResult MineCard(MineFlashcardRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Expression);

        string reading = string.IsNullOrWhiteSpace(request.Reading) ? request.Expression : request.Reading.Trim();
        string meaning = string.IsNullOrWhiteSpace(request.Meaning)
            ? "No English gloss in the supplied dictionary."
            : request.Meaning.Trim();
        string sentence = (request.SentenceText ?? "").Trim();
        int chNum = Math.Max(1, request.ChapterNumber);
        string chId = $"chapter-{chNum:D2}";
        string chTitle = string.IsNullOrWhiteSpace(request.ChapterTitle) ? $"Chapter {chNum}" : request.ChapterTitle.Trim();

        string cardId = ComputeCardId(request.Expression, reading);

        lock (_syncLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LibraryJsonPath)!);

            JsonObject rootObj = FlashcardLibraryStore.LoadOrCreateLibraryRoot(LibraryJsonPath);
            var booksArray = rootObj["books"] as JsonArray ?? new JsonArray();
            rootObj["books"] = booksArray;

            JsonObject? bookObj = booksArray
                .OfType<JsonObject>()
                .FirstOrDefault(b => string.Equals((string?)b["id"], request.BookId, StringComparison.Ordinal));

            if (bookObj is null)
            {
                bookObj = new JsonObject
                {
                    ["id"] = request.BookId,
                    ["title"] = string.IsNullOrWhiteSpace(request.BookTitle) ? request.BookId : request.BookTitle,
                    ["chapters"] = new JsonArray(),
                    ["dictionary_sources"] = new JsonArray(),
                    ["attribution"] = new JsonArray(
                        "Japanese examples and book text: user-supplied EPUB.",
                        "Vocabulary glosses: user-supplied Jiten / JMdict deck or Yomitan dictionaries.")
                };
                booksArray.Add(bookObj);
            }

            var chaptersArray = bookObj["chapters"] as JsonArray ?? new JsonArray();
            bookObj["chapters"] = chaptersArray;

            JsonObject? chapterObj = chaptersArray
                .OfType<JsonObject>()
                .FirstOrDefault(c => string.Equals((string?)c["id"], chId, StringComparison.Ordinal)
                                  || ((int?)c["number"] == chNum));

            if (chapterObj is null)
            {
                chapterObj = new JsonObject
                {
                    ["id"] = chId,
                    ["number"] = chNum,
                    ["title"] = chTitle,
                    ["cards"] = new JsonArray()
                };
                chaptersArray.Add(chapterObj);
            }

            var cardsArray = chapterObj["cards"] as JsonArray ?? new JsonArray();
            chapterObj["cards"] = cardsArray;

            var rubyJsonArray = new JsonArray();
            if (request.SentenceRuby is not null)
            {
                foreach (var r in request.SentenceRuby)
                {
                    if (!string.IsNullOrEmpty(r.Reading))
                    {
                        rubyJsonArray.Add(new JsonObject
                        {
                            ["start"] = r.Start,
                            ["end"] = r.End,
                            ["surface"] = r.Surface,
                            ["reading"] = r.Reading,
                            ["source"] = r.Source
                        });
                    }
                }
            }

            var newCardObj = new JsonObject
            {
                ["id"] = cardId,
                ["kind"] = "vocabulary",
                ["prompt"] = request.Expression.Trim(),
                ["meaning"] = meaning,
                ["reading"] = reading,
                ["example"] = sentence,
                ["notes"] = request.Notes ?? "",
                ["example_ruby"] = rubyJsonArray,
                ["dictionary_entries"] = new JsonArray(),
                ["dictionary_metadata"] = new JsonArray(),
                ["definition_candidates"] = new JsonArray(),
                ["sense_selection"] = null,
                ["set_number"] = 0
            };

            // Replace existing card with same id in this chapter or append
            int existingIdx = -1;
            for (int i = 0; i < cardsArray.Count; i++)
            {
                if (cardsArray[i] is JsonObject existingCard &&
                    string.Equals((string?)existingCard["id"], cardId, StringComparison.Ordinal))
                {
                    existingIdx = i;
                    break;
                }
            }

            if (existingIdx >= 0)
            {
                cardsArray[existingIdx] = newCardObj;
            }
            else
            {
                cardsArray.Add(newCardObj);
            }

            AtomicFile.WriteAllText(LibraryJsonPath, rootObj.ToJsonString(WriteJsonOpts) + "\n");
            AnkiTsvWriter.AppendRow(AnkiTsvPath, request.Expression, reading, meaning, sentence, request.BookTitle, chTitle);

            int totalBookCards = chaptersArray
                .OfType<JsonObject>()
                .Sum(c => (c["cards"] as JsonArray)?.Count ?? 0);

            var summary = new FlashcardSummaryItem(
                Id: cardId,
                Kind: "vocabulary",
                Prompt: request.Expression.Trim(),
                Reading: reading,
                Meaning: meaning,
                Example: sentence,
                ChapterId: chId,
                ChapterNumber: chNum,
                ChapterTitle: chTitle,
                SetNumber: 0);

            return new MineFlashcardResult(
                CardId: cardId,
                LibraryJsonPath: LibraryJsonPath,
                AnkiTsvPath: AnkiTsvPath,
                TotalBookCards: totalBookCards,
                Card: summary);
        }
    }

    public IReadOnlyList<FlashcardSummaryItem> ListBookCards(string bookId, int limit = 200) =>
        FlashcardLibraryStore.ListBookCards(LibraryJsonPath, bookId, limit);

    private static string ComputeCardId(string expr, string reading)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($":{expr.Trim()}:{reading.Trim()}"));
        return $"v-{Convert.ToHexStringLower(hash)[..16]}";
    }
}
