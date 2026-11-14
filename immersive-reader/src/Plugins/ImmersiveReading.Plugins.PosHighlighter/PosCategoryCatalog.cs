namespace ImmersiveReading.Plugins.PosHighlighter;

/// <summary>Display + styling metadata for one highlightable <see cref="PosCategory"/>.</summary>
public sealed record PosCategoryInfo(
    PosCategory Category,
    string Id,
    string Label,
    string JpLabel,
    string Example,
    string Selector,
    Func<PosHighlighterSettings, string> ColorOf);

/// <summary>
/// Single source of truth for the highlightable categories: ids used over RPC/config, UI labels,
/// and the CSS selectors <see cref="PosDomEnrichmentStage.GenerateCss"/> emits. The client never
/// duplicates selectors; it only receives generated CSS (spec 10 NFR-2).
/// </summary>
public static class PosCategoryCatalog
{
    public static IReadOnlyList<PosCategoryInfo> All { get; } =
    [
        new(PosCategory.AdjectiveI, "adjI", "i-Adjectives", "形容詞", "美味しい",
            ".ir-tok.adjective-i, .adjective-i, .ir-tok.ir-pos-adj-i, .ir-pos-adj-i, .ir-tok.adjective:not(.adjective-na)",
            s => s.ColorAdjI),
        new(PosCategory.AdjectiveNa, "adjNa", "na-Adjectives", "形容動詞", "静か",
            ".ir-tok.adjective-na, .adjective-na, .ir-tok.ir-pos-adj-na, .ir-pos-adj-na",
            s => s.ColorAdjNa),
        new(PosCategory.VerbGodan, "verbGodan", "u-Verbs (Godan)", "五段動詞", "読む",
            ".ir-tok.verb-u, .verb-u, .ir-tok.ir-pos-verb-u, .ir-pos-verb-u, .ir-tok.verb:not(.verb-ru):not(.verb-irr)",
            s => s.ColorVerbGodan),
        new(PosCategory.VerbIchidan, "verbIchidan", "ru-Verbs (Ichidan)", "一段動詞", "食べる",
            ".ir-tok.verb-ru, .verb-ru, .ir-tok.ir-pos-verb-ru, .ir-pos-verb-ru",
            s => s.ColorVerbIchidan),
        new(PosCategory.VerbIrregular, "verbIrregular", "Irregular Verbs", "サ変・カ変", "する",
            ".ir-tok.verb-irr, .verb-irr, .ir-tok.ir-pos-verb-irr, .ir-pos-verb-irr",
            s => s.ColorVerbIrregular),
        new(PosCategory.Suffix, "suffix", "Suffixes", "接尾辞", "さん",
            ".ir-tok.suffix, .suffix, .ir-tok.ir-pos-suffix, .ir-pos-suffix",
            s => s.ColorSuffix),
        new(PosCategory.ParticleSingle, "particleSingle", "Particles (single kana)", "助詞", "は が を",
            ".ir-tok.particle-single, .particle-single, .ir-tok.ir-pos-prt-single, .ir-pos-prt-single, .ir-tok.particle:not(.particle-multi)",
            s => s.ColorParticleSingle),
        new(PosCategory.ParticleMulti, "particleMulti", "Particles (compound)", "助詞", "から まで",
            ".ir-tok.particle-multi, .particle-multi, .ir-tok.ir-pos-prt-multi, .ir-pos-prt-multi",
            s => s.ColorParticleMulti),
        new(PosCategory.Adverb, "adverb", "Adverbs", "副詞", "ゆっくり",
            ".ir-tok.adverb, .adverb, .ir-tok.ir-pos-adverb, .ir-pos-adverb",
            s => s.ColorAdverb),
        new(PosCategory.Noun, "noun", "Nouns", "名詞", "先生",
            ".ir-tok.noun, .noun, .ir-tok.ir-pos-noun, .ir-pos-noun",
            s => s.ColorNoun),
        new(PosCategory.Conjunction, "conjunction", "Conjunctions", "接続詞", "しかし",
            ".ir-tok.conjunction, .conjunction, .ir-tok.ir-pos-conj, .ir-pos-conj",
            s => s.ColorConjunction),
        new(PosCategory.Auxiliary, "auxiliary", "Auxiliary Verbs", "助動詞", "です ない",
            ".ir-tok.auxiliary, .auxiliary, .ir-tok.ir-pos-aux, .ir-pos-aux",
            s => s.ColorAuxiliary),
        new(PosCategory.Interjection, "interjection", "Interjections", "感動詞", "ああ",
            ".ir-tok.interjection, .interjection, .ir-tok.ir-pos-intj, .ir-pos-intj",
            s => s.ColorInterjection),
    ];

    public static IReadOnlySet<string> AllIds { get; } =
        new HashSet<string>(All.Select(c => c.Id), StringComparer.Ordinal);

    /// <summary>Keeps only known ids, de-duplicated, in catalog order (spec 10 NFR-3).</summary>
    public static List<string> Sanitize(IEnumerable<string?>? ids)
    {
        var wanted = new HashSet<string?>(ids ?? [], StringComparer.Ordinal);
        return All.Where(c => wanted.Contains(c.Id)).Select(c => c.Id).ToList();
    }
}
