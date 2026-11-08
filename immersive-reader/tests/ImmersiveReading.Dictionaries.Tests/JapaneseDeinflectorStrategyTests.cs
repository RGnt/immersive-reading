using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Plugins.Japanese;
using ImmersiveReading.Plugins.Japanese.Rules;
using Xunit;

namespace ImmersiveReading.Dictionaries.Tests;

public sealed class JapaneseDeinflectorStrategyTests
{
    private sealed class CustomMockRuleProvider : IDeinflectionRuleProvider
    {
        public IReadOnlyList<DeinflectionRule> GetRules() =>
        [
            new("でござる", "だ", "polite-copula", "any", "copula")
        ];
    }

    [Fact]
    public void StandardRules_ContainsPoliteAndPastRules()
    {
        var rules = StandardJapaneseDeinflectionRules.Instance.GetRules();
        Assert.NotEmpty(rules);
        Assert.Contains(rules, r => r.InflectedSuffix == "ました" && r.DictionarySuffix == "る");
    }

    [Fact]
    public void Deinflector_WithCustomRuleProvider_ExecutesCustomRules()
    {
        var deinflector = new JapaneseDeinflector(new CustomMockRuleProvider());
        var results = deinflector.Deinflect("拙者でござる");

        Assert.Single(results);
        Assert.Equal("拙者だ", results[0].BaseForm);
        Assert.Equal(["polite-copula"], results[0].RuleChain);
        Assert.Equal("copula", results[0].PartOfSpeechConstraint);
    }

    [Fact]
    public void Deinflector_DefaultConstructor_UsesStandardRules()
    {
        var deinflector = new JapaneseDeinflector();
        var results = deinflector.Deinflect("食べさせられた");

        Assert.NotEmpty(results);
        Assert.Contains(results, r => r.BaseForm == "食べる");
    }
}

