using System;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.PosHighlighter;

public enum PosCategory
{
    Unknown,
    AdjectiveI,
    AdjectiveNa,
    VerbGodan,
    VerbIchidan,
    VerbIrregular,
    Suffix,
    ParticleSingle,
    ParticleMulti,
    Adverb,
    Conjunction,
    Auxiliary,
    Interjection,
    Noun
}

public static class PosClassifier
{
    public static PosCategory Classify(AnalyzedToken token)
    {
        string pos = token.PartOfSpeech ?? "";
        string posSub = token.PartOfSpeechSub ?? "";
        string conjType = token.ConjugationType ?? "";
        string surface = token.Surface ?? "";

        // 1. Suffixes & Prefixes (Must precede particles and nouns)
        // IPAdic: 名詞,接尾 or 動詞,接尾 or 形容詞,接尾. UniDic: 接尾辞, 接頭辞
        if (posSub.StartsWith("接尾", StringComparison.Ordinal) ||
            pos == "接尾辞" ||
            pos.Contains("接尾", StringComparison.Ordinal) ||
            posSub.StartsWith("接頭", StringComparison.Ordinal) ||
            pos == "接頭辞" ||
            pos.Contains("接頭", StringComparison.Ordinal))
        {
            return PosCategory.Suffix;
        }

        // 2. Particles (Single-kana vs Multi-kana/Compound)
        if (pos.StartsWith("助詞", StringComparison.Ordinal))
        {
            return surface.Length == 1 ? PosCategory.ParticleSingle : PosCategory.ParticleMulti;
        }

        // 3. Verbs by conjugation type (Godan / u-verb, Ichidan / ru-verb, Irregular)
        if (pos.StartsWith("動詞", StringComparison.Ordinal))
        {
            if (conjType.Contains("五段", StringComparison.Ordinal))
            {
                return PosCategory.VerbGodan;
            }
            if (conjType.Contains("一段", StringComparison.Ordinal))
            {
                return PosCategory.VerbIchidan;
            }
            if (conjType.Contains("サ変", StringComparison.Ordinal) ||
                conjType.Contains("カ変", StringComparison.Ordinal) ||
                conjType.Contains("不規則", StringComparison.Ordinal) ||
                conjType.Contains("変格", StringComparison.Ordinal))
            {
                return PosCategory.VerbIrregular;
            }
            // Default to Godan if unclassified verb
            return PosCategory.VerbGodan;
        }

        // 4. Adjectives (i-adjectives vs na-adjectives)
        // Na-adjectives: IPAdic tags as 名詞,形容動詞語幹; UniDic tags as 形状詞, 形状詞可能, サ変形状詞可能
        if (pos.Contains("形状詞", StringComparison.Ordinal) ||
            posSub.Contains("形状詞", StringComparison.Ordinal) ||
            pos.Contains("形容動詞", StringComparison.Ordinal) ||
            posSub.Contains("形容動詞", StringComparison.Ordinal))
        {
            return PosCategory.AdjectiveNa;
        }

        // I-adjectives: IPAdic tags as 形容詞; UniDic tags as 形容詞
        if (pos.StartsWith("形容詞", StringComparison.Ordinal) ||
            conjType.Contains("形容詞", StringComparison.Ordinal))
        {
            return PosCategory.AdjectiveI;
        }

        // 5. Adverbs (UniDic: 副詞 or 副詞可能)
        if (pos.StartsWith("副詞", StringComparison.Ordinal) ||
            posSub.Contains("副詞", StringComparison.Ordinal) ||
            pos.Contains("副詞", StringComparison.Ordinal))
        {
            return PosCategory.Adverb;
        }

        // 6. Conjunctions
        if (pos.StartsWith("接続詞", StringComparison.Ordinal))
        {
            return PosCategory.Conjunction;
        }

        // 7. Auxiliaries
        if (pos.StartsWith("助動詞", StringComparison.Ordinal))
        {
            return PosCategory.Auxiliary;
        }

        // 8. Interjections
        if (pos.StartsWith("感動詞", StringComparison.Ordinal))
        {
            return PosCategory.Interjection;
        }

        // 9. Verbal Noun functioning as suru-verb
        if (posSub.Contains("サ変", StringComparison.Ordinal) &&
            (surface.EndsWith("する", StringComparison.Ordinal) ||
             surface.EndsWith("した", StringComparison.Ordinal) ||
             surface.EndsWith("して", StringComparison.Ordinal)))
        {
            return PosCategory.VerbIrregular;
        }

        // 10. Nouns & Pronouns
        if (pos.StartsWith("名詞", StringComparison.Ordinal) ||
            pos.StartsWith("代名詞", StringComparison.Ordinal))
        {
            return PosCategory.Noun;
        }

        return PosCategory.Unknown;
    }

