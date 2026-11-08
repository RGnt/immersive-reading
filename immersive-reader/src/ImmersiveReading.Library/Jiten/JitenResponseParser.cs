using System.Text.Json;

namespace ImmersiveReading.Library.Jiten;

public static class JitenResponseParser
{
    public static string GetMediaTypeName(int mediaType) => mediaType switch
    {
        1 => "Anime",
        2 => "Drama",
        3 => "Movie",
        4 => "Novel",
        5 => "Non-Fiction",
        6 => "Video Game",
        7 => "Visual Novel",
        8 => "Web Novel",
        9 => "Manga",
        10 => "Audio",
        11 => "YouTube",
        _ => "Media"
    };

    public static string GetGenreName(int genre) => genre switch
    {
        1 => "Action",
        2 => "Adventure",
        3 => "Comedy",
        4 => "Drama",
        5 => "Ecchi",
        6 => "Fantasy",
        7 => "Horror",
        8 => "Mecha",
        9 => "Music",
        10 => "Mystery",
        11 => "Psychological",
        12 => "Romance",
        13 => "Sci-Fi",
        14 => "Slice of Life",
        15 => "Sports",
        16 => "Supernatural",
        17 => "Thriller",
        18 => "Adult Only",
        _ => $"Genre {genre}"
    };

    public static string GetLinkTypeName(int linkType) => linkType switch
    {
        1 => "Web",
        2 => "VNDB",
        3 => "TMDB",
        4 => "AniList",
        5 => "MAL",
        6 => "Google Books",
        7 => "IMDb",
        8 => "IGDB",
        9 => "Syosetsu",
        10 => "Bookmeter",
        11 => "Amazon",
        12 => "YouTube",
        13 => "Kakuyomu",
        _ => "External"
    };

    public static JitenDeckSummary ParseDeck(JsonElement elem)
    {
        long deckId = elem.TryGetProperty("deckId", out var dId) ? dId.GetInt64() : 0;
        string origTitle = elem.TryGetProperty("originalTitle", out var ot) ? ot.GetString() ?? "" : "";
        string? romaji = elem.TryGetProperty("romajiTitle", out var rt) ? rt.GetString() : null;
        string? english = elem.TryGetProperty("englishTitle", out var et) ? et.GetString() : null;
        string? coverRaw = elem.TryGetProperty("coverName", out var cv) ? cv.GetString() : null;
        string? coverUrl = null;
        if (!string.IsNullOrWhiteSpace(coverRaw) && !coverRaw.Equals("nocover.jpg", StringComparison.OrdinalIgnoreCase))
        {
            coverUrl = coverRaw.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? coverRaw
                : $"https://cdn.jiten.moe/{deckId}/{coverRaw}";
        }

        string? desc = elem.TryGetProperty("description", out var ds) ? ds.GetString() : null;
        int mediaType = elem.TryGetProperty("mediaType", out var mt) ? mt.GetInt32() : 0;
        int charCount = elem.TryGetProperty("characterCount", out var cc) ? cc.GetInt32() : 0;
        int wordCount = elem.TryGetProperty("wordCount", out var wc) ? wc.GetInt32() : 0;
        int uniqueWordCount = elem.TryGetProperty("uniqueWordCount", out var uwc) ? uwc.GetInt32() : 0;
        int uniqueKanjiCount = elem.TryGetProperty("uniqueKanjiCount", out var ukc) ? ukc.GetInt32() : 0;
        double diffRaw = elem.TryGetProperty("difficultyRaw", out var dr) && dr.ValueKind == JsonValueKind.Number
            ? dr.GetDouble()
            : (elem.TryGetProperty("difficulty", out var df) && df.ValueKind == JsonValueKind.Number ? df.GetDouble() : 0.0);
        int childrenCount = elem.TryGetProperty("childrenDeckCount", out var ch) ? ch.GetInt32() : 0;

        var genres = new List<string>();
        if (elem.TryGetProperty("genres", out var gArr) && gArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var gElem in gArr.EnumerateArray())
            {
                if (gElem.ValueKind == JsonValueKind.Number)
                {
                    genres.Add(GetGenreName(gElem.GetInt32()));
                }
            }
        }

