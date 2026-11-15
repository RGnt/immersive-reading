using System.Text;

namespace ImmersiveReading.Core.Knowledge;

/// <summary>
/// Language-neutral lexical matching shared by the sources: character bigrams, so it works for Japanese (no word spaces) and for
/// English alike. Rarer grams weigh more, so "です" in every fact does not make everything relevant. Embeddings can replace this
/// behind the same <see cref="IKnowledgeSource"/> without any consumer noticing.
/// </summary>
public static class KnowledgeMatch
{
    /// <summary>Matches below this are noise (a shared particle, a shared letter pair).</summary>
    public const double MinScore = 0.2;

    /// <summary>NFKC, lower case, letters and digits only: "Ｈｅｌｌｏ, World!" and "hello world" compare equal.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder();
        foreach (char c in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static HashSet<string> Grams(string text)
    {
        string n = Normalize(text);
        var grams = new HashSet<string>(StringComparer.Ordinal);
        if (n.Length == 0) return grams;
        if (n.Length == 1) { grams.Add(n); return grams; }
        for (int i = 0; i + 1 < n.Length; i++) grams.Add(n.Substring(i, 2));
        return grams;
    }

    /// <summary>Scores every document against the query in one pass (document frequencies come from the same list). Same order as the input.</summary>
    public static double[] Score(string query, IReadOnlyList<string> documents)
    {
        var scores = new double[documents.Count];
        var q = Grams(query);
        if (q.Count == 0 || documents.Count == 0) return scores;

        var docGrams = documents.Select(Grams).ToList();
        var weight = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string g in q)
        {
            int df = docGrams.Count(d => d.Contains(g));
            weight[g] = Math.Log(1 + documents.Count / (1.0 + df));
        }
        double total = weight.Values.Sum();
        for (int i = 0; i < documents.Count; i++)
            scores[i] = total <= 0 ? 0 : q.Where(g => docGrams[i].Contains(g)).Sum(g => weight[g]) / total;
        return scores;
    }

    public static string Clean(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    /// <summary>Stable comparison key for "is this the same fact again".</summary>
    public static string Key(string text) => Normalize(text);
}