    public static string ToCssClass(PosCategory category) => category switch
    {
        PosCategory.AdjectiveI => "ir-pos-adj-i",
        PosCategory.AdjectiveNa => "ir-pos-adj-na",
        PosCategory.VerbGodan => "ir-pos-verb-u",
        PosCategory.VerbIchidan => "ir-pos-verb-ru",
        PosCategory.VerbIrregular => "ir-pos-verb-irr",
        PosCategory.Suffix => "ir-pos-suffix",
        PosCategory.ParticleSingle => "ir-pos-prt-single",
        PosCategory.ParticleMulti => "ir-pos-prt-multi",
        PosCategory.Adverb => "ir-pos-adverb",
        PosCategory.Conjunction => "ir-pos-conj",
        PosCategory.Auxiliary => "ir-pos-aux",
        PosCategory.Interjection => "ir-pos-intj",
        PosCategory.Noun => "ir-pos-noun",
        _ => ""
    };

    public static string ToCode(PosCategory category) => category switch
    {
        PosCategory.AdjectiveI => "adj-i",
        PosCategory.AdjectiveNa => "adj-na",
        PosCategory.VerbGodan => "verb-u",
        PosCategory.VerbIchidan => "verb-ru",
        PosCategory.VerbIrregular => "verb-irr",
        PosCategory.Suffix => "suffix",
        PosCategory.ParticleSingle => "prt-single",
        PosCategory.ParticleMulti => "prt-multi",
        PosCategory.Adverb => "adverb",
        PosCategory.Conjunction => "conj",
        PosCategory.Auxiliary => "aux",
        PosCategory.Interjection => "intj",
        PosCategory.Noun => "noun",
        _ => "unk"
    };

    public static string ToWordType(PosCategory category) => category switch
    {
        PosCategory.AdjectiveI or PosCategory.AdjectiveNa => "adjective",
        PosCategory.VerbGodan or PosCategory.VerbIchidan or PosCategory.VerbIrregular => "verb",
        PosCategory.ParticleSingle or PosCategory.ParticleMulti => "particle",
        PosCategory.Suffix => "suffix",
        PosCategory.Adverb => "adverb",
        PosCategory.Conjunction => "conjunction",
        PosCategory.Auxiliary => "auxiliary",
        PosCategory.Interjection => "interjection",
        PosCategory.Noun => "noun",
        _ => "unknown"
    };

    public static string ToSubType(PosCategory category) => category switch
    {
        PosCategory.AdjectiveI => "adjective-i",
        PosCategory.AdjectiveNa => "adjective-na",
        PosCategory.VerbGodan => "verb-u",
        PosCategory.VerbIchidan => "verb-ru",
        PosCategory.VerbIrregular => "verb-irr",
        PosCategory.ParticleSingle => "particle-single",
        PosCategory.ParticleMulti => "particle-multi",
        PosCategory.Suffix => "suffix",
        PosCategory.Adverb => "adverb",
        PosCategory.Conjunction => "conjunction",
        PosCategory.Auxiliary => "auxiliary",
        PosCategory.Interjection => "interjection",
        PosCategory.Noun => "noun",
        _ => "unknown"
    };

    public static PosClassification ClassifyToken(int id, AnalyzedToken token)
    {
        var category = Classify(token);
        string wt = ToWordType(category);
        string st = ToSubType(category);
        string cd = ToCode(category);
        string legacy = ToCssClass(category);

        var classes = new List<string>(3);
        if (wt != "unknown") classes.Add(wt);
        if (st != "unknown" && st != wt) classes.Add(st);
        if (!string.IsNullOrEmpty(legacy) && !classes.Contains(legacy)) classes.Add(legacy);

        return new PosClassification(id, wt, st, cd, classes);
    }
}

public sealed record PosClassification(
    int Id,
    string WordType,
    string SubType,
    string Code,
    IReadOnlyList<string> Classes
);