        var tags = new List<JitenDeckTag>();
        if (elem.TryGetProperty("tags", out var tArr) && tArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var tElem in tArr.EnumerateArray())
            {
                string tagName = tElem.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                int pct = tElem.TryGetProperty("percentage", out var tp) ? tp.GetInt32() : 0;
                if (!string.IsNullOrWhiteSpace(tagName))
                {
                    tags.Add(new JitenDeckTag(tagName, pct));
                }
            }
        }

        var links = new List<JitenDeckLink>();
        if (elem.TryGetProperty("links", out var lArr) && lArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var lElem in lArr.EnumerateArray())
            {
                int lt = lElem.TryGetProperty("linkType", out var ltProp) ? ltProp.GetInt32() : 0;
                string lUrl = lElem.TryGetProperty("url", out var luProp) ? luProp.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(lUrl))
                {
                    links.Add(new JitenDeckLink(lt, GetLinkTypeName(lt), lUrl));
                }
            }
        }

        return new JitenDeckSummary(
            DeckId: deckId,
            OriginalTitle: origTitle,
            RomajiTitle: romaji,
            EnglishTitle: english,
            CoverUrl: coverUrl,
            MediaType: mediaType,
            MediaTypeName: GetMediaTypeName(mediaType),
            Description: desc,
            CharacterCount: charCount,
            WordCount: wordCount,
            UniqueWordCount: uniqueWordCount,
            UniqueKanjiCount: uniqueKanjiCount,
            Difficulty: Math.Round(diffRaw, 2),
            DifficultyRaw: diffRaw,
            ChildrenDeckCount: childrenCount,
            Genres: genres,
            Tags: tags,
            Links: links,
            JitenUrl: $"https://jiten.moe/decks/media/{deckId}"
        );
    }

    public static JitenDifficultyResult ParseDifficulty(long deckId, JsonElement root)
    {
        double diff = root.TryGetProperty("difficulty", out var dProp) ? dProp.GetDouble() : 0.0;
        double peak = root.TryGetProperty("peak", out var pkProp) ? pkProp.GetDouble() : 0.0;
        int voterCount = root.TryGetProperty("distinctVoterCount", out var vcProp) ? vcProp.GetInt32() : 0;

        var deciles = new Dictionary<string, double>();
        if (root.TryGetProperty("deciles", out var decProp) && decProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in decProp.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    deciles[prop.Name] = Math.Round(prop.Value.GetDouble(), 2);
                }
            }
        }

        var progression = new List<JitenProgressionSegment>();
        if (root.TryGetProperty("progression", out var progProp) && progProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in progProp.EnumerateArray())
            {
                int segment = seg.TryGetProperty("segment", out var sProp) ? sProp.GetInt32() : 0;
                double sDiff = seg.TryGetProperty("difficulty", out var sdProp) ? sdProp.GetDouble() : 0.0;
                double sPeak = seg.TryGetProperty("peak", out var spProp) ? spProp.GetDouble() : 0.0;
                progression.Add(new JitenProgressionSegment(segment, Math.Round(sDiff, 2), Math.Round(sPeak, 2)));
            }
        }

        return new JitenDifficultyResult(
            DeckId: deckId,
            Difficulty: Math.Round(diff, 2),
            Peak: Math.Round(peak, 2),
            DistinctVoterCount: voterCount,
            Deciles: deciles,
            Progression: progression
        );
    }

    public static JitenDeckStatsResult ParseStats(long deckId, JsonElement root)
    {
        int totalUniqueWords = root.TryGetProperty("totalUniqueWords", out var tuw) ? tuw.GetInt32() : 0;
        string? computedAt = root.TryGetProperty("computedAt", out var ca) ? ca.GetString() : null;
        double rSquared = root.TryGetProperty("rSquared", out var rsq) ? rsq.GetDouble() : 0.0;

        var milestones = new Dictionary<string, int>();
        if (root.TryGetProperty("milestones", out var msProp) && msProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in msProp.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    milestones[prop.Name] = prop.Value.GetInt32();
                }
            }
        }

        return new JitenDeckStatsResult(
            DeckId: deckId,
            TotalUniqueWords: totalUniqueWords,
            RSquared: Math.Round(rSquared, 4),
            ComputedAt: computedAt,
            Milestones: milestones
        );
    }

    public static IReadOnlyList<JitenVocabularyPreviewWord> ParseVocabularyPreview(JsonElement root)
    {
        var words = new List<JitenVocabularyPreviewWord>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var wElem in root.EnumerateArray())
            {
                long wordId = wElem.TryGetProperty("wordId", out var wi) ? wi.GetInt64() : 0;
                int readingIndex = wElem.TryGetProperty("readingIndex", out var ri) ? ri.GetInt32() : 0;
                string reading = wElem.TryGetProperty("reading", out var rd) ? rd.GetString() ?? "" : "";
                string readingFurigana = wElem.TryGetProperty("readingFurigana", out var rf) ? rf.GetString() ?? "" : "";
                string mainDef = wElem.TryGetProperty("mainDefinition", out var md) ? md.GetString() ?? "" : "";
                int frequencyRank = wElem.TryGetProperty("frequencyRank", out var fr) ? fr.GetInt32() : 0;
                int occurrences = wElem.TryGetProperty("occurrences", out var oc) ? oc.GetInt32() : 0;

                words.Add(new JitenVocabularyPreviewWord(
                    WordId: wordId,
                    ReadingIndex: readingIndex,
                    Reading: reading,
                    ReadingFurigana: readingFurigana,
                    MainDefinition: mainDef,
                    FrequencyRank: frequencyRank,
                    Occurrences: occurrences
                ));
            }
        }
        return words;
    }

    public static JitenDeckSummary? FindMatchingSubDeck(IReadOnlyList<JitenDeckSummary> subDecks, double volumeIndex, string? bookTitle)
    {
        if (subDecks == null || subDecks.Count == 0) return null;

        int intVol = (int)Math.Round(volumeIndex);
        string volStr = intVol.ToString();
        string volPad = intVol.ToString("D2");

        // 1. Exact match on volume pattern in title: "Volume 1", "Vol 1", "Vol. 1", "第1巻", "第1"
        foreach (var sub in subDecks)
        {
            string t = sub.OriginalTitle;
            if (t.Equals($"Volume {volStr}", StringComparison.OrdinalIgnoreCase) ||
                t.Equals($"Vol {volStr}", StringComparison.OrdinalIgnoreCase) ||
                t.Equals($"Vol. {volStr}", StringComparison.OrdinalIgnoreCase) ||
                t.Equals($"第{volStr}巻", StringComparison.OrdinalIgnoreCase) ||
                t.Equals($"第{volStr}", StringComparison.OrdinalIgnoreCase) ||
                t.Equals(volStr, StringComparison.OrdinalIgnoreCase) ||
                t.EndsWith($" {volStr}", StringComparison.OrdinalIgnoreCase) ||
                t.EndsWith($" {volPad}", StringComparison.OrdinalIgnoreCase) ||
                t.EndsWith(volStr, StringComparison.OrdinalIgnoreCase))
            {
                return sub;
            }
        }

        // 2. Direct ordinal position if 1-based index fits in bounds
        if (intVol >= 1 && intVol <= subDecks.Count)
        {
            return subDecks[intVol - 1];
        }

        return null;
    }
}

