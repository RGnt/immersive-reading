using System.IO.Compression;
using System.Text;
using ImmersiveReading.Core.Epub;
using Xunit;

namespace ImmersiveReading.Core.Tests.Epub;

public sealed class EpubMetadataExtractorTests
{
    private const string SampleOpf = """
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
          <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
            <dc:title id="t1">テスト冒険記 第1巻</dc:title>
            <meta refines="#t1" property="file-as">テストボウケンキ 01</meta>
            <dc:creator id="c1">山田太郎</dc:creator>
            <meta refines="#c1" property="file-as">ヤマダタロウ</meta>
            <dc:publisher>テスト出版</dc:publisher>
            <dc:date>2024-05-01</dc:date>
            <dc:language>ja</dc:language>
            <dc:identifier id="pub-id">urn:isbn:978-4-04-867760-8</dc:identifier>
            <dc:description>冒険の始まり。</dc:description>
            <meta property="belongs-to-collection" id="col1">テスト冒険記</meta>
            <meta refines="#col1" property="group-position">1</meta>
          </metadata>
          <manifest>
            <item id="cover-img" href="images/cover.jpg" media-type="image/jpeg" properties="cover-image"/>
            <item id="cover-page" href="text/cover.xhtml" media-type="application/xhtml+xml"/>
            <item id="toc" href="text/toc.xhtml" media-type="application/xhtml+xml" properties="nav"/>
            <item id="ch1" href="text/ch01.xhtml" media-type="application/xhtml+xml"/>
            <item id="ch2" href="text/ch02.xhtml" media-type="application/xhtml+xml"/>
            <item id="afterword" href="text/afterword.xhtml" media-type="application/xhtml+xml"/>
          </manifest>
          <spine page-progression-direction="rtl">
            <itemref idref="cover-page"/>
            <itemref idref="toc"/>
            <itemref idref="ch1"/>
            <itemref idref="ch2"/>
            <itemref idref="afterword"/>
          </spine>
        </package>
        """;

    [Fact]
    public void ExtractFromStream_ParsesMetadataAndChaptersCorrectly()
    {
        byte[] dummyJpg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];
        var epubBytes = new TestEpubBuilder()
            .WithOpf(SampleOpf)
            .AddEntry("OEBPS/images/cover.jpg", dummyJpg)
            .WithChapter("text/cover.xhtml", "表紙", "<p>表紙画像</p>")
            .WithChapter("text/toc.xhtml", "目次", """
                <nav xmlns:epub="http://www.idpf.org/2007/ops" epub:type="toc">
                  <ol>
                    <li><a href="cover.xhtml">表紙</a></li>
                    <li><a href="toc.xhtml">目次</a></li>
                    <li><a href="ch01.xhtml">第1話 旅立ち</a></li>
                    <li><a href="ch02.xhtml">第2話 出会い</a></li>
                    <li><a href="afterword.xhtml">あとがき</a></li>
                  </ol>
                </nav>
                """)
            .WithChapter("text/ch01.xhtml", "第1話 旅立ち", "<p>むかしむかしあるところに、心優しい少年が住んでいました。少年は毎日森へ出かけては木の実を拾ったり動物たちと語り合ったりして、平和な日々を過ごしていました。しかしある日、運命の歯車が静かに動き出したのです。</p>")
            .WithChapter("text/ch02.xhtml", "第2話 出会い", "<p>森の中で不思議な輝きを放つ謎の少女に出会った。彼女の瞳は青空のように澄んでいた。</p>")
            .WithChapter("text/afterword.xhtml", "あとがき", "<p>読んでくれてありがとう。</p>")
            .Build();

        using var ms = new MemoryStream(epubBytes);
        var pkg = EpubMetadataExtractor.ExtractFromStream(ms, "test.epub", computeDeepChapterStats: true);

        Assert.Equal("テスト冒険記 第1巻", pkg.Title);
        Assert.Equal("テストボウケンキ 01", pkg.SortTitle);
        Assert.Equal("山田太郎", pkg.Authors);
        Assert.Equal("ヤマダタロウ", pkg.AuthorSort);
        Assert.Equal("テスト出版", pkg.Publisher);
        Assert.Equal("2024-05-01", pkg.PublishedDate);
        Assert.Equal("ja", pkg.Language);
        Assert.Equal("9784048677608", pkg.Isbn);
        Assert.Equal("テスト冒険記", pkg.SeriesName);
        Assert.Equal(1.0, pkg.SeriesIndex);
        Assert.Equal("vertical-rl", pkg.WritingMode);
        Assert.True(pkg.StatsComputed);
        Assert.NotNull(pkg.CoverImageBytes);
        Assert.Equal(".jpg", pkg.CoverExtension);

        // Verify chapter exclusion / story detection
        // Cover, toc, and afterword should be flagged as non-story (IsExcluded = true)
        Assert.Equal(5, pkg.Chapters.Count);
        Assert.True(pkg.Chapters[0].IsExcluded);  // 表紙
        Assert.True(pkg.Chapters[1].IsExcluded);  // 目次
        Assert.False(pkg.Chapters[2].IsExcluded); // 第1話
        Assert.False(pkg.Chapters[3].IsExcluded); // 第2話
        Assert.True(pkg.Chapters[4].IsExcluded);  // あとがき
    }

    [Fact]
    public void ExtractFromStream_FallsBackToFilename_WhenOpfFieldsMissing()
    {
        string emptyOpf = """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="pub-id">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                <dc:identifier id="pub-id">test-id</dc:identifier>
              </metadata>
              <manifest>
                <item id="ch1" href="text/ch1.xhtml" media-type="application/xhtml+xml"/>
              </manifest>
              <spine>
                <itemref idref="ch1"/>
              </spine>
            </package>
            """;

        var epubBytes = new TestEpubBuilder()
            .WithOpf(emptyOpf)
            .WithChapter("text/ch1.xhtml", "Chapter 1", "<p>Hello world</p>")
            .Build();

        using var ms = new MemoryStream(epubBytes);
        var pkg = EpubMetadataExtractor.ExtractFromStream(
            ms,
            "[川原 礫] ソードアート・オンライン 第01巻 (電撃文庫).epub",
            computeDeepChapterStats: false);

        Assert.Equal("ソードアート・オンライン 第01巻", pkg.Title);
        Assert.Equal("川原 礫", pkg.Authors);
        Assert.Equal("電撃文庫", pkg.Imprint);
        Assert.Equal("ソードアート・オンライン", pkg.SeriesName);
        Assert.Equal(1.0, pkg.SeriesIndex);
        Assert.False(pkg.StatsComputed);
    }

    [Fact]
    public async Task ExtractFromFileAsync_MatchesExtractFromStream()
    {
        var epubBytes = new TestEpubBuilder()
            .WithOpf(SampleOpf)
            .WithChapter("text/ch1.xhtml", "第1話", "<p>テスト</p>")
            .Build();

        string tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, epubBytes);

            using var ms = new MemoryStream(epubBytes);
            var fromStream = EpubMetadataExtractor.ExtractFromStream(ms, Path.GetFileName(tempFile));
            var fromFile = await EpubMetadataExtractor.ExtractFromFileAsync(tempFile);

            Assert.Equal(fromStream.BookId, fromFile.BookId);
            Assert.Equal(fromStream.EpubSha256, fromFile.EpubSha256);
            Assert.Equal(fromStream.Title, fromFile.Title);
            Assert.Equal(fromStream.Authors, fromFile.Authors);
            Assert.Equal(fromStream.Chapters.Count, fromFile.Chapters.Count);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
