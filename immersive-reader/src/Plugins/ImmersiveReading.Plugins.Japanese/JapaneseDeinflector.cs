using System.Collections.Concurrent;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.Japanese;

public sealed class JapaneseDeinflector : IDeinflector
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<DeinflectionCandidate>> Cache = new(StringComparer.Ordinal);

    private sealed record Rule(
        string InflectedSuffix,
        string DictionarySuffix,
        string RuleName,
        string FromPos,
        string ToPos
    );

    private static readonly Rule[] Rules =
    [
        // ----------------- Polite (-masu) -----------------
        new("ました", "る", "past", "polite", "v1"),
        new("ました", "う", "past", "polite", "v5u"),
        new("ました", "く", "past", "polite", "v5k"),
        new("ました", "ぐ", "past", "polite", "v5g"),
        new("ました", "す", "past", "polite", "v5s"),
        new("ました", "つ", "past", "polite", "v5t"),
        new("ました", "ぬ", "past", "polite", "v5n"),
        new("ました", "ぶ", "past", "polite", "v5b"),
        new("ました", "む", "past", "polite", "v5m"),
        new("ました", "る", "past", "polite", "v5r"),
        new("ました", "する", "past", "polite", "vs"),
        new("ました", "くる", "past", "polite", "vk"),

        new("ません", "る", "negative", "polite", "v1"),
        new("ません", "う", "negative", "polite", "v5u"),
        new("ません", "く", "negative", "polite", "v5k"),
        new("ません", "ぐ", "negative", "polite", "v5g"),
        new("ません", "す", "negative", "polite", "v5s"),
        new("ません", "つ", "negative", "polite", "v5t"),
        new("ません", "ぬ", "negative", "polite", "v5n"),
        new("ません", "ぶ", "negative", "polite", "v5b"),
        new("ません", "む", "negative", "polite", "v5m"),
        new("ません", "る", "negative", "polite", "v5r"),
        new("ません", "する", "negative", "polite", "vs"),
        new("ません", "くる", "negative", "polite", "vk"),

        new("ましょう", "る", "volitional", "polite", "v1"),
        new("ましょう", "う", "volitional", "polite", "v5u"),
        new("ましょう", "く", "volitional", "polite", "v5k"),
        new("ましょう", "ぐ", "volitional", "polite", "v5g"),
        new("ましょう", "す", "volitional", "polite", "v5s"),
        new("ましょう", "つ", "volitional", "polite", "v5t"),
        new("ましょう", "ぬ", "volitional", "polite", "v5n"),
        new("ましょう", "ぶ", "volitional", "polite", "v5b"),
        new("ましょう", "む", "volitional", "polite", "v5m"),
        new("ましょう", "る", "volitional", "polite", "v5r"),
        new("ましょう", "する", "volitional", "polite", "vs"),
        new("ましょう", "くる", "volitional", "polite", "vk"),

        new("ます", "る", "polite", "verb", "v1"),
        new("います", "う", "polite", "verb", "v5u"),
        new("きます", "く", "polite", "verb", "v5k"),
        new("ぎます", "ぐ", "polite", "verb", "v5g"),
        new("します", "す", "polite", "verb", "v5s"),
        new("ちます", "つ", "polite", "verb", "v5t"),
        new("にます", "ぬ", "polite", "verb", "v5n"),
        new("びます", "ぶ", "polite", "verb", "v5b"),
        new("みます", "む", "polite", "verb", "v5m"),
        new("ります", "る", "polite", "verb", "v5r"),
        new("します", "する", "polite", "verb", "vs"),
        new("きます", "くる", "polite", "verb", "vk"),

        // ----------------- Past (-ta) & Te-form (-te) -----------------
        new("た", "る", "past", "verb", "v1"),
        new("て", "る", "te-form", "verb", "v1"),

        // Godan past & te
        new("った", "う", "past", "verb", "v5u"),
        new("った", "つ", "past", "verb", "v5t"),
        new("った", "る", "past", "verb", "v5r"),
        new("った", "く", "past", "verb", "v5k-s"), // 行った -> 行く
        new("って", "う", "te-form", "verb", "v5u"),
        new("って", "つ", "te-form", "verb", "v5t"),
        new("って", "る", "te-form", "verb", "v5r"),
        new("って", "く", "te-form", "verb", "v5k-s"), // 行って -> 行く

        new("いた", "く", "past", "verb", "v5k"),
        new("いて", "く", "te-form", "verb", "v5k"),

        new("いだ", "ぐ", "past", "verb", "v5g"),
        new("いで", "ぐ", "te-form", "verb", "v5g"),

        new("した", "す", "past", "verb", "v5s"),
        new("して", "す", "te-form", "verb", "v5s"),
        new("した", "する", "past", "verb", "vs"),
        new("して", "する", "te-form", "verb", "vs"),

        new("んだ", "ぬ", "past", "verb", "v5n"),
        new("んだ", "ぶ", "past", "verb", "v5b"),
        new("んだ", "む", "past", "verb", "v5m"),
        new("んで", "ぬ", "te-form", "verb", "v5n"),
        new("んで", "ぶ", "te-form", "verb", "v5b"),
        new("んで", "む", "te-form", "verb", "v5m"),

        new("きた", "くる", "past", "verb", "vk"),
        new("きて", "くる", "te-form", "verb", "vk"),

        // ----------------- Contractions (-chau, -teru) -----------------
        new("んじゃった", "む", "contraction (-chau)", "verb", "v5m"),
        new("んじゃった", "ぶ", "contraction (-chau)", "verb", "v5b"),
        new("んじゃった", "ぬ", "contraction (-chau)", "verb", "v5n"),
        new("んじゃう", "む", "contraction (-chau)", "verb", "v5m"),
        new("んじゃう", "ぶ", "contraction (-chau)", "verb", "v5b"),
        new("んじゃう", "ぬ", "contraction (-chau)", "verb", "v5n"),

        new("っちゃった", "う", "contraction (-chau)", "verb", "v5u"),
        new("っちゃった", "つ", "contraction (-chau)", "verb", "v5t"),
        new("っちゃった", "る", "contraction (-chau)", "verb", "v5r"),
        new("っちゃった", "く", "contraction (-chau)", "verb", "v5k-s"),
        new("っちゃう", "う", "contraction (-chau)", "verb", "v5u"),
        new("っちゃう", "つ", "contraction (-chau)", "verb", "v5t"),
        new("っちゃう", "る", "contraction (-chau)", "verb", "v5r"),
        new("っちゃう", "く", "contraction (-chau)", "verb", "v5k-s"),

        new("いちゃった", "く", "contraction (-chau)", "verb", "v5k"),
        new("いちゃう", "く", "contraction (-chau)", "verb", "v5k"),
        new("しちゃった", "す", "contraction (-chau)", "verb", "v5s"),
        new("しちゃう", "す", "contraction (-chau)", "verb", "v5s"),
        new("しちゃった", "する", "contraction (-chau)", "verb", "vs"),
        new("しちゃう", "する", "contraction (-chau)", "verb", "vs"),

        new("ちゃった", "る", "contraction (-chau)", "verb", "v1"),
        new("ちゃう", "る", "contraction (-chau)", "verb", "v1"),

        new("てる", "る", "contraction (-teru)", "verb", "v1"),
        new("てた", "る", "contraction (-teru)", "verb", "v1"),
        new("でる", "む", "contraction (-teru)", "verb", "v5m"),
        new("でた", "む", "contraction (-teru)", "verb", "v5m"),

        // ----------------- Negative Past (-nakatta) -----------------
        new("なかった", "る", "negative", "verb", "v1"),
        new("わなかった", "う", "negative", "verb", "v5u"),
        new("かなかった", "く", "negative", "verb", "v5k"),
        new("がなかった", "ぐ", "negative", "verb", "v5g"),
        new("さなかった", "す", "negative", "verb", "v5s"),
        new("たなかった", "つ", "negative", "verb", "v5t"),
        new("ななかった", "ぬ", "negative", "verb", "v5n"),
        new("ばなかった", "ぶ", "negative", "verb", "v5b"),
        new("まなかった", "む", "negative", "verb", "v5m"),
        new("らなかった", "る", "negative", "verb", "v5r"),
        new("しなかった", "する", "negative", "verb", "vs"),
        new("こなかった", "くる", "negative", "verb", "vk"),
        new("くなかった", "い", "past", "adj-i", "adj-i"),

        // ----------------- Negative (-nai) -----------------
        new("ない", "る", "negative", "verb", "v1"),
        new("わない", "う", "negative", "verb", "v5u"),
        new("かない", "く", "negative", "verb", "v5k"),
        new("がない", "ぐ", "negative", "verb", "v5g"),
        new("さない", "す", "negative", "verb", "v5s"),
        new("たない", "つ", "negative", "verb", "v5t"),
        new("なない", "ぬ", "negative", "verb", "v5n"),
        new("ばない", "ぶ", "negative", "verb", "v5b"),
        new("まない", "む", "negative", "verb", "v5m"),
        new("らない", "る", "negative", "verb", "v5r"),
        new("しない", "する", "negative", "verb", "vs"),
        new("こない", "くる", "negative", "verb", "vk"),
        new("くない", "い", "negative", "adj-i", "adj-i"),

        // ----------------- I-Adjectives -----------------
        new("かった", "い", "past", "adj-i", "adj-i"),
        new("くて", "い", "te-form", "adj-i", "adj-i"),
        new("く", "い", "adverbial", "adj-i", "adj-i"),
        new("さ", "い", "noun-forming (-sa)", "adj-i", "adj-i"),
        new("ければ", "い", "conditional (-ba)", "adj-i", "adj-i"),

        // ----------------- Passive & Potential -----------------
        new("られる", "る", "passive", "verb", "v1"),
        new("られる", "る", "potential", "verb", "v1"),
        new("われる", "う", "passive", "verb", "v5u"),
        new("かれる", "く", "passive", "verb", "v5k"),
        new("がれる", "ぐ", "passive", "verb", "v5g"),
        new("される", "す", "passive", "verb", "v5s"),
        new("たれる", "つ", "passive", "verb", "v5t"),
        new("なれる", "ぬ", "passive", "verb", "v5n"),
        new("ばれる", "ぶ", "passive", "verb", "v5b"),
        new("まれる", "む", "passive", "verb", "v5m"),
        new("られる", "る", "passive", "verb", "v5r"),

        // Potential (Godan -eru)
        new("える", "う", "potential", "verb", "v5u"),
        new("ける", "く", "potential", "verb", "v5k"),
        new("げる", "ぐ", "potential", "verb", "v5g"),
        new("せる", "す", "potential", "verb", "v5s"),
        new("てる", "つ", "potential", "verb", "v5t"),
        new("ねる", "ぬ", "potential", "verb", "v5n"),
        new("べる", "ぶ", "potential", "verb", "v5b"),
        new("める", "む", "potential", "verb", "v5m"),
        new("れる", "る", "potential", "verb", "v5r"),

        // ----------------- Causative & Causative-Passive -----------------
        new("させる", "る", "causative", "verb", "v1"),
        new("わせる", "う", "causative", "verb", "v5u"),
        new("かせる", "く", "causative", "verb", "v5k"),
        new("がせる", "ぐ", "causative", "verb", "v5g"),
        new("させる", "す", "causative", "verb", "v5s"),
        new("たせる", "つ", "causative", "verb", "v5t"),
        new("なせる", "ぬ", "causative", "verb", "v5n"),
        new("ばせる", "ぶ", "causative", "verb", "v5b"),
        new("ませる", "む", "causative", "verb", "v5m"),
        new("らせる", "る", "causative", "verb", "v5r"),

        // Short causative / causative-passive
        new("される", "す", "causative-passive", "verb", "v5s"),
        new("せられる", "る", "causative-passive", "verb", "v1")
    ];

    public IReadOnlyList<DeinflectionCandidate> Deinflect(ReadOnlySpan<char> surface)
    {
        if (surface.IsEmpty)
        {
            return Array.Empty<DeinflectionCandidate>();
        }

        var key = surface.ToString();
        return Cache.GetOrAdd(key, static k => ExecuteDeinflection(k));
    }

    private static IReadOnlyList<DeinflectionCandidate> ExecuteDeinflection(string surface)
    {
        var results = new List<DeinflectionCandidate>();
        var seen = new HashSet<(string Form, string Pos)>(EqualityComparer<(string, string)>.Default);

        var queue = new Queue<(string Form, List<string> RuleChain, string Pos)>();
        queue.Enqueue((surface, new List<string>(), "any"));

        while (queue.Count > 0)
        {
            var (currentForm, chain, currentPos) = queue.Dequeue();

            if (chain.Count >= 4)
            {
                continue;
            }

            foreach (var rule in Rules)
            {
                if (currentForm.Length < rule.InflectedSuffix.Length)
                {
                    continue;
                }

                if (!currentForm.EndsWith(rule.InflectedSuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (currentPos != "any" && currentPos != rule.FromPos && rule.FromPos != "verb")
                {
                    continue;
                }

                var stemLength = currentForm.Length - rule.InflectedSuffix.Length;
                var baseCandidate = string.Concat(currentForm.AsSpan(0, stemLength), rule.DictionarySuffix);

                if (baseCandidate.Length == 0)
                {
                    continue;
                }

                var nextChain = new List<string>(chain) { rule.RuleName };

                if (seen.Add((baseCandidate, rule.ToPos)))
                {
                    results.Add(new DeinflectionCandidate(
                        BaseForm: baseCandidate,
                        ExpectedReadingPrefix: null,
                        RuleChain: nextChain,
                        PartOfSpeechConstraint: rule.ToPos
                    ));

                    queue.Enqueue((baseCandidate, nextChain, rule.ToPos));
                }
            }
        }

        return results;
    }
}
