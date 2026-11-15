using ImmersiveReading.Core.Prompts;
using Xunit;

namespace ImmersiveReading.Core.Tests;

/// <summary>Spec 19 PT-5: the user's edited copy of a prompt is used only when it is valid and keeps the built-in's contract.</summary>
public class PromptCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pc-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string BuiltIn = "---\nname: coach.improve\nversion: 2\npurpose: feedback\nvariables:\n  - language | default=English\n  - utterance | required | untrusted\n  - words | type=list\n---\nExplain in {{language}}.\n{{utterance}}\n";

    private PromptCatalog Catalog(string? folder = null)
    {
        var catalog = new PromptCatalog(folder ?? Path.Combine(_dir, "prompts"));
        catalog.RegisterBuiltIns("coachPlugin", [BuiltIn]);
        return catalog;
    }

    private static string Edited(string body = "Be kind.", string header = "version: 2", string variables = "  - language | default=English\n  - utterance | required | untrusted\n  - words | type=list")
        => $"---\nname: coach.improve\n{header}\nvariables:\n{variables}\n---\n{body} {{{{language}}}} {{{{utterance}}}}\n";

    private static readonly Dictionary<string, object?> Values = new() { ["utterance"] = "こんにちは" };

    [Fact]
    public void WithoutACopyTheBuiltInIsUsed_AndListedWithItsContract()
    {
        var catalog = Catalog();

        var info = Assert.Single(catalog.List());
        Assert.Equal(("coach.improve", "coachPlugin", 2, false), (info.Name, info.Owner, info.Version, info.UsingOverride));
        Assert.Equal(["language", "utterance", "words"], info.Variables.Select(v => v.Name).ToArray());
        Assert.Contains("Explain in English.", catalog.Render("coach.improve", Values));
        Assert.Throws<KeyNotFoundException>(() => catalog.Render("nope"));
    }

    [Fact]
    public void AValidCopyReplacesTheBuiltIn_SurvivesARestart_AndResetBringsTheBuiltInBack()
    {
        string folder = Path.Combine(_dir, "prompts");
        Catalog(folder).SaveOverride("coach.improve", Edited("Be very kind."));

        var restarted = Catalog(folder);
        Assert.StartsWith("Be very kind.", restarted.Render("coach.improve", Values));
        Assert.True(restarted.Get("coach.improve")!.UsingOverride);

        Assert.True(restarted.ResetOverride("coach.improve"));
        Assert.Contains("Explain in English.", restarted.Render("coach.improve", Values));
        Assert.False(Catalog(folder).Get("coach.improve")!.UsingOverride);
        Assert.False(restarted.ResetOverride("coach.improve"));         // nothing left to remove
    }

    [Theory]
    [InlineData("a copy without front matter", "no front matter at all", "front matter")]
    [InlineData("the name changed", "---\nname: other\nversion: 2\nvariables:\n  - language | default=x\n  - utterance | required | untrusted\n  - words | type=list\n---\nhi {{language}} {{utterance}}", "must stay 'coach.improve'")]
    [InlineData("an older version", "---\nname: coach.improve\nversion: 1\nvariables:\n  - language | default=x\n  - utterance | required | untrusted\n  - words | type=list\n---\nhi", "at least 2")]
    [InlineData("a variable the program fills in was removed", "---\nname: coach.improve\nversion: 2\nvariables:\n  - utterance | required | untrusted\n  - words | type=list\n---\nhi {{utterance}}", "'language' must stay declared")]
    [InlineData("untrusted was dropped", "---\nname: coach.improve\nversion: 2\nvariables:\n  - language | default=x\n  - utterance | required\n  - words | type=list\n---\nhi", "must stay 'untrusted'")]
    [InlineData("a type changed", "---\nname: coach.improve\nversion: 2\nvariables:\n  - language | default=x\n  - utterance | required | untrusted\n  - words\n---\nhi", "keep type 'list'")]
    [InlineData("a new required variable", "---\nname: coach.improve\nversion: 2\nvariables:\n  - language | default=x\n  - utterance | required | untrusted\n  - words | type=list\n  - mood | required\n---\nhi", "cannot be 'required'")]
    [InlineData("a body error", "---\nname: coach.improve\nversion: 2\nvariables:\n  - language | default=x\n  - utterance | required | untrusted\n  - words | type=list\n---\nhi {{typo}}", "'typo' is used but not declared")]
    public void ACopyThatBreaksTheContractIsRefused_AndNothingIsWritten(string why, string text, string expected)
    {
        string folder = Path.Combine(_dir, "prompts");
        var catalog = Catalog(folder);

        var check = catalog.Check("coach.improve", text);
        var ex = Assert.Throws<PromptTemplateException>(() => catalog.SaveOverride("coach.improve", text));

        Assert.False(check.Ok, why);
        Assert.Contains(expected, check.Error);
        Assert.Contains(expected, ex.Message);
        Assert.False(Directory.Exists(folder) && Directory.GetFiles(folder).Length > 0);
        Assert.False(catalog.Get("coach.improve")!.UsingOverride);
    }

    [Fact]
    public void ABrokenFileOnDiskIsIgnoredAndExplained_NeverHalfApplied()
    {
        string folder = Path.Combine(_dir, "prompts");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "coach.improve.prompt.md"), "---\nname: coach.improve\nversion: 2\nvariables:\n  - utterance | required | untrusted\n---\nhi");

        var catalog = Catalog(folder);

        var info = catalog.Get("coach.improve")!;
        Assert.False(info.UsingOverride);
        Assert.Contains("'language' must stay declared", info.OverrideError);
        Assert.NotNull(info.OverrideText);                               // the editor can still show what the user wrote
        Assert.Contains("Explain in English.", catalog.Render("coach.improve", Values));
    }

    [Fact]
    public void UnusedVariablesAreOnlyAWarning_AndAddedOptionalVariablesAreFine()
    {
        var catalog = Catalog();
        string text = Edited(variables: "  - language | default=English\n  - utterance | required | untrusted\n  - words | type=list\n  - tone | default=warm");

        var check = catalog.Check("coach.improve", text);

        Assert.True(check.Ok);
        Assert.Contains(check.Warnings, w => w.Contains("'words'"));
        Assert.Contains(check.Warnings, w => w.Contains("'tone'"));
    }

    [Fact]
    public void TheBuilderGetsTheTextInForce_AsFiles_AndTwoOwnersCannotShareAName()
    {
        var catalog = Catalog();
        catalog.SaveOverride("coach.improve", Edited("Mine."));
        string target = Path.Combine(_dir, "effective");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "stale.prompt.md"), "old");

        int written = catalog.WriteEffective("coachPlugin", target);

        Assert.Equal(1, written);
        Assert.Equal(["coach.improve.prompt.md"], Directory.GetFiles(target).Select(f => Path.GetFileName(f)!).ToArray());
        Assert.StartsWith("---\nname: coach.improve", File.ReadAllText(Path.Combine(target, "coach.improve.prompt.md")));
        Assert.Contains("Mine.", File.ReadAllText(Path.Combine(target, "coach.improve.prompt.md")));
        Assert.Throws<InvalidOperationException>(() => catalog.RegisterBuiltIns("someoneElse", [BuiltIn]));
    }

    [Fact]
    public void DisposingTheRegistrationRemovesThePrompt_ButKeepsTheUsersFile()
    {
        string folder = Path.Combine(_dir, "prompts");
        var catalog = new PromptCatalog(folder);
        var registration = catalog.RegisterBuiltIns("coachPlugin", [BuiltIn]);
        catalog.SaveOverride("coach.improve", Edited("Mine."));

        registration.Dispose();

        Assert.Empty(catalog.List());
        Assert.True(File.Exists(Path.Combine(folder, "coach.improve.prompt.md")));
        catalog.RegisterBuiltIns("coachPlugin", [BuiltIn]);
        Assert.True(catalog.Get("coach.improve")!.UsingOverride);       // comes back when the plugin does
    }
}
