using System.Text;

namespace ImmersiveReading.App.Tests;

/// <summary>
/// Characterization tests for the <c>ir-app://</c> custom scheme handler
/// (<see cref="Program.HandleAppSchemeRequest(string, string, string, string?, out string)"/>).
/// </summary>
public sealed class ProgramSchemeTests : IDisposable
{
    private readonly string _root;
    private readonly string _wwwroot;
    private readonly string _covers;
    private readonly string _studyOutput;

    public ProgramSchemeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ir-scheme-tests-{Guid.NewGuid():N}");
        _wwwroot = Path.Combine(_root, "wwwroot");
        _covers = Path.Combine(_root, "covers");
        _studyOutput = Path.Combine(_root, "output");
        Directory.CreateDirectory(Path.Combine(_wwwroot, "assets"));
        Directory.CreateDirectory(_covers);
        Directory.CreateDirectory(Path.Combine(_studyOutput, "pdf"));

        File.WriteAllText(Path.Combine(_wwwroot, "index.html"), "<html>index</html>");
        File.WriteAllText(Path.Combine(_wwwroot, "app.js"), "console.log(1);");
        File.WriteAllText(Path.Combine(_wwwroot, "assets", "reader.css"), "body{}");
        File.WriteAllText(Path.Combine(_wwwroot, "assets", "bridge.js"), "var x=1;");
        File.WriteAllBytes(Path.Combine(_covers, "book-1.png"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_studyOutput, "pdf", "master.pdf"), [4, 5, 6]);
        // A file outside every served root, used by traversal attempts.
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "secret");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Ignore cleanup locks on Windows
        }
    }

    private (string Body, string ContentType) Request(string url, string? studyOutput = null)
    {
        using var stream = Program.HandleAppSchemeRequest(url, _wwwroot, _covers, studyOutput, out string contentType);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return (Encoding.UTF8.GetString(ms.ToArray()), contentType);
    }

    [Theory]
    [InlineData("ir-app://app/index.html", "<html>index</html>", "text/html; charset=utf-8")]
    [InlineData("ir-app://app/app.js", "console.log(1);", "application/javascript; charset=utf-8")]
    public void RootFiles_AreServed_WithMimeType(string url, string expectedBody, string expectedMime)
    {
        var (body, mime) = Request(url);

        Assert.Equal(expectedBody, body);
        Assert.Equal(expectedMime, mime);
    }

    [Theory]
    [InlineData("ir-app://assets/reader.css", "body{}", "text/css; charset=utf-8")]
    [InlineData("ir-app://assets/bridge.js", "var x=1;", "application/javascript; charset=utf-8")]
    public void HostAsSubfolder_IsServed(string url, string expectedBody, string expectedMime)
    {
        var (body, mime) = Request(url);

        Assert.Equal(expectedBody, body);
        Assert.Equal(expectedMime, mime);
    }

    [Fact]
    public void MissingFile_ReturnsNotFoundText()
    {
        var (body, mime) = Request("ir-app://app/nope.html");

        Assert.Equal("Not found", body);
        Assert.Equal("text/plain; charset=utf-8", mime);
    }

    [Theory]
    [InlineData("ir-app://covers/book-1.png")]
    [InlineData("ir-app://app/covers/book-1.png")]
    public void Covers_AreServed_AsPng(string url)
    {
        using var stream = Program.HandleAppSchemeRequest(url, _wwwroot, _covers, out string mime);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        Assert.Equal("image/png", mime);
        Assert.Equal(new byte[] { 1, 2, 3 }, ms.ToArray());
    }

    [Fact]
    public void MissingCover_ReturnsEmptyStream()
    {
        using var stream = Program.HandleAppSchemeRequest("ir-app://covers/none.png", _wwwroot, _covers, out _);

        Assert.Equal(0, stream.Length);
    }

    [Theory]
    [InlineData("ir-app://studypack/pdf/master.pdf")]
    [InlineData("ir-app://app/studypack/pdf/master.pdf")]
    public void StudyPack_IsServed_WhenOutputDirProvided(string url)
    {
        using var stream = Program.HandleAppSchemeRequest(url, _wwwroot, _covers, _studyOutput, out string mime);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        Assert.Equal("application/pdf", mime);
        Assert.Equal(new byte[] { 4, 5, 6 }, ms.ToArray());
    }

    [Fact]
    public void StudyPack_IsNotServed_WhenOutputDirIsNull()
    {
        var (body, _) = Request("ir-app://studypack/pdf/master.pdf", studyOutput: null);

        Assert.Equal("Not found", body);
    }

    [Theory]
    [InlineData("ir-app://app/../secret.txt")]
    [InlineData("ir-app://app/%2e%2e/secret.txt")]
    [InlineData("ir-app://app/assets/../../secret.txt")]
    [InlineData("ir-app://covers/../secret.txt")]
    public void PathTraversal_NeverServesFilesOutsideRoots(string url)
    {
        var (body, _) = Request(url);

        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSchemeHandler_Instance_ServesFiles()
    {
        var handler = new ImmersiveReading.App.Hosting.AppSchemeHandler(_wwwroot, _covers, _studyOutput);
        using var stream = handler.Handle("ir-app://app/index.html", out string mime);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        Assert.Equal("<html>index</html>", Encoding.UTF8.GetString(ms.ToArray()));
        Assert.Equal("text/html; charset=utf-8", mime);
    }

    /// <summary>
    /// Plugin UI is loaded as a blob module whose bare imports are rewritten (client/src/services/
    /// pluginImportRewriter.ts). A first-party ui.js that imports anything outside that supported list
    /// would fail at load time, so check it here against the list in the TypeScript source itself.
    /// </summary>
    [Fact]
    public void FirstPartyPluginUi_OnlyImportsSpecifiersTheHostProvides()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "immersive-reader", "src", "Plugins")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);

        string rewriter = File.ReadAllText(Path.Combine(dir!, "immersive-reader", "src", "ImmersiveReading.App",
            "client", "src", "services", "pluginImportRewriter.ts"));
        var arrayMatch = System.Text.RegularExpressions.Regex.Match(
            rewriter, @"SUPPORTED_SPECIFIERS\s*=\s*\[(.*?)\]", System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(arrayMatch.Success, "SUPPORTED_SPECIFIERS not found in pluginImportRewriter.ts");
        var supported = System.Text.RegularExpressions.Regex.Matches(arrayMatch.Groups[1].Value, "'([^']+)'")
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Contains("react", supported);

        var fromRe = new System.Text.RegularExpressions.Regex(@"from\s*['""]([^'""]+)['""]");
        var uiFiles = Directory.GetFiles(Path.Combine(dir!, "immersive-reader", "src", "Plugins"), "ui.js", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(uiFiles);

        foreach (var file in uiFiles)
        {
            foreach (System.Text.RegularExpressions.Match m in fromRe.Matches(File.ReadAllText(file)))
            {
                Assert.True(supported.Contains(m.Groups[1].Value),
                    $"{file}: imports '{m.Groups[1].Value}', which the plugin loader does not provide.");
            }
        }
    }

    /// <summary>
    /// The host loads plugin ui.js as a plain ES module, so JSX must be compiled first
    /// (`npm run build:plugins`). A raw-JSX ui.js fails at load with "Unexpected token '&lt;'".
    /// </summary>
    [Fact]
    public void FirstPartyPluginUi_IsPlainJavaScript_NotRawJsx()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "immersive-reader", "src", "Plugins")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);

        var jsxAtLineStart = new System.Text.RegularExpressions.Regex(@"^\s*</?[A-Za-z][\w.]*[\s/>]", System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (var file in Directory.GetFiles(Path.Combine(dir!, "immersive-reader", "src", "Plugins"), "ui.js", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            Assert.False(jsxAtLineStart.IsMatch(File.ReadAllText(file)),
                $"{file} looks like raw JSX. Write ui.src.jsx and run `npm run build:plugins` in the client folder.");
        }
    }
}
