using System.IO.Compression;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Core.Epub;

public sealed record ExtractedEpubPackage(
    string BookId,
    string EpubSha256,
    long FileSizeBytes,
    string Title,
    string SortTitle,
    string Authors,
    string AuthorSort,
    string Isbn,
    string Publisher,
    string Imprint,
    string PublishedDate,
    string Language,
    string Description,
    string SeriesName,
    double? SeriesIndex,
    string WritingMode,
    OpfOriginalMetadata OpfOriginal,
    byte[]? CoverImageBytes,
    string? CoverExtension,
    IReadOnlyList<BookChapterRecord> Chapters,
    int TotalCharCount,
    bool StatsComputed
);

public static class EpubMetadataExtractor
{
    public static async Task<ExtractedEpubPackage> ExtractFromFileAsync(
        string epubFilePath,
        bool computeDeepChapterStats = false,
        CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(epubFilePath);
        var (bookId, sha256) = await BookIdentity.ComputeFromFileAsync(epubFilePath, ct).ConfigureAwait(false);

        await using var fs = new FileStream(
            epubFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 32768,
            useAsync: true);

        return ExtractFromStream(
            fs,
            Path.GetFileName(epubFilePath),
            bookId,
            sha256,
            fileInfo.Length,
            computeDeepChapterStats);
    }

    public static ExtractedEpubPackage ExtractFromStream(
        Stream epubStream,
        string fileName,
        string? precomputedBookId = null,
        string? precomputedSha256 = null,
        long fileSizeBytes = 0,
        bool computeDeepChapterStats = false)
    {
        string bookId;
        string sha256;
        if (precomputedBookId is not null && precomputedSha256 is not null)
        {
            bookId = precomputedBookId;
            sha256 = precomputedSha256;
        }
        else
        {
            (bookId, sha256) = BookIdentity.ComputeFromStream(epubStream);
        }

        if (fileSizeBytes <= 0 && epubStream.CanSeek)
        {
            fileSizeBytes = epubStream.Length;
        }

        using var archive = new ZipArchive(epubStream, ZipArchiveMode.Read, leaveOpen: true);

        // 1. Locate OPF path from META-INF/container.xml
        string opfPath = EpubArchiveReader.FindOpfPath(archive) ?? EpubArchiveReader.FindFirstOpfEntry(archive)
            ?? throw new InvalidDataException($"No OPF package found in EPUB '{fileName}'.");

        var opfEntry = EpubPaths.GetEntryNormalized(archive, opfPath)
            ?? throw new InvalidDataException($"OPF entry '{opfPath}' not found in EPUB '{fileName}'.");

        string opfDir = EpubPaths.GetPosixDirectory(opfPath);
        string opfXml = EpubArchiveReader.ReadEntryString(opfEntry);
        var opfDoc = EpubArchiveReader.ParseXmlOrHtml(opfXml);

        // 2. Extract raw OPF metadata (using XDocument for full XML <meta> text content fidelity)
        var rawOpf = OpfMetadataParser.ParseOpfMetadataXml(opfXml, opfDoc);

        // 3. Apply filename & title heuristics when OPF fields are missing or bracket-encoded
        var fileHints = FilenameMetadataParser.Parse(fileName);
        var titleHints = FilenameMetadataParser.Parse(rawOpf.Title);
        var effective = MetadataMerger.Merge(rawOpf, fileHints, titleHints, fileName);

        // 4. Parse manifest & spine
        var manifest = OpfManifestParser.ParseManifest(opfDoc, opfDir);
        var (spineHrefs, writingMode) = OpfManifestParser.ParseSpine(opfDoc, manifest);
        if (rawOpf.WritingMode != "auto")
        {
            writingMode = rawOpf.WritingMode;
        }

        // 5. Extract Cover Image
        var (coverBytes, coverExt) = EpubCoverExtractor.ExtractCoverImage(archive, opfDoc, manifest);

        // 6. Build Chapter list using Table of Contents and front-matter classification
        var (chapters, totalChars) = ChapterListBuilder.BuildChapterList(
            archive,
            opfDoc,
            manifest,
            spineHrefs,
            bookId,
            computeDeepChapterStats);

        var opfSnapshot = rawOpf with
        {
            Title = !string.IsNullOrWhiteSpace(rawOpf.Title) ? rawOpf.Title : effective.Title,
            WritingMode = writingMode
        };

        return new ExtractedEpubPackage(
            BookId: bookId,
            EpubSha256: sha256,
            FileSizeBytes: fileSizeBytes,
            Title: effective.Title,
            SortTitle: effective.SortTitle,
            Authors: effective.Authors,
            AuthorSort: effective.AuthorSort,
            Isbn: effective.Isbn,
            Publisher: rawOpf.Publisher,
            Imprint: effective.Imprint,
            PublishedDate: rawOpf.PublishedDate,
            Language: rawOpf.Language,
            Description: rawOpf.Description,
            SeriesName: effective.SeriesName,
            SeriesIndex: effective.SeriesIndex,
            WritingMode: writingMode,
            OpfOriginal: opfSnapshot,
            CoverImageBytes: coverBytes,
            CoverExtension: coverExt,
            Chapters: chapters,
            TotalCharCount: totalChars,
            StatsComputed: computeDeepChapterStats);
    }

    // Public helper delegations for backwards compatibility
    public static ZipArchiveEntry? GetEntryNormalized(ZipArchive archive, string posixPath)
        => EpubPaths.GetEntryNormalized(archive, posixPath);

    public static string NormalizePosixPath(string path)
        => EpubPaths.NormalizePosixPath(path);

    public static string GetPosixDirectory(string posixPath)
        => EpubPaths.GetPosixDirectory(posixPath);

    public static string ResolvePosixPath(string baseDir, string relativeHref)
        => EpubPaths.ResolvePosixPath(baseDir, relativeHref);

    public static string NormalizeLanguageTag(string rawLanguage)
        => EpubLanguage.NormalizeLanguageTag(rawLanguage);
}
