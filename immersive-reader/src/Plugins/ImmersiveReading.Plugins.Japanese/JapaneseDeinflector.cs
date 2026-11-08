using System.Collections.Concurrent;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Plugins.Japanese.Rules;

namespace ImmersiveReading.Plugins.Japanese;

/// <summary>
/// Breadth-first deinflection engine for Japanese verbs and adjectives.
/// Traverses inflection rule trees up to depth 4 using an injected <see cref="IDeinflectionRuleProvider"/>.
/// </summary>
public sealed class JapaneseDeinflector : IDeinflector
{
    private const int MaxCacheSize = 10_000;
    private readonly ConcurrentDictionary<string, IReadOnlyList<DeinflectionCandidate>> _cache = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<DeinflectionRule> _rules;

    public int CacheCount => _cache.Count;

    public JapaneseDeinflector(IDeinflectionRuleProvider? ruleProvider = null)
    {
        _rules = (ruleProvider ?? StandardJapaneseDeinflectionRules.Instance).GetRules();
    }

    public IReadOnlyList<DeinflectionCandidate> Deinflect(ReadOnlySpan<char> surface)
    {
        if (surface.IsEmpty)
        {
            return Array.Empty<DeinflectionCandidate>();
        }

        if (_cache.Count > MaxCacheSize)
        {
            _cache.Clear();
        }

        var key = surface.ToString();
        return _cache.GetOrAdd(key, k => ExecuteDeinflection(k));
    }

    private IReadOnlyList<DeinflectionCandidate> ExecuteDeinflection(string surface)
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

            foreach (var rule in _rules)
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
