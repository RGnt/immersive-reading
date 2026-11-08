using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Library;
using Xunit;

namespace ImmersiveReading.Core.Tests;

public sealed class Phase4StudyPackSpecsTests : IDisposable
{
    private readonly string _tempDir;

    public Phase4StudyPackSpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-phase4-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void PythonStudyPackBridge_DiscoversRealExtractorStudyPack_MasterPdfs_ChapterPdfs_And_20WordSets()
    {
        string workspaceRoot = FindWorkspaceRoot();
        string extractorOutput = Path.Combine(workspaceRoot, "enrichement-extractor", "output");
        string extractorDir = Path.Combine(workspaceRoot, "enrichement-extractor");

        var bridge = new PythonStudyPackBridge(extractorOutput, extractorDir);
        var overview = bridge.GetBookStudyPack("book-4e0541b184c4ace9");

        Assert.True(overview.Available);
        Assert.Contains(overview.MasterPdfs, p => p.FileName == "kanji-master.pdf");
        Assert.Contains(overview.MasterPdfs, p => p.FileName == "vocabulary-master.pdf");
        Assert.Contains(overview.MasterPdfs, p => p.FileName == "grammar-master.pdf");
        Assert.NotEmpty(overview.ChapterPdfs);
        Assert.NotEmpty(overview.VocabularySets);

        var firstSet = overview.VocabularySets[0];
        Assert.Equal(1, firstSet.Number);
        Assert.Equal(20, firstSet.Size);
        Assert.Equal(20, firstSet.Words.Count);
        Assert.All(firstSet.Words, w => Assert.False(string.IsNullOrWhiteSpace(w.Word)));
    }

