using System.IO.Compression;
using System.Text;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library;

namespace ImmersiveReading.Core.Tests;

/// <summary>
/// Executable verification of all @phase1 Gherkin scenarios in:
/// - specs/features/01-library-ingestion-and-24k-scanning.feature
/// - specs/features/02-library-hierarchy-tags-and-custom-fields.feature
/// </summary>
public sealed class Phase1LibrarySpecsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryDatabase _db;
    private readonly LibraryManager _manager;

    public Phase1LibrarySpecsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ir-phase1-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        string dbPath = Path.Combine(_tempDir, "library.db");
        string outputDir = Path.Combine(FindWorkspaceRoot(), "enrichement-extractor", "output");
        _db = new LibraryDatabase(dbPath, Path.Combine(_tempDir, "covers"));
        _db.InitializeSchema();
        _manager = new LibraryManager(_db, outputDir);
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

    public void Dispose()
    {
        _db.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup locks on Windows
        }
    }

    [Theory]
    [InlineData(@"[かさい まり] こぐまのクーク物語　はじめての海とキャンプ\[かさい まり] こぐまのクーク物語　はじめての海とキャンプ.epub", "book-4e0541b184c4ace9")]
    [InlineData(@"くま クマ 熊 ベアー (PASH! ブックス) - くまなの\くま クマ 熊 ベアー (PASH! ブックス) - くまなの.epub", "book-93a87170fe31025d")]
    public async Task Feature01_BookIdentity_MatchesEnrichmentExtractorParity_And_PreservesSourceEpub(
        string relativeBookPath,
        string expectedBookId)
    {
        string booksRoot = Path.Combine(FindWorkspaceRoot(), "enrichement-extractor", "books");
        string epubPath = Path.Combine(booksRoot, relativeBookPath);
        if (!File.Exists(epubPath))
        {
            return;
        }

        byte[] beforeBytes = await File.ReadAllBytesAsync(epubPath);
        var (bookId, fullSha) = BookIdentity.ComputeFromBytes(beforeBytes);

        Assert.Equal(expectedBookId, bookId);
        Assert.Equal(64, fullSha.Length);

        var imported = await _manager.ImportBookAsync(epubPath);
        Assert.Equal(expectedBookId, imported.Summary.BookId);
        Assert.True(imported.Chapters.Count > 0);

        // Verify deduplication on re-import
        var reimported = await _manager.ImportBookAsync(epubPath);
        Assert.Equal(expectedBookId, reimported.Summary.BookId);
        var allBooks = _manager.ListBooks(new BookListQuery());
        Assert.Equal(1, allBooks.TotalCount);

        // Verify byte-for-byte immutability of source EPUB
        byte[] afterBytes = await File.ReadAllBytesAsync(epubPath);
        Assert.Equal(beforeBytes, afterBytes);
    }

    [Fact]
    public async Task Feature01_OpfExtraction_ExtractsTitle_SortTitle_Authors_Isbn13_And_CollectionSeries()
    {
        string epubPath = CreateSyntheticEpub(
            fileNameOrPath: "honzuki_01.epub",
            opfXml: """
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
                    <dc:title id="t1">本好きの下剋上 第一部「兵士の娘Ⅰ」</dc:title>
                    <meta refines="#t1" property="file-as">ほんずきのげこくじょう 01</meta>
                    <dc:creator id="c1">香月美夜</dc:creator>
                    <meta refines="#c1" property="file-as">かづきみや</meta>
                    <dc:publisher>TOブックス</dc:publisher>
                    <dc:date>2015-01-25</dc:date>
                    <dc:language>ja</dc:language>
                    <dc:identifier id="pub-id">urn:isbn:978-4-86472-342-8</dc:identifier>
                    <dc:description>本が大好きな女子大生が転生した先は、本のない世界だった。</dc:description>
                    <meta property="belongs-to-collection" id="col1">本好きの下剋上</meta>
                    <meta refines="#col1" property="group-position">1</meta>
                  </metadata>
                  <manifest>
                    <item id="ch1" href="text/ch01.xhtml" media-type="application/xhtml+xml"/>
                  </manifest>
                  <spine page-progression-direction="rtl">
                    <itemref idref="ch1"/>
                  </spine>
                </package>
                """);

        var detail = await _manager.ImportBookAsync(epubPath, computeDeepChapterStats: true);

        Assert.Equal("本好きの下剋上 第一部「兵士の娘Ⅰ」", detail.Summary.Title);
        Assert.Equal("ほんずきのげこくじょう 01", detail.Summary.SortTitle);
        Assert.Equal("香月美夜", detail.Summary.Authors);
        Assert.Equal("かづきみや", detail.Summary.AuthorSort);
        Assert.Equal("TOブックス", detail.Summary.Publisher);
        Assert.Equal("2015-01-25", detail.Summary.PublishedDate);
        Assert.Equal("ja", detail.Summary.Language);
        Assert.Equal("9784864723428", detail.Summary.Isbn);
        Assert.Equal("本好きの下剋上", detail.Summary.SeriesName);
        Assert.Equal(1.0, detail.Summary.SeriesIndex);
        Assert.Equal("vertical-rl", detail.Summary.WritingMode);
        Assert.Equal("本好きの下剋上 第一部「兵士の娘Ⅰ」", detail.OpfOriginal.Title);
    }

    [Theory]
    [InlineData("[かさい まり] こぐまのクーク物語　はじめての海とキャンプ.epub", "こぐまのクーク物語 はじめての海とキャンプ", "かさい まり", "", "", null)]
    [InlineData("くま クマ 熊 ベアー (PASH! ブックス) - くまなの.epub", "くま クマ 熊 ベアー", "くまなの", "PASH! ブックス", "", null)]
    [InlineData("[川原 礫] ソードアート・オンライン 第01巻 (電撃文庫).epub", "ソードアート・オンライン 第01巻", "川原 礫", "電撃文庫", "ソードアート・オンライン", 1.0)]
    public void Feature01_FilenameHeuristicFallback_ParsesJapaneseConventions(
        string fileName,
        string expectedTitle,
        string expectedAuthors,
        string expectedImprint,
        string expectedSeries,
        double? expectedVolume)
    {
        var hints = FilenameMetadataParser.Parse(fileName);
        Assert.Equal(expectedTitle.Replace('　', ' '), hints.CleanTitle.Replace('　', ' '));
        Assert.Equal(expectedAuthors, hints.Authors);
        Assert.Equal(expectedImprint, hints.Imprint);
        Assert.Equal(expectedSeries, hints.SeriesName);
        Assert.Equal(expectedVolume, hints.SeriesIndex);
    }

    [Fact]
    public async Task Feature02_NestedFolderHierarchy_And_NumericSeriesVolumeSorting()
    {
        var fJapanese = _manager.SaveFolder(null, null, "Japanese", folderKind: "collection", sortOrder: 1);
        var fShounen = _manager.SaveFolder(null, fJapanese.FolderId, "Shounen", folderKind: "category", sortOrder: 1);
        var fSeinen = _manager.SaveFolder(null, fJapanese.FolderId, "Seinen", folderKind: "category", sortOrder: 2);
        var fMonster = _manager.SaveFolder(null, fSeinen.FolderId, "Monster", folderKind: "series", sortOrder: 1);

        // Import 4 volumes in scrambled order (10, 1, 2, 2.5) and 1 book in Shounen
        var b10 = await _manager.ImportBookAsync(CreateMinimalEpub("monster10.epub", "Monster Vol 10"), fMonster.FolderId, "Monster", 10.0);
        var b1 = await _manager.ImportBookAsync(CreateMinimalEpub("monster01.epub", "Monster Vol 1"), fMonster.FolderId, "Monster", 1.0);
        var b2 = await _manager.ImportBookAsync(CreateMinimalEpub("monster02.epub", "Monster Vol 2"), fMonster.FolderId, "Monster", 2.0);
        var b25 = await _manager.ImportBookAsync(CreateMinimalEpub("monster02_5.epub", "Monster Vol 2.5"), fMonster.FolderId, "Monster", 2.5);
        var bShounen = await _manager.ImportBookAsync(CreateMinimalEpub("naruto01.epub", "Naruto Vol 1"), fShounen.FolderId, "Naruto", 1.0);

        // 1. Verify numeric volume order inside "Monster" (1.0, 2.0, 2.5, 10.0 - NOT ASCII 1, 10, 2)
        var monsterBooks = _manager.ListBooks(new BookListQuery(FolderId: fMonster.FolderId, Sort: "series_or_title"));
        Assert.Equal(4, monsterBooks.TotalCount);
        Assert.Equal([b1.Summary.BookId, b2.Summary.BookId, b25.Summary.BookId, b10.Summary.BookId],
            monsterBooks.Items.Select(i => i.BookId).ToArray());
        Assert.Equal("Japanese / Seinen / Monster", monsterBooks.Items[0].FolderPathBreadcrumb);

        // 2. Verify recursive descendant query on root "Japanese" folder returns all 5 books
        var allJapanese = _manager.ListBooks(new BookListQuery(FolderId: fJapanese.FolderId, IncludeDescendants: true));
        Assert.Equal(5, allJapanese.TotalCount);

        // 3. Verify folder tree subtree counts
        var tree = _manager.GetFolderTree();
        Assert.Single(tree);
        Assert.Equal(5, tree[0].TotalSubtreeBookCount);
    }

    [Fact]
    public async Task Feature02_EditableMetadata_BulkEdit_And_ResetToOriginalOpf()
    {
        string epubPath = CreateMinimalEpub("messy.epub", "[かさい まり] こぐまのクーク物語");
        var imported = await _manager.ImportBookAsync(epubPath);

        // Single-book metadata fix including ISBN
        var updated = _manager.UpdateBookMetadata(new BookMetadataUpdateRequest(
            BookId: imported.Summary.BookId,
            Title: "こぐまのクーク物語 はじめての海とキャンプ",
            Authors: "かさい まり",
            Isbn: "9784041026229",
            Publisher: "角川書店"));

        Assert.True(updated.Summary.IsMetadataEdited);
        Assert.Equal("こぐまのクーク物語 はじめての海とキャンプ", updated.Summary.Title);
        Assert.Equal("9784041026229", updated.Summary.Isbn);
        Assert.Equal("角川書店", updated.Summary.Publisher);

        // FTS5 search by edited ISBN finds the book
        var searchByIsbn = _manager.ListBooks(new BookListQuery(Search: "9784041026229"));
        Assert.Single(searchByIsbn.Items);
        Assert.Equal(imported.Summary.BookId, searchByIsbn.Items[0].BookId);

        // Reset to original OPF metadata
        var reset = _manager.ResetBookMetadataToOpf(imported.Summary.BookId);
        Assert.False(reset.Summary.IsMetadataEdited);
        Assert.Equal("[かさい まり] こぐまのクーク物語", reset.Summary.Title);
        Assert.Equal("", reset.Summary.Isbn);

        // Bulk edit + auto-numbering volumes across 3 books
        var v1 = await _manager.ImportBookAsync(CreateMinimalEpub("s1.epub", "Book A"));
        var v2 = await _manager.ImportBookAsync(CreateMinimalEpub("s2.epub", "Book B"));
        var v3 = await _manager.ImportBookAsync(CreateMinimalEpub("s3.epub", "Book C"));
        var folder = _manager.SaveFolder(null, null, "Seinen");

        int count = _manager.BulkUpdateBooks(new BulkBookUpdatePatch(
            BookIds: [v1.Summary.BookId, v2.Summary.BookId, v3.Summary.BookId],
            FolderId: folder.FolderId,
            SeriesName: "三日間の幸福",
            AutoNumberVolumes: true,
            AutoNumberStart: 1.0,
            Authors: "三秋縋"));

        Assert.Equal(3, count);
        var seriesBooks = _manager.ListBooks(new BookListQuery(FolderId: folder.FolderId, Sort: "series_or_title"));
        Assert.Equal([1.0, 2.0, 3.0], seriesBooks.Items.Select(x => x.SeriesIndex ?? 0).ToArray());
        Assert.All(seriesBooks.Items, b => Assert.Equal("三日間の幸福", b.SeriesName));
    }

    [Fact]
    public async Task Feature02_ManualTags_And_MultiLevelCustomFieldsWithInheritance()
    {
        // 1. Manual Tags (AND / OR filtering)
        var tN2 = _manager.SaveTag(null, "#n2-target", "#2563eb");
        var tMystery = _manager.SaveTag(null, "#mystery", "#7c3aed");
        var tSlice = _manager.SaveTag(null, "#slice-of-life", "#10b981");

        // 2. Folder Hierarchy with Series-level jiten.moe URL
        var fJa = _manager.SaveFolder(null, null, "Japanese", "collection");
        var fSeries = _manager.SaveFolder(
            folderId: null,
            parentFolderId: fJa.FolderId,
            name: "くま クマ 熊 ベアー",
            folderKind: "series",
            customFields: [new CustomFieldValueInput("jiten_moe_url", "https://jiten.moe/decks/media/1024")]);

        var vol1 = await _manager.ImportBookAsync(
            CreateMinimalEpub("kuma1.epub", "くま クマ 熊 ベアー 1"),
            folderId: fSeries.FolderId,
            seriesNameOverride: "くま クマ 熊 ベアー",
            seriesIndexOverride: 1.0,
            tagIds: [tN2.TagId, tMystery.TagId],
            customFields: [new CustomFieldValueInput("jiten_difficulty", "3.5", 3.5)]);

        var vol2 = await _manager.ImportBookAsync(
            CreateMinimalEpub("kuma2.epub", "くま クマ 熊 ベアー 2"),
            folderId: fSeries.FolderId,
            seriesNameOverride: "くま クマ 熊 ベアー",
            seriesIndexOverride: 2.0,
            tagIds: [tN2.TagId, tSlice.TagId],
            customFields:
            [
                new CustomFieldValueInput("jiten_moe_url", "https://jiten.moe/decks/media/1024/subdeck/2"),
                new CustomFieldValueInput("jiten_difficulty", "7.2", 7.2)
            ]);

        // Verify Vol 1 inherits jiten_moe_url from the Series folder "くま クマ 熊 ベアー"
        var d1 = _manager.GetBookDetail(vol1.Summary.BookId)!;
        var jiten1 = d1.Summary.CustomFields["jiten_moe_url"];
        Assert.Equal("https://jiten.moe/decks/media/1024", jiten1.EffectiveValueText);
        Assert.True(jiten1.IsInherited);
        Assert.Equal("くま クマ 熊 ベアー", jiten1.SourceEntityName);

        // Verify Vol 2 overrides jiten_moe_url at the book level
        var d2 = _manager.GetBookDetail(vol2.Summary.BookId)!;
        var jiten2 = d2.Summary.CustomFields["jiten_moe_url"];
        Assert.Equal("https://jiten.moe/decks/media/1024/subdeck/2", jiten2.EffectiveValueText);
        Assert.False(jiten2.IsInherited);

        // Verify AND vs OR multi-tag filtering
        var andFilter = _manager.ListBooks(new BookListQuery(TagIds: [tN2.TagId, tMystery.TagId], TagMatchMode: "AND"));
        Assert.Single(andFilter.Items);
        Assert.Equal(vol1.Summary.BookId, andFilter.Items[0].BookId);

        var orFilter = _manager.ListBooks(new BookListQuery(TagIds: [tMystery.TagId, tSlice.TagId], TagMatchMode: "OR"));
        Assert.Equal(2, orFilter.TotalCount);

        // Verify numeric custom field filtering (jiten_difficulty <= 4.0)
        var easyBooks = _manager.ListBooks(new BookListQuery(
            CustomFieldFilters: [new CustomFieldFilter("jiten_difficulty", "lte", ValueNumber: 4.0)]));
        Assert.Single(easyBooks.Items);
        Assert.Equal(vol1.Summary.BookId, easyBooks.Items[0].BookId);
    }

    [Fact]
    public async Task Feature01_TwoStageBatchDirectoryScanner_IndexesMultipleBooksInParallel()
    {
        string scanRoot = Path.Combine(_tempDir, "batch-scan");
        Directory.CreateDirectory(scanRoot);

        for (int i = 1; i <= 12; i++)
        {
            CreateMinimalEpub(
                Path.Combine(scanRoot, $"[著者A] テストシリーズ 第{i:D2}巻 (電撃文庫).epub"),
                $"[著者A] テストシリーズ 第{i:D2}巻 (電撃文庫)");
        }

        var result = await _manager.ScanDirectoryAsync(scanRoot, recursive: true);
        Assert.Equal(12, result.TotalFilesDiscovered);
        Assert.Equal(12, result.ImportedNew);
        Assert.Equal(0, result.FailedFiles);

        var listed = _manager.ListBooks(new BookListQuery(Sort: "series_or_title", Limit: 50));
        Assert.Equal(12, listed.TotalCount);
        Assert.Equal(1.0, listed.Items[0].SeriesIndex);
        Assert.Equal(12.0, listed.Items[11].SeriesIndex);
        Assert.Equal("著者A", listed.Items[0].Authors);
        Assert.Equal("電撃文庫", listed.Items[0].Imprint);
    }

    [Fact]
    public async Task Feature01_EpubParsing_ToCDrivenChapters_And_EarlyPagesIntroductionClassification()
    {
        string booksRoot = Path.Combine(FindWorkspaceRoot(), "enrichement-extractor", "books");
        string kumaEpub = Path.Combine(booksRoot, @"くま クマ 熊 ベアー (PASH! ブックス) - くまなの\くま クマ 熊 ベアー (PASH! ブックス) - くまなの.epub");
        string kuukuEpub = Path.Combine(booksRoot, @"[かさい まり] こぐまのクーク物語　はじめての海とキャンプ\[かさい まり] こぐまのクーク物語　はじめての海とキャンプ.epub");

        if (File.Exists(kumaEpub))
        {
            var imported = await _manager.ImportBookAsync(kumaEpub);
            var chapters = imported.Chapters;

            // Early parts before Chapter 1 must be marked as front matter (is_excluded = true)
            var frontMatter = chapters.TakeWhile(c => c.IsExcluded).ToList();
            Assert.True(frontMatter.Count >= 4, $"Expected at least 4 front matter pages, got {frontMatter.Count}");
            Assert.Contains(frontMatter, c => c.Title.Contains("Cover") || c.Title.Contains("表紙"));
            Assert.Contains(frontMatter, c => c.Title.Contains("Contents") || c.Title.Contains("目次") || c.Title.Contains("Ｃｏｎｔｅｎｔｓ"));

            // First actual story chapter must be Chapter 1 ("１ クマさん装備ゲットだぜ！"), not Chapter 6!
            var firstStoryChapter = chapters.FirstOrDefault(c => !c.IsExcluded);
            Assert.NotNull(firstStoryChapter);
            Assert.Equal("chapter-01", firstStoryChapter.ChapterId);
            Assert.Contains("１ クマさん装備ゲットだぜ！", firstStoryChapter.Title);
            Assert.Equal("text/part0004.html", firstStoryChapter.SpineHref);

            // Second actual story chapter must be Chapter 2 ("２ クマさん、少女と出会う")
            var secondStoryChapter = chapters.FirstOrDefault(c => c.ChapterId == "chapter-02");
            Assert.NotNull(secondStoryChapter);
            Assert.Contains("２ クマさん、少女と出会う", secondStoryChapter.Title);
            Assert.Equal("text/part0005.html", secondStoryChapter.SpineHref);

            // Third actual story chapter must be Chapter 3 ("３ クマさん、ウルフを買い取りしてもらう")
            var thirdStoryChapter = chapters.FirstOrDefault(c => c.ChapterId == "chapter-03");
            Assert.NotNull(thirdStoryChapter);
            Assert.Contains("３ クマさん、ウルフを買い取りしてもらう", thirdStoryChapter.Title);
            Assert.Equal("text/part0008.html", thirdStoryChapter.SpineHref);
        }

        if (File.Exists(kuukuEpub))
        {
            var imported = await _manager.ImportBookAsync(kuukuEpub);
            var chapters = imported.Chapters;

            // Early parts before Chapter 1 must be marked as front matter
            var frontMatter = chapters.TakeWhile(c => c.IsExcluded).ToList();
            Assert.True(frontMatter.Count >= 5, $"Expected at least 5 front matter items, got {frontMatter.Count}");

            // First story chapter must be Chapter 1 ("海に行くよ"), not Chapter 8 or 9!
            var firstStoryChapter = chapters.FirstOrDefault(c => !c.IsExcluded);
            Assert.NotNull(firstStoryChapter);
            Assert.Equal("chapter-01", firstStoryChapter.ChapterId);
            Assert.Contains("海に行くよ", firstStoryChapter.Title);
            Assert.EndsWith("Text/part0007.xhtml", firstStoryChapter.SpineHref, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Feature06_ClearLibrary_DeletesBooks_And_Cascades()
    {
        // 1. Setup library with books and folders
        var fld = _manager.SaveFolder(null, null, "Fiction");
        var b1 = await _manager.ImportBookAsync(CreateMinimalEpub("clear1.epub", "Clearable Book 1"), folderId: fld.FolderId);
        var b2 = await _manager.ImportBookAsync(CreateMinimalEpub("clear2.epub", "Clearable Book 2"), folderId: fld.FolderId);

        var listBefore = _manager.ListBooks(new BookListQuery());
        Assert.True(listBefore.TotalCount >= 2);

        // 2. Clear library (with folders)
        var result = _manager.ClearLibrary(new ClearLibraryOptions(RemoveFolders: true, ResetVocabulary: false));

        Assert.True(result.BooksDeleted >= 2);
        Assert.True(result.FoldersDeleted >= 1);

        // 3. Verify library is empty
        var listAfter = _manager.ListBooks(new BookListQuery());
        Assert.Equal(0, listAfter.TotalCount);
        Assert.Empty(_manager.GetFolderTree());

        // 4. Verify FTS search returns 0
        var ftsSearch = _manager.ListBooks(new BookListQuery(Search: "Clearable"));
        Assert.Equal(0, ftsSearch.TotalCount);
    }

    private string CreateMinimalEpub(string fileNameOrPath, string dcTitle)
    {
        return CreateSyntheticEpub(
            fileNameOrPath,
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:title>{System.Security.SecurityElement.Escape(dcTitle)}</dc:title>
                <dc:language>ja</dc:language>
                <dc:identifier id="id">urn:uuid:{Guid.NewGuid()}</dc:identifier>
              </metadata>
              <manifest>
                <item id="ch1" href="text/ch01.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine>
                <itemref idref="ch1"/>
              </spine>
            </package>
            """);
    }

    private string CreateSyntheticEpub(string fileNameOrPath, string opfXml)
    {
        string fullPath = Path.IsPathRooted(fileNameOrPath)
            ? fileNameOrPath
            : Path.Combine(_tempDir, fileNameOrPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        WriteZipEntry(zip, "mimetype", "application/epub+zip");
        WriteZipEntry(zip, "META-INF/container.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);
        WriteZipEntry(zip, "OEBPS/content.opf", opfXml);
        WriteZipEntry(zip, "OEBPS/text/ch01.xhtml", """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml">
              <head><title>第1章</title></head>
              <body><p>これはテストの段落です。</p></body>
            </html>
            """);

        return fullPath;
    }

    private static void WriteZipEntry(ZipArchive zip, string entryPath, string content)
    {
        var entry = zip.CreateEntry(entryPath, CompressionLevel.Fastest);
        using var s = entry.Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }
}
