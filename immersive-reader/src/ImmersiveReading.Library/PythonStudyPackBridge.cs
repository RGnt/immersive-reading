using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImmersiveReading.Library;

public sealed record StudyPackPdfArtifact(
    string FileName,
    string Title,
    string Kind,          // "master" | "chapter-kanji" | "chapter-vocabulary"
    string FullPath,
    string AppSchemeUrl,  // e.g. "ir-app://studypack/pdf/book-.../kanji-master.pdf"
    long SizeBytes
);

public sealed record VocabularySetWordItem(
    string CardId,
    string Word,
    string Reading,
    string ChapterId,
    int StudyNumber
);

public sealed record VocabularyLearningSet(
    string Id,
    int Number,
    int Size,
    IReadOnlyList<VocabularySetWordItem> Words
);

public sealed record StudyPackBookOverview(
    string BookId,
    bool Available,
    string? Generation,
    string? ActiveManifestPath,
    IReadOnlyList<StudyPackPdfArtifact> MasterPdfs,
    IReadOnlyList<StudyPackPdfArtifact> ChapterPdfs,
    IReadOnlyList<VocabularyLearningSet> VocabularySets,
    int TotalVocabularyInSets,
    bool HasHtmlCompanion,
    string? HtmlEntryPath
);

public sealed record StudyPackBuildResult(
    bool Success,
    int ExitCode,
    string OutputLog,
    StudyPackBookOverview? StudyPack
);

/// <summary>
/// Holds an OS-level byte-range read lease on a published enrichement-extractor snapshot
/// under .publication/leases/{generation}/{uuid}.lock so immersive.retention will never
/// garbage-collect the release while the C# reader/inspector is using it.
/// </summary>
public sealed class StudyPackLease : IDisposable
{
    private FileStream? _lockStream;
    public string Generation { get; }
    public string LockFilePath { get; }
    public bool IsActive => _lockStream is not null;

    internal StudyPackLease(string generation, string lockFilePath, FileStream lockStream)
    {
        Generation = generation;
        LockFilePath = lockFilePath;
        _lockStream = lockStream;
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _lockStream, null);
        if (stream is null) return;

        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                try
                {
                    stream.Unlock(0, 1);
                }
                catch
                {
                    // Ignore unlock errors on close
                }
            }
        }
        finally
        {
            stream.Dispose();
        }

        try
        {
            if (File.Exists(LockFilePath))
            {
                File.Delete(LockFilePath);
            }
        }
        catch
        {
            // Ignore cleanup race
        }
    }
}

/// <summary>
/// Connects the .NET Library &amp; Reader to the Python enrichement-extractor output directory
/// and .publication/ atomic release store.
/// </summary>
public sealed class PythonStudyPackBridge
{
    private static readonly Regex SafeHex32Regex = new("^[a-f0-9]{32}$", RegexOptions.Compiled);
    private static readonly Regex SafeBookIdRegex = new("^book-[a-f0-9]{16}$", RegexOptions.Compiled);

    private readonly string _outputRoot;
    private readonly string? _extractorRoot;

    public string OutputRoot => _outputRoot;

    public PythonStudyPackBridge(string outputRoot, string? extractorRoot = null)
    {
        _outputRoot = Path.GetFullPath(outputRoot);
        _extractorRoot = extractorRoot is not null
            ? Path.GetFullPath(extractorRoot)
            : Path.GetDirectoryName(_outputRoot);
    }

