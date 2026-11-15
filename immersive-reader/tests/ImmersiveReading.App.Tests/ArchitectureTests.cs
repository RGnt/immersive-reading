using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ImmersiveReading.App.Tests;

/// <summary>
/// Guards the dependency direction and the separation of concerns documented in wiki/dotnet-architecture.md section 1.
/// The rules are read from the project files and the source text, so a violation fails the build of the tests, not a review.
/// Known debts are listed in explicit allowlists with a ceiling: they may shrink, they may not grow.
/// </summary>
public class ArchitectureTests
{
    private static readonly string SrcDir = FindSrc();

    private static string FindSrc()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ImmersiveReading.sln"))) return Path.Combine(dir.FullName, "src");
        }
        throw new InvalidOperationException("ImmersiveReading.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>"Core", "Library", "Enrichment", "Dictionaries", "App" or "Plugins.Name": the project a path or namespace belongs to.</summary>
    private static string ProjectOfName(string name) => name.StartsWith("ImmersiveReading.", StringComparison.Ordinal) ? name["ImmersiveReading.".Length..] : name;

    private static IEnumerable<(string Project, string File)> SourceFiles()
    {
        foreach (string csproj in Directory.EnumerateFiles(SrcDir, "*.csproj", SearchOption.AllDirectories))
        {
            string project = ProjectOfName(Path.GetFileNameWithoutExtension(csproj));
            string root = Path.GetDirectoryName(csproj)!;
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.StartsWith("bin/") || rel.Contains("/obj/") || rel.Contains("/bin/")) continue;
                yield return (project, file);
            }
        }
    }

    private static string Rel(string file) => Path.GetRelativePath(SrcDir, file).Replace('\\', '/');

    private static Dictionary<string, List<string>> ProjectReferences()
    {
        var map = new Dictionary<string, List<string>>();
        foreach (string csproj in Directory.EnumerateFiles(SrcDir, "*.csproj", SearchOption.AllDirectories))
        {
            var refs = XDocument.Load(csproj).Descendants("ProjectReference")
                .Select(e => ProjectOfName(Path.GetFileNameWithoutExtension((string)e.Attribute("Include")!)))
                .ToList();
            map[ProjectOfName(Path.GetFileNameWithoutExtension(csproj))] = refs;
        }
        return map;
    }

    // Plugins may use these non-Core projects because they implement a contract those projects own.
    private static readonly Dictionary<string, string[]> AllowedPluginDependencies = new()
    {
        ["Plugins.DeckBuilder"] = ["Dictionaries"],     // implements IDeckDictionary on top of the dictionary store
        ["Plugins.PosHighlighter"] = ["Enrichment"]     // is a stage of the enrichment pipeline (Enrichment.Dom)
    };

    [Fact]
    public void Core_And_The_Libraries_Reference_Only_Core()
    {
        var refs = ProjectReferences();
        Assert.Empty(refs["Core"]);
        foreach (string lib in new[] { "Library", "Enrichment", "Dictionaries", "Knowledge" })
            Assert.True(refs[lib].All(r => r == "Core"), $"{lib} must reference only Core, it references: {string.Join(", ", refs[lib])}");
    }

    [Fact]
    public void A_Plugin_References_Core_And_At_Most_The_Allowed_Contract_Owners()
    {
        foreach (var (project, references) in ProjectReferences().Where(p => p.Key.StartsWith("Plugins.")))
        {
            var allowed = new HashSet<string> { "Core" };
            if (AllowedPluginDependencies.TryGetValue(project, out var extra)) allowed.UnionWith(extra);
            var illegal = references.Where(r => !allowed.Contains(r)).ToList();
            Assert.True(illegal.Count == 0, $"{project} may not reference {string.Join(", ", illegal)} (allowed: {string.Join(", ", allowed)})");
        }
    }

    [Fact]
    public void Only_The_App_References_Plugins_And_Nothing_References_The_App()
    {
        foreach (var (project, references) in ProjectReferences())
        {
            Assert.DoesNotContain("App", references);
            if (project != "App") Assert.True(references.All(r => !r.StartsWith("Plugins.") || project.StartsWith("Plugins.") && r == project), $"{project} references a plugin");
        }
    }

    private static readonly Regex UsingRx = new(@"^\s*(?:global\s+)?using\s+(?:static\s+)?(?:\w+\s*=\s*)?(ImmersiveReading\.[\w.]+)\s*;", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The project a namespace like <c>ImmersiveReading.Plugins.DeckBuilder.Enrichment</c> belongs to.</summary>
    private static string ProjectOfNamespace(string ns)
    {
        var parts = ns.Split('.');
        return parts[1] == "Plugins" && parts.Length > 2 ? $"Plugins.{parts[2]}" : parts[1];
    }

    [Fact]
    public void Source_Files_Only_Use_Namespaces_Of_Projects_They_May_Depend_On()
    {
        var violations = new List<string>();
        foreach (var (project, file) in SourceFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match m in UsingRx.Matches(text))
            {
                string target = ProjectOfNamespace(m.Groups[1].Value);
                if (target == project || target == "Core") continue;
                if (project == "App")
                {
                    // the composition root is the only place that may name a concrete plugin
                    if (target.StartsWith("Plugins.") && !Rel(file).StartsWith("ImmersiveReading.App/Composition/"))
                        violations.Add($"{Rel(file)} uses {m.Groups[1].Value}: concrete plugins are named only in App/Composition");
                    continue;
                }
                if (AllowedPluginDependencies.TryGetValue(project, out var extra) && extra.Contains(target)) continue;
                violations.Add($"{Rel(file)} ({project}) uses {m.Groups[1].Value} ({target})");
            }
        }
        Assert.True(violations.Count == 0, "Layering violations:\n" + string.Join("\n", violations));
    }

    // ------------------------------------------------------------------ separation of concerns: where SQL may live

    /// <summary>App files that still contain SQL, with the most statements each may keep. Move statements into Library / Dictionaries services and lower the number; never raise it.</summary>
    private static readonly Dictionary<string, int> AppSqlCeiling = new()
    {
        ["ImmersiveReading.App/Plugins/PluginDatabaseContext.cs"] = 5,   // infrastructure that hands plugins their own database: legitimate
        ["ImmersiveReading.App/Cli/ChapterVerification.cs"] = 2,         // a developer self-check command
        ["ImmersiveReading.App/Rpc/Modules/ReaderRpcModule.cs"] = 8,     // debt: reading progress, bookmarks, kanji queries -> Library
        ["ImmersiveReading.App/Rpc/Modules/BootstrapRpcModule.cs"] = 2,  // debt: dictionary counts -> Dictionaries
        ["ImmersiveReading.App/Rpc/Modules/StudyPackRpcModule.cs"] = 2,  // debt: has_study_pack flag -> Library
        ["ImmersiveReading.App/Rpc/Modules/DictionaryRpcModule.cs"] = 1  // debt: dictionary source listing -> Dictionaries
    };

    [Fact]
    public void Rpc_Layer_Does_Not_Grow_New_SQL()
    {
        var problems = new List<string>();
        foreach (var (project, file) in SourceFiles().Where(f => f.Project == "App"))
        {
            int count = Regex.Matches(File.ReadAllText(file), @"\.CommandText\s*=").Count;
            if (count == 0) continue;
            string rel = Rel(file);
            if (!AppSqlCeiling.TryGetValue(rel, out int ceiling)) problems.Add($"{rel} has {count} SQL statement(s): data access belongs in Library / Dictionaries");
            else if (count > ceiling) problems.Add($"{rel} has {count} SQL statements, the ceiling is {ceiling}");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static readonly Regex LibraryTableRx = new(@"\b(?:FROM|JOIN|UPDATE|INTO)\s+(books|reading_progress|bookmarks|library_folders|book_chapters|book_tags|tags)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void Plugins_Do_Not_Query_The_Library_Tables()
    {
        // A plugin gets library data through IHostLibrary / IBookCatalog and keeps its own data in its own tables.
        var hits = SourceFiles().Where(f => f.Project.StartsWith("Plugins."))
            .Where(f => LibraryTableRx.IsMatch(File.ReadAllText(f.File)))
            .Select(f => Rel(f.File)).ToList();
        Assert.True(hits.Count == 0, "Plugins querying library tables: " + string.Join(", ", hits));
    }

    /// <summary>
    /// Library files that still know the AI companion's tables. Debt: the plugin's schema lives in Library migrations (so its tables can
    /// cascade-delete with a book), and clearing the library deletes its rows by name. A plugin-owned schema in the plugin database plus a
    /// "books removed / library cleared" hook would remove all of this; it needs a data migration, so it is tracked, not done.
    /// </summary>
    private static readonly HashSet<string> LibraryFilesKnowingPluginTables = new(StringComparer.Ordinal)
    {
        "ImmersiveReading.Library/Schema/LibraryMigrations.cs",
        "ImmersiveReading.Library/LibraryDatabase.cs",
        "ImmersiveReading.Library/Books/BookMetadataService.cs",
        "ImmersiveReading.Library/Books/ChapterModelUpgrader.cs"
    };

    [Fact]
    public void Library_Does_Not_Spread_Knowledge_Of_Plugin_Tables()
    {
        var rx = new Regex(@"\b(book_personas|persona_chat_\w+|comprehension_\w+)\b");
        var hits = SourceFiles().Where(f => f.Project is "Library" or "Core" or "Enrichment" or "Dictionaries")
            .Where(f => rx.IsMatch(File.ReadAllText(f.File)))
            .Select(f => Rel(f.File))
            .Where(rel => !LibraryFilesKnowingPluginTables.Contains(rel)).ToList();
        Assert.True(hits.Count == 0, "Plugin tables named outside the known files: " + string.Join(", ", hits));
    }
}
