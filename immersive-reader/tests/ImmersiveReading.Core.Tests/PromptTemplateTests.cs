using System.Text.Json;
using ImmersiveReading.Core.Prompts;
using Xunit;

namespace ImmersiveReading.Core.Tests;

/// <summary>Spec 19 part A: the C# template engine runs the same vectors as the Python one (PT-7).</summary>
public class PromptTemplateTests
{
    private static string VectorFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "specs", "fixtures", "prompt-vectors.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("specs/fixtures/prompt-vectors.json");
    }

    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorFile()));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            yield return [c.GetProperty("id").GetString()!];
    }

    private static object? ToValue(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out long l) ? (object)l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(ToValue).ToList(),
        _ => null
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SharedVector(string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(VectorFile()));
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().First(x => x.GetProperty("id").GetString() == id);
        string template = c.GetProperty("template").GetString()!;
        var vars = c.TryGetProperty("vars", out var v) && v.ValueKind == JsonValueKind.Object
            ? v.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value))
            : new Dictionary<string, object?>();

        if (c.TryGetProperty("load_error", out var loadError))
        {
            var ex = Assert.Throws<PromptTemplateException>(() => PromptTemplate.Parse(template));
            Assert.Contains(loadError.GetString()!, ex.Message);
        }
        else if (c.TryGetProperty("error", out var error))
        {
            var parsed = PromptTemplate.Parse(template);
            var ex = Assert.Throws<PromptTemplateException>(() => parsed.Render(vars));
            Assert.Contains(error.GetString()!, ex.Message);
        }
        else
        {
            Assert.Equal(c.GetProperty("expect").GetString(), PromptTemplate.Parse(template).Render(vars));
        }
    }

    [Fact]
    public void ABodyErrorNamesTheLineItIsOn_SoAnEditorCanMarkIt()
    {
        string text = "---\nname: t\nversion: 1\nvariables:\n  - a\n---\nline one\nline two {{missing}}\n";

        var ex = Assert.Throws<PromptTemplateException>(() => PromptTemplate.Parse(text));

        Assert.Equal(8, ex.Line);
        Assert.Contains("'missing' is used but not declared", ex.Message);
    }

    [Fact]
    public void UnusedVariablesAreReported_AndTheEverydayShippedTemplatesLoad()
    {
        var t = PromptTemplate.Parse("---\nname: t\nversion: 2\npurpose: p\nvariables:\n  - a\n  - b | default=x\n---\n{{a}}");

        Assert.Equal(["b"], t.Unused);
        Assert.Equal((2, "p"), (t.Version, t.Purpose));
    }

    [Fact]
    public void EveryBuilderPromptThePythonProjectShipsAlsoLoadsHere()
    {
        string dir = "";
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "knowledge-graph", "immersive_knowledge", "prompts");
            if (Directory.Exists(candidate)) { dir = candidate; break; }
        }
        Assert.NotEqual("", dir);
        var files = Directory.GetFiles(dir, "*.prompt.md");
        Assert.NotEmpty(files);
        foreach (string file in files) Assert.NotNull(PromptTemplate.Parse(File.ReadAllText(file), file));
    }
}
