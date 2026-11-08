namespace ImmersiveReading.Core.Plugins;

/// <summary>
/// Represents a single grammatical inflection suffix mapping.
/// </summary>
public sealed record DeinflectionRule(
    string InflectedSuffix,
    string DictionarySuffix,
    string RuleName,
    string FromPos,
    string ToPos
);

/// <summary>
/// Strategy provider for deinflection rules.
/// </summary>
public interface IDeinflectionRuleProvider
{
    IReadOnlyList<DeinflectionRule> GetRules();
}