    [Fact]
    public void PythonStudyPackBridge_ReadsActivePublication_And_AcquiresByteRangeSnapshotLease()
    {
        string pubDir = Path.Combine(_tempDir, ".publication");
        string gen = "0123456789abcdef0123456789abcdef"; // 32-char lowercase hex matching retention.py
        string relRoot = Path.Combine(pubDir, "releases", gen);
        string bookId = "book-0123456789abcdef";
        string studySnapRoot = Path.Combine(relRoot, "study", bookId);

        Directory.CreateDirectory(pubDir);
        Directory.CreateDirectory(Path.Combine(studySnapRoot, "pdf", bookId));
        Directory.CreateDirectory(Path.Combine(studySnapRoot, "data", bookId));

        File.WriteAllBytes(Path.Combine(studySnapRoot, "pdf", bookId, "kanji-master.pdf"), [0x25, 0x50, 0x44, 0x46]);
        File.WriteAllText(
            Path.Combine(studySnapRoot, "data", bookId, "vocabulary-sets.json"),
            """
            {
              "set_size": 20,
              "sets": [
                {
                  "id": "set-001",
                  "number": 1,
                  "size": 1,
                  "words": [
                    { "card_id": "vocab-0001", "word": "冒険", "reading": "ぼうけん", "chapter_id": "chapter-01", "study_number": 1 }
                  ]
                }
              ]
            }
            """);

        string manifestPath = Path.Combine(relRoot, "manifest.json");
        string manifestJson = $$"""
            {
              "schema_version": "1.0",
              "generation": "{{gen}}",
              "books": {
                "{{bookId}}": {
                  "study": {
                    "root": ".publication/releases/{{gen}}/study/{{bookId}}"
                  }
                }
              }
            }
            """;
        File.WriteAllText(manifestPath, manifestJson, Encoding.UTF8);
        string manifestSha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(manifestPath)));

        File.WriteAllText(
            Path.Combine(pubDir, "active.json"),
            $$"""
            {
              "schema_version": "1.0",
              "generation": "{{gen}}",
              "manifest": ".publication/releases/{{gen}}/manifest.json",
              "sha256": "{{manifestSha}}"
            }
            """,
            Encoding.UTF8);

        var bridge = new PythonStudyPackBridge(_tempDir);
        var (activeGen, activeManifestPath, activeDoc) = bridge.TryReadActivePublication();
        using (activeDoc)
        {
            Assert.Equal(gen, activeGen);
            Assert.NotNull(activeManifestPath);
        }

        string leaseFilePath;
        using (var lease = bridge.AcquireSnapshotLease(gen))
        {
            Assert.NotNull(lease);
            leaseFilePath = lease!.LockFilePath;
            Assert.True(File.Exists(leaseFilePath));

        // Verify byte 0 is locked at OS level on supported platforms
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
#pragma warning disable CA1416
            using var probeStream = new FileStream(leaseFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Assert.Throws<IOException>(() => probeStream.Lock(0, 1));
#pragma warning restore CA1416
        }

            var pack = bridge.GetBookStudyPack(bookId);
            Assert.True(pack.Available);
            Assert.Equal(gen, pack.Generation);
            Assert.Single(pack.MasterPdfs);
            Assert.Single(pack.VocabularySets);
            Assert.Equal("冒険", pack.VocabularySets[0].Words[0].Word);
        }

        // After disposing lease, lock file is cleaned up
        Assert.False(File.Exists(leaseFilePath));
    }

    [Fact]
    public void FlashcardMiningService_AtomicallyWritesLibraryJson_And_AnkiTsv_WithoutSentenceTranslation()
    {
        var service = new FlashcardMiningService(_tempDir);

        var res1 = service.MineCard(new MineFlashcardRequest(
            BookId: "book-4e0541b184c4ace9",
            BookTitle: "こぐまのクーク物語",
            ChapterHref: "xhtml/p-001.xhtml",
            ChapterNumber: 1,
            ChapterTitle: "Chapter 01",
            Expression: "キャンプ",
            Reading: "きゃんぷ",
            Meaning: "camp; camping",
            SentenceText: "はじめての海とキャンプに出かけました。"));

        Assert.StartsWith("v-", res1.CardId);
        Assert.Equal("キャンプ", res1.Card.Prompt);
        Assert.Equal("camp; camping", res1.Card.Meaning);
        // Hard Invariant #2: Story sentence is stored strictly untranslated in Japanese
        Assert.Equal("はじめての海とキャンプに出かけました。", res1.Card.Example);

        // Mining the same word in the same chapter updates in-place rather than duplicating
        var res1Updated = service.MineCard(new MineFlashcardRequest(
            BookId: "book-4e0541b184c4ace9",
            BookTitle: "こぐまのクーク物語",
            ChapterHref: "xhtml/p-001.xhtml",
            ChapterNumber: 1,
            ChapterTitle: "Chapter 01",
            Expression: "キャンプ",
            Reading: "きゃんぷ",
            Meaning: "camping trip; outdoor camp",
            SentenceText: "はじめての海とキャンプに出かけました。"));

        Assert.Equal(res1.CardId, res1Updated.CardId);
        Assert.Equal("camping trip; outdoor camp", res1Updated.Card.Meaning);

        var cards = service.ListBookCards("book-4e0541b184c4ace9");
        Assert.Single(cards);

        Assert.True(File.Exists(service.LibraryJsonPath));
        Assert.True(File.Exists(service.AnkiTsvPath));

        using var doc = JsonDocument.Parse(File.ReadAllText(service.LibraryJsonPath));
        Assert.Equal("1.0", doc.RootElement.GetProperty("schema_version").GetString());

        string tsvContent = File.ReadAllText(service.AnkiTsvPath);
        Assert.Contains("キャンプ\tきゃんぷ\tcamping trip; outdoor camp\tはじめての海とキャンプに出かけました。", tsvContent);
    }

    [Fact]
    public async Task PythonStudyPackBridge_EnsureRequirements_Detects_Existing_Packages_Without_Calling_Pip()
    {
        string workspaceRoot = FindWorkspaceRoot();
        string pythonExe = Path.Combine(workspaceRoot, "enrichement-extractor", ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe))
        {
            pythonExe = Path.Combine(workspaceRoot, "enrichement-extractor", ".venv", "bin", "python");
        }

        if (!File.Exists(pythonExe))
        {
            return; // Skip if no venv present in test environment
        }

        string sitePackagesDir = Path.Combine(_tempDir, "test-python-packages");
        var requirements = new List<string> { "sudachipy>=0.6.8", "sudachidict_core>=20240109" };

        var bridge = new PythonStudyPackBridge(_tempDir, Path.Combine(workspaceRoot, "enrichement-extractor"), configuredPythonExe: pythonExe);

        // This call should succeed because sudachipy and sudachidict_core are already installed in the venv,
        // even though the venv does NOT have 'pip' installed!
        var method = typeof(PythonStudyPackBridge).GetMethod("EnsurePipRequirementsAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        var task = (Task)method.Invoke(null, [pythonExe, sitePackagesDir, requirements, null, CancellationToken.None])!;
        await task;

        // Verify stamp file written
        string stampFile = Path.Combine(sitePackagesDir, ".requirements.sha256");
        Assert.True(File.Exists(stampFile));
    }

    private static string FindWorkspaceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "enrichement-extractor")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate workspace root containing enrichement-extractor.");
    }
}
