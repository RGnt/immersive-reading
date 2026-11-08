using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.CustomFields;
using ImmersiveReading.Library.Folders;
using ImmersiveReading.Library.Internal;
using ImmersiveReading.Library.Tags;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

public sealed class BookImportService
{
    private readonly LibraryDatabase _db;
    private readonly FolderService _folderService;
    private readonly TagService _tagService;
    private readonly CustomFieldService _customFieldService;
    private readonly BookQueryService _bookQueryService;
    private readonly string? _enrichmentExtractorOutputDir;
    private readonly TimeProvider _timeProvider;

    public BookImportService(
        LibraryDatabase db,
        FolderService folderService,
        TagService tagService,
        CustomFieldService customFieldService,
        BookQueryService bookQueryService,
        string? enrichmentExtractorOutputDir = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _folderService = folderService;
        _tagService = tagService;
        _customFieldService = customFieldService;
        _bookQueryService = bookQueryService;
        _enrichmentExtractorOutputDir = enrichmentExtractorOutputDir;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<BookDetail> ImportBookAsync(
        string epubFilePath,
        string? folderId = null,
        string? seriesNameOverride = null,
        double? seriesIndexOverride = null,
        IReadOnlyList<string>? tagIds = null,
        IReadOnlyList<CustomFieldValueInput>? customFields = null,
        bool computeDeepChapterStats = false,
        CancellationToken ct = default)
    {
        string fullPath = Path.GetFullPath(epubFilePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"EPUB file not found: {fullPath}", fullPath);
        }

        var pkg = await EpubMetadataExtractor.ExtractFromFileAsync(fullPath, computeDeepChapterStats, ct).ConfigureAwait(false);
        string bookDir = Path.GetDirectoryName(fullPath) ?? "";
        string epubFileName = Path.GetFileName(fullPath);

        string? coverPath = SaveCoverToCache(pkg.BookId, pkg.CoverImageBytes, pkg.CoverExtension);
        bool hasStudyPack = CheckStudyPackExists(pkg.BookId);

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        UpsertExtractedBookInTransaction(
            conn,
            tx,
            pkg,
            bookDir,
            epubFileName,
            coverPath,
            hasStudyPack,
            folderId,
            seriesNameOverride,
            seriesIndexOverride);

        if (tagIds is { Count: > 0 })
        {
            _tagService.SetBookTagsInTransaction(conn, tx, pkg.BookId, tagIds);
        }

        // Auto-attach sibling CSV deck if present and not explicitly provided
        var effectiveCustomInputs = new List<CustomFieldValueInput>();
        if (customFields is not null)
        {
            effectiveCustomInputs.AddRange(customFields);
        }

        if (!effectiveCustomInputs.Any(c => string.Equals(c.FieldKey, "vocab_deck_csv", StringComparison.OrdinalIgnoreCase)) &&
            Directory.Exists(bookDir))
        {
            string? firstCsv = Directory.EnumerateFiles(bookDir, "*.csv", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (firstCsv is not null)
            {
                effectiveCustomInputs.Add(new CustomFieldValueInput("vocab_deck_csv", firstCsv));
            }
        }

        if (effectiveCustomInputs.Count > 0)
        {
            _customFieldService.SetEntityCustomFieldsInTransaction(conn, tx, "book", pkg.BookId, effectiveCustomInputs);
        }

        FtsIndexer.RebuildFtsForBookInTransaction(conn, tx, pkg.BookId);
        tx.Commit();

        return _bookQueryService.GetBookDetail(pkg.BookId)
            ?? throw new InvalidOperationException($"Failed to load book '{pkg.BookId}' after import.");
    }

    public async Task<ScanBatchResult> ScanDirectoryAsync(
        string rootDirectory,
        string? targetFolderId = null,
        bool recursive = true,
        bool autoCreateFolderHierarchyFromDirectories = false,
        IProgress<ScanBatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string fullRoot = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(fullRoot))
        {
            throw new DirectoryNotFoundException($"Directory not found: {fullRoot}");
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var epubFiles = Directory.EnumerateFiles(fullRoot, "*.epub", searchOption).ToList();

        int totalFiles = epubFiles.Count;
        int scanned = 0;
        int importedNew = 0;
        int deduplicated = 0;
        int failed = 0;
        var errors = new ConcurrentBag<string>();

        // Preload existing book IDs & SHA256s for fast deduplication detection
        var existingBookIds = LoadExistingBookIds();

        // Optional directory -> virtual folder mapping when autoCreateFolderHierarchyFromDirectories is true
        var dirToFolderId = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [fullRoot] = targetFolderId
        };

        const int batchSize = 250;
        for (int offset = 0; offset < epubFiles.Count; offset += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = epubFiles.Skip(offset).Take(batchSize).ToList();
            var extractedBag = new ConcurrentBag<(string FilePath, ExtractedEpubPackage Package, string? CoverCachePath, bool HasStudyPack)>();

            await Parallel.ForEachAsync(
                chunk,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                    CancellationToken = ct
                },
                async (filePath, token) =>
                {
                    try
                    {
                        // Stage A fast scan: computeDeepChapterStats = false
                        var pkg = await EpubMetadataExtractor.ExtractFromFileAsync(
                            filePath,
                            computeDeepChapterStats: false,
                            ct: token).ConfigureAwait(false);

                        string? coverPath = SaveCoverToCache(pkg.BookId, pkg.CoverImageBytes, pkg.CoverExtension);
                        bool hasPack = CheckStudyPackExists(pkg.BookId);
                        extractedBag.Add((filePath, pkg, coverPath, hasPack));
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        errors.Add($"{Path.GetFileName(filePath)}: {ex.Message}");
                    }
                }).ConfigureAwait(false);

            // Commit batch in a single SQLite WAL transaction
            using var conn = _db.OpenConnection();
            using var tx = conn.BeginTransaction();

            foreach (var item in extractedBag.OrderBy(x => x.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                string bookDir = Path.GetDirectoryName(item.FilePath) ?? "";
                string epubFileName = Path.GetFileName(item.FilePath);

                string? assignedFolderId = targetFolderId;
                if (autoCreateFolderHierarchyFromDirectories)
                {
                    assignedFolderId = _folderService.ResolveOrCreateFolderForDiskDirectory(conn, tx, fullRoot, bookDir, dirToFolderId);
                }

                bool existed = existingBookIds.Contains(item.Package.BookId);
                UpsertExtractedBookInTransaction(
                    conn,
                    tx,
                    item.Package,
                    bookDir,
                    epubFileName,
                    item.CoverCachePath,
                    item.HasStudyPack,
                    assignedFolderId,
                    seriesNameOverride: null,
                    seriesIndexOverride: null);

                // Attach sibling CSV if present
                if (Directory.Exists(bookDir))
                {
                    string? firstCsv = Directory.EnumerateFiles(bookDir, "*.csv", SearchOption.TopDirectoryOnly).FirstOrDefault();
                    if (firstCsv is not null)
                    {
                        _customFieldService.SetEntityCustomFieldsInTransaction(
                            conn,
                            tx,
                            "book",
                            item.Package.BookId,
                            [new CustomFieldValueInput("vocab_deck_csv", firstCsv)]);
                    }
                }

                FtsIndexer.RebuildFtsForBookInTransaction(conn, tx, item.Package.BookId);

                if (existed)
                {
                    deduplicated++;
                }
                else
                {
                    existingBookIds.Add(item.Package.BookId);
                    importedNew++;
                }

                scanned++;
            }

            tx.Commit();

            string lastFile = chunk.Count > 0 ? Path.GetFileName(chunk[^1]) : "";
            progress?.Report(new ScanBatchProgress(
                ScannedFiles: scanned + failed,
                TotalFiles: totalFiles,
                ImportedNew: importedNew,
                DeduplicatedExisting: deduplicated,
                FailedFiles: failed,
                CurrentFile: lastFile));
        }

        sw.Stop();
        return new ScanBatchResult(
            TotalFilesDiscovered: totalFiles,
            ImportedNew: importedNew,
            DeduplicatedExisting: deduplicated,
            FailedFiles: failed,
            ElapsedSeconds: Math.Round(sw.Elapsed.TotalSeconds, 3),
            Errors: errors.ToList());
    }

