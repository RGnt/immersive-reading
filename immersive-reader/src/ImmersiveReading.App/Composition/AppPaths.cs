namespace ImmersiveReading.App.Composition;

public sealed record AppPaths(
    string ReaderRoot,
    string WorkspaceRoot,
    string AppDataDir,
    string DbPath,
    string DictDbPath,
    string CoversDir,
    string ExtractorDir,
    string ExtractorOutputDir,
    string ExtractorBooksDir,
    string WwwrootDir)
{
    public static AppPaths Discover(string? baseDir = null)
    {
        baseDir ??= AppContext.BaseDirectory;
        string readerRoot = FindReaderProjectRoot(baseDir);
        string workspaceRoot = FindWorkspaceRoot(readerRoot);
        string appDataDir = Path.Combine(readerRoot, ".library-data");
        Directory.CreateDirectory(appDataDir);

        string dbPath = Path.Combine(appDataDir, "library.db");
        string coversDir = Path.Combine(appDataDir, "covers");
        Directory.CreateDirectory(coversDir);

        string extractorDir = Path.Combine(workspaceRoot, "enrichement-extractor");
        string extractorOutputDir = Path.Combine(extractorDir, "output");
        string extractorBooksDir = Path.Combine(extractorDir, "books");
        string dictDbPath = Path.Combine(appDataDir, "dictionaries.db");
        string wwwrootDir = ResolveWwwrootDir(baseDir, readerRoot);

        return new AppPaths(
            ReaderRoot: readerRoot,
            WorkspaceRoot: workspaceRoot,
            AppDataDir: appDataDir,
            DbPath: dbPath,
            DictDbPath: dictDbPath,
            CoversDir: coversDir,
            ExtractorDir: extractorDir,
            ExtractorOutputDir: extractorOutputDir,
            ExtractorBooksDir: extractorBooksDir,
            WwwrootDir: wwwrootDir);
    }

    private static string FindReaderProjectRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ImmersiveReading.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return startDir;
    }

    private static string FindWorkspaceRoot(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "enrichement-extractor")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return startDir;
    }

    private static string ResolveWwwrootDir(string baseDir, string readerRoot)
    {
        string srcWww = Path.Combine(readerRoot, "src", "ImmersiveReading.App", "wwwroot");
        if (Directory.Exists(srcWww)) return srcWww;

        string localWww = Path.Combine(baseDir, "wwwroot");
        return localWww;
    }
}