    /// <summary>
    /// Reads .publication/active.json if present and returns the active 32-hex generation ID and manifest root.
    /// </summary>
    public (string? Generation, string? ManifestPath, JsonDocument? ManifestDoc) TryReadActivePublication()
    {
        string activePath = Path.Combine(_outputRoot, ".publication", "active.json");
        if (!File.Exists(activePath))
        {
            return (null, null, null);
        }

        try
        {
            using var activeDoc = JsonDocument.Parse(File.ReadAllText(activePath, Encoding.UTF8));
            var root = activeDoc.RootElement;
            string? gen = root.TryGetProperty("generation", out var gProp) ? gProp.GetString() : null;
            string? relManifest = root.TryGetProperty("manifest", out var mProp) ? mProp.GetString() : null;
            string? expectedSha = root.TryGetProperty("sha256", out var sProp) ? sProp.GetString() : null;

            if (string.IsNullOrEmpty(gen) || !SafeHex32Regex.IsMatch(gen) || string.IsNullOrEmpty(relManifest))
            {
                return (null, null, null);
            }

            string fullManifestPath = Path.GetFullPath(Path.Combine(_outputRoot, relManifest.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullManifestPath.StartsWith(_outputRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullManifestPath))
            {
                return (null, null, null);
            }

            if (!string.IsNullOrEmpty(expectedSha))
            {
                byte[] manifestBytes = File.ReadAllBytes(fullManifestPath);
                string actualSha = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null, null);
                }
            }

            var manifestDoc = JsonDocument.Parse(File.ReadAllText(fullManifestPath, Encoding.UTF8));
            return (gen, fullManifestPath, manifestDoc);
        }
        catch
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Acquires an OS-level byte-range lease under .publication/leases/{generation}/{uuid}.lock
    /// so immersive.retention will preserve the snapshot while in use.
    /// </summary>
    public StudyPackLease? AcquireSnapshotLease(string? generation = null)
    {
        string? targetGen = generation;
        if (string.IsNullOrEmpty(targetGen))
        {
            var (activeGen, _, doc) = TryReadActivePublication();
            doc?.Dispose();
            targetGen = activeGen;
        }

        if (string.IsNullOrEmpty(targetGen) || !SafeHex32Regex.IsMatch(targetGen))
        {
            return null;
        }

        string leaseDir = Path.Combine(_outputRoot, ".publication", "leases", targetGen);
        Directory.CreateDirectory(leaseDir);

        string leaseId = Guid.NewGuid().ToString("N"); // 32-char lowercase hex matching GENERATION regex in retention.py
        string lockFilePath = Path.Combine(leaseDir, $"{leaseId}.lock");

        var fs = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }));
            fs.SetLength(0);
            fs.Write(payload, 0, payload.Length);
            fs.Flush(flushToDisk: true);

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                fs.Lock(0, 1);
            }

            return new StudyPackLease(targetGen, lockFilePath, fs);
        }
        catch
        {
            fs.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Inspects both .publication/releases/{generation} (when active.json is present) and
    /// compatibility output directories (output/data/{bookId}, output/pdf/{bookId}, output/html/{bookId})
    /// to discover all generated PDFs and 20-word vocabulary sets for a book.
    /// </summary>
    public StudyPackBookOverview GetBookStudyPack(string bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId) || !SafeBookIdRegex.IsMatch(bookId))
        {
            return EmptyOverview(bookId);
        }

        var (generation, manifestPath, manifestDoc) = TryReadActivePublication();
        using (manifestDoc)
        {
            string? studySnapshotRoot = null;
            if (manifestDoc is not null &&
                manifestDoc.RootElement.TryGetProperty("books", out var booksEl) &&
                booksEl.TryGetProperty(bookId, out var bookEntry))
            {
                if (bookEntry.TryGetProperty("study", out var studyEntry) &&
                    studyEntry.TryGetProperty("root", out var rootProp))
                {
                    string? relRoot = rootProp.GetString();
                    if (!string.IsNullOrEmpty(relRoot))
                    {
                        string candidate = Path.GetFullPath(Path.Combine(_outputRoot, relRoot.Replace('/', Path.DirectorySeparatorChar)));
                        if (candidate.StartsWith(_outputRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(candidate))
                        {
                            studySnapshotRoot = candidate;
                        }
                    }
                }
            }

            // Resolve data, pdf, and html directories (preferring immutable snapshot root if present, falling back to compatibility view)
            string dataDir = ResolveBookSubdir(studySnapshotRoot, "data", bookId);
            string pdfDir = ResolveBookSubdir(studySnapshotRoot, "pdf", bookId);
            string htmlDir = ResolveBookSubdir(studySnapshotRoot, "html", bookId);

            var masterPdfs = new List<StudyPackPdfArtifact>();
            var chapterPdfs = new List<StudyPackPdfArtifact>();

            if (Directory.Exists(pdfDir))
            {
                var masterSpec = new (string FileName, string Title)[]
                {
                    ("kanji-master.pdf", "Kanji Master Sheet (A4)"),
                    ("vocabulary-master.pdf", "Vocabulary Master Sheet (A4)"),
                    ("grammar-master.pdf", "Grammar Master Sheet (A4)")
                };

                foreach (var (fileName, title) in masterSpec)
                {
                    string fullPdf = Path.Combine(pdfDir, fileName);
                    if (File.Exists(fullPdf))
                    {
                        var fi = new FileInfo(fullPdf);
                        masterPdfs.Add(new StudyPackPdfArtifact(
                            FileName: fileName,
                            Title: title,
                            Kind: "master",
                            FullPath: fullPdf,
                            AppSchemeUrl: $"ir-app://studypack/pdf/{bookId}/{fileName}",
                            SizeBytes: fi.Length));
                    }
                }

                foreach (var file in Directory.GetFiles(pdfDir, "chapter-*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(file);
                    string fName = fi.Name;
                    string kind = fName.Contains("kanji", StringComparison.OrdinalIgnoreCase) ? "chapter-kanji" : "chapter-vocabulary";
                    chapterPdfs.Add(new StudyPackPdfArtifact(
                        FileName: fName,
                        Title: FormatChapterPdfTitle(fName),
                        Kind: kind,
                        FullPath: fi.FullName,
                        AppSchemeUrl: $"ir-app://studypack/pdf/{bookId}/{fName}",
                        SizeBytes: fi.Length));
                }
            }

            var vocabSets = LoadVocabularySets(dataDir);
            int totalWordsInSets = vocabSets.Sum(s => s.Words.Count);

            string htmlIndex = Path.Combine(htmlDir, "index.html");
            bool hasHtml = File.Exists(htmlIndex);

            bool available = masterPdfs.Count > 0 || chapterPdfs.Count > 0 || vocabSets.Count > 0 || File.Exists(Path.Combine(dataDir, "reader.json"));

            return new StudyPackBookOverview(
                BookId: bookId,
                Available: available,
                Generation: generation,
                ActiveManifestPath: manifestPath,
                MasterPdfs: masterPdfs,
                ChapterPdfs: chapterPdfs,
                VocabularySets: vocabSets,
                TotalVocabularyInSets: totalWordsInSets,
                HasHtmlCompanion: hasHtml,
                HtmlEntryPath: hasHtml ? htmlIndex : null);
        }
    }

    /// <summary>
    /// Invokes the local Python enrichement-extractor CLI to build or enrich a book folder.
    /// </summary>
    public async Task<StudyPackBuildResult> BuildStudyPackAsync(
        string bookId,
        string bookFolderPath,
        bool readerOnly = false,
        IProgress<string>? onProgressLine = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_extractorRoot) || !Directory.Exists(_extractorRoot))
        {
            return new StudyPackBuildResult(false, -1, "enrichement-extractor root directory not found.", null);
        }

        string pythonExe = Path.Combine(_extractorRoot, ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
        {
            pythonExe = "python";
        }

        string subCommand = readerOnly ? "enrich" : "build";
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            WorkingDirectory = _extractorRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("immersive.cli");
        psi.ArgumentList.Add(subCommand);
        psi.ArgumentList.Add("--book");
        psi.ArgumentList.Add(bookFolderPath);
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add(_outputRoot);

        var logSb = new StringBuilder();
        using var proc = new Process { StartInfo = psi };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (logSb) logSb.AppendLine(e.Data);
                onProgressLine?.Report(e.Data);
            }
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (logSb) logSb.AppendLine(e.Data);
                onProgressLine?.Report(e.Data);
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        bool ok = proc.ExitCode == 0;
        var overview = GetBookStudyPack(bookId);
        return new StudyPackBuildResult(ok, proc.ExitCode, logSb.ToString(), overview);
    }

    private string ResolveBookSubdir(string? studySnapshotRoot, string category, string bookId)
    {
        if (!string.IsNullOrEmpty(studySnapshotRoot))
        {
            string snapCandidate = Path.Combine(studySnapshotRoot, category, bookId);
            if (Directory.Exists(snapCandidate))
            {
                return snapCandidate;
            }
        }
        return Path.Combine(_outputRoot, category, bookId);
    }

    private static List<VocabularyLearningSet> LoadVocabularySets(string dataDir)
    {
        var result = new List<VocabularyLearningSet>();
        string setsPath = Path.Combine(dataDir, "vocabulary-sets.json");
        if (!File.Exists(setsPath))
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(setsPath, Encoding.UTF8));
            if (!doc.RootElement.TryGetProperty("sets", out var setsArr) || setsArr.ValueKind != JsonValueKind.Array)
            {
                return result;
            }

            foreach (var setEl in setsArr.EnumerateArray())
            {
                string id = setEl.TryGetProperty("id", out var idP) ? (idP.GetString() ?? "") : "";
                int number = setEl.TryGetProperty("number", out var numP) ? numP.GetInt32() : 0;
                int size = setEl.TryGetProperty("size", out var szP) ? szP.GetInt32() : 20;

                var words = new List<VocabularySetWordItem>();
                if (setEl.TryGetProperty("words", out var wordsArr) && wordsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var w in wordsArr.EnumerateArray())
                    {
                        words.Add(new VocabularySetWordItem(
                            CardId: w.TryGetProperty("card_id", out var cid) ? (cid.GetString() ?? "") : "",
                            Word: w.TryGetProperty("word", out var wd) ? (wd.GetString() ?? "") : "",
                            Reading: w.TryGetProperty("reading", out var rd) ? (rd.GetString() ?? "") : "",
                            ChapterId: w.TryGetProperty("chapter_id", out var chid) ? (chid.GetString() ?? "") : "",
                            StudyNumber: w.TryGetProperty("study_number", out var snum) ? snum.GetInt32() : 0
                        ));
                    }
                }

                result.Add(new VocabularyLearningSet(id, number, size, words));
            }
        }
        catch
        {
            // Ignore malformed JSON
        }

        return result;
    }

    private static string FormatChapterPdfTitle(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Replace('-', ' ').ToUpperInvariant();
    }

    private static StudyPackBookOverview EmptyOverview(string bookId)
    {
        return new StudyPackBookOverview(
            BookId: bookId ?? "",
            Available: false,
            Generation: null,
            ActiveManifestPath: null,
            MasterPdfs: [],
            ChapterPdfs: [],
            VocabularySets: [],
            TotalVocabularyInSets: 0,
            HasHtmlCompanion: false,
            HtmlEntryPath: null);
    }
}