    public HashSet<string> LoadExistingBookIds()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT book_id FROM books;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            set.Add(reader.GetString(0));
        }
        return set;
    }

    public void UpsertExtractedBookInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        ExtractedEpubPackage pkg,
        string bookDir,
        string epubFileName,
        string? coverPath,
        bool hasStudyPack,
        string? folderId,
        string? seriesNameOverride,
        double? seriesIndexOverride)
    {
        string now = _timeProvider.GetUtcNow().UtcDateTime.ToString("O");
        string opfJson = JsonSerializer.Serialize(pkg.OpfOriginal);
        string seriesName = seriesNameOverride ?? pkg.SeriesName;
        double? seriesIndex = seriesIndexOverride ?? pkg.SeriesIndex;

        // Check if book already exists and whether user edited its metadata
        bool exists = false;
        bool isEdited = false;
        using (var chk = conn.CreateCommand())
        {
            chk.Transaction = tx;
            chk.CommandText = "SELECT is_metadata_edited FROM books WHERE book_id = $id;";
            chk.Parameters.AddWithValue("$id", pkg.BookId);
            using var r = chk.ExecuteReader();
            if (r.Read())
            {
                exists = true;
                isEdited = r.GetInt32(0) != 0;
            }
        }

        if (!exists)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO books (
                    book_id, epub_sha256, folder_id, title, sort_title, series_name, series_index, custom_sort_order,
                    authors, author_sort, isbn, publisher, imprint, published_date, language, description,
                    opf_identifier, opf_original_json, is_metadata_edited, folder_path, epub_file_name, file_size_bytes,
                    cover_cache_path, chapter_count, total_char_count, stats_computed, writing_mode,
                    added_at_utc, updated_at_utc, has_study_pack
                ) VALUES (
                    $id, $sha, $folderId, $title, $sortTitle, $seriesName, $seriesIdx, 0,
                    $authors, $authorSort, $isbn, $publisher, $imprint, $pubDate, $lang, $desc,
                    $opfId, $opfJson, $edited, $folderPath, $fileName, $fileSize,
                    $cover, $chCount, $charCount, $statsComp, $writingMode,
                    $now, $now, $hasPack
                );
                """;
            ins.Parameters.AddWithValue("$id", pkg.BookId);
            ins.Parameters.AddWithValue("$sha", pkg.EpubSha256);
            ins.Parameters.AddWithValue("$folderId", (object?)folderId ?? DBNull.Value);
            ins.Parameters.AddWithValue("$title", pkg.Title);
            ins.Parameters.AddWithValue("$sortTitle", pkg.SortTitle);
            ins.Parameters.AddWithValue("$seriesName", seriesName);
            ins.Parameters.AddWithValue("$seriesIdx", (object?)seriesIndex ?? DBNull.Value);
            ins.Parameters.AddWithValue("$authors", pkg.Authors);
            ins.Parameters.AddWithValue("$authorSort", pkg.AuthorSort);
            ins.Parameters.AddWithValue("$isbn", pkg.Isbn);
            ins.Parameters.AddWithValue("$publisher", pkg.Publisher);
            ins.Parameters.AddWithValue("$imprint", pkg.Imprint);
            ins.Parameters.AddWithValue("$pubDate", pkg.PublishedDate);
            ins.Parameters.AddWithValue("$lang", pkg.Language);
            ins.Parameters.AddWithValue("$desc", pkg.Description);
            ins.Parameters.AddWithValue("$opfId", pkg.OpfOriginal.OpfIdentifier);
            ins.Parameters.AddWithValue("$opfJson", opfJson);
            ins.Parameters.AddWithValue("$edited", (seriesNameOverride is not null || seriesIndexOverride is not null) ? 1 : 0);
            ins.Parameters.AddWithValue("$folderPath", bookDir);
            ins.Parameters.AddWithValue("$fileName", epubFileName);
            ins.Parameters.AddWithValue("$fileSize", pkg.FileSizeBytes);
            ins.Parameters.AddWithValue("$cover", (object?)coverPath ?? DBNull.Value);
            ins.Parameters.AddWithValue("$chCount", pkg.Chapters.Count);
            ins.Parameters.AddWithValue("$charCount", pkg.TotalCharCount);
            ins.Parameters.AddWithValue("$statsComp", pkg.StatsComputed ? 1 : 0);
            ins.Parameters.AddWithValue("$writingMode", pkg.WritingMode);
            ins.Parameters.AddWithValue("$now", now);
            ins.Parameters.AddWithValue("$hasPack", hasStudyPack ? 1 : 0);
            ins.ExecuteNonQuery();
        }
        else
        {
            // Preserve user-edited bibliographic fields when re-importing!
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            if (isEdited)
            {
                upd.CommandText = """
                    UPDATE books
                    SET folder_path = $folderPath,
                        epub_file_name = $fileName,
                        file_size_bytes = $fileSize,
                        cover_cache_path = COALESCE($cover, cover_cache_path),
                        folder_id = COALESCE($folderId, folder_id),
                        has_study_pack = $hasPack,
                        updated_at_utc = $now
                    WHERE book_id = $id;
                    """;
                upd.Parameters.AddWithValue("$id", pkg.BookId);
                upd.Parameters.AddWithValue("$folderPath", bookDir);
                upd.Parameters.AddWithValue("$fileName", epubFileName);
                upd.Parameters.AddWithValue("$fileSize", pkg.FileSizeBytes);
                upd.Parameters.AddWithValue("$cover", (object?)coverPath ?? DBNull.Value);
                upd.Parameters.AddWithValue("$folderId", (object?)folderId ?? DBNull.Value);
                upd.Parameters.AddWithValue("$hasPack", hasStudyPack ? 1 : 0);
                upd.Parameters.AddWithValue("$now", now);
                upd.ExecuteNonQuery();
            }
            else
            {
                upd.CommandText = """
                    UPDATE books
                    SET folder_path = $folderPath,
                        epub_file_name = $fileName,
                        file_size_bytes = $fileSize,
                        cover_cache_path = COALESCE($cover, cover_cache_path),
                        folder_id = COALESCE($folderId, folder_id),
                        series_name = CASE WHEN $seriesName != '' THEN $seriesName ELSE series_name END,
                        series_index = COALESCE($seriesIdx, series_index),
                        chapter_count = $chCount,
                        total_char_count = CASE WHEN $statsComp = 1 THEN $charCount ELSE total_char_count END,
                        stats_computed = CASE WHEN $statsComp = 1 THEN 1 ELSE stats_computed END,
                        has_study_pack = $hasPack,
                        updated_at_utc = $now
                    WHERE book_id = $id;
                    """;
                upd.Parameters.AddWithValue("$id", pkg.BookId);
                upd.Parameters.AddWithValue("$folderPath", bookDir);
                upd.Parameters.AddWithValue("$fileName", epubFileName);
                upd.Parameters.AddWithValue("$fileSize", pkg.FileSizeBytes);
                upd.Parameters.AddWithValue("$cover", (object?)coverPath ?? DBNull.Value);
                upd.Parameters.AddWithValue("$folderId", (object?)folderId ?? DBNull.Value);
                upd.Parameters.AddWithValue("$seriesName", seriesName);
                upd.Parameters.AddWithValue("$seriesIdx", (object?)seriesIndex ?? DBNull.Value);
                upd.Parameters.AddWithValue("$chCount", pkg.Chapters.Count);
                upd.Parameters.AddWithValue("$charCount", pkg.TotalCharCount);
                upd.Parameters.AddWithValue("$statsComp", pkg.StatsComputed ? 1 : 0);
                upd.Parameters.AddWithValue("$hasPack", hasStudyPack ? 1 : 0);
                upd.Parameters.AddWithValue("$now", now);
                upd.ExecuteNonQuery();
            }
        }

        // Upsert chapters
        using (var delCh = conn.CreateCommand())
        {
            delCh.Transaction = tx;
            delCh.CommandText = "DELETE FROM book_chapters WHERE book_id = $id;";
            delCh.Parameters.AddWithValue("$id", pkg.BookId);
            delCh.ExecuteNonQuery();
        }

        if (pkg.Chapters.Count > 0)
        {
            using var chCmd = conn.CreateCommand();
            chCmd.Transaction = tx;
            chCmd.CommandText = """
                INSERT INTO book_chapters (
                    book_id, chapter_index, chapter_id, title, spine_href, fragment_id,
                    end_spine_href, end_fragment_id, char_count, cumulative_chars, is_excluded
                ) VALUES (
                    $bookId, $idx, $chId, $title, $href, $frag,
                    $endHref, $endFrag, $chars, $cumChars, $excl
                );
                """;
            var pBookId = chCmd.Parameters.Add("$bookId", SqliteType.Text);
            var pIdx = chCmd.Parameters.Add("$idx", SqliteType.Integer);
            var pChId = chCmd.Parameters.Add("$chId", SqliteType.Text);
            var pTitle = chCmd.Parameters.Add("$title", SqliteType.Text);
            var pHref = chCmd.Parameters.Add("$href", SqliteType.Text);
            var pFrag = chCmd.Parameters.Add("$frag", SqliteType.Text);
            var pEndHref = chCmd.Parameters.Add("$endHref", SqliteType.Text);
            var pEndFrag = chCmd.Parameters.Add("$endFrag", SqliteType.Text);
            var pChars = chCmd.Parameters.Add("$chars", SqliteType.Integer);
            var pCumChars = chCmd.Parameters.Add("$cumChars", SqliteType.Integer);
            var pExcl = chCmd.Parameters.Add("$excl", SqliteType.Integer);

            pBookId.Value = pkg.BookId;
            foreach (var ch in pkg.Chapters)
            {
                pIdx.Value = ch.ChapterIndex;
                pChId.Value = ch.ChapterId;
                pTitle.Value = ch.Title;
                pHref.Value = ch.SpineHref;
                pFrag.Value = ch.FragmentId;
                pEndHref.Value = (object?)ch.EndSpineHref ?? DBNull.Value;
                pEndFrag.Value = (object?)ch.EndFragmentId ?? DBNull.Value;
                pChars.Value = ch.CharCount;
                pCumChars.Value = ch.CumulativeChars;
                pExcl.Value = ch.IsExcluded ? 1 : 0;
                chCmd.ExecuteNonQuery();
            }
        }
    }

    public string? SaveCoverToCache(string bookId, byte[]? coverBytes, string? ext)
    {
        if (coverBytes is null || coverBytes.Length == 0) return null;
        string safeExt = string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext;
        if (!safeExt.StartsWith('.')) safeExt = "." + safeExt;
        string path = Path.Combine(_db.CoversCacheDirectory, $"{bookId}{safeExt}");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, coverBytes);
        }
        return path;
    }

    public bool CheckStudyPackExists(string bookId)
    {
        if (string.IsNullOrWhiteSpace(_enrichmentExtractorOutputDir)) return false;
        string readerJson = Path.Combine(_enrichmentExtractorOutputDir, "data", bookId, "reader.json");
        return File.Exists(readerJson);
    }
}

