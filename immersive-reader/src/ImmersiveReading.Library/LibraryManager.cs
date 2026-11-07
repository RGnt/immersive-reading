using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Library;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library;

public sealed class LibraryManager
{
    private readonly LibraryDatabase _db;
    private readonly string? _enrichmentExtractorOutputDir;

    public LibraryManager(LibraryDatabase db, string? enrichmentExtractorOutputDir = null)
    {
        _db = db;
        _enrichmentExtractorOutputDir = enrichmentExtractorOutputDir;
    }

    // =========================================================================
    // 1. Book Ingestion & 24,000-Book Parallel Directory Scanner
    // =========================================================================

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
            SetBookTagsInTransaction(conn, tx, pkg.BookId, tagIds);
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
            SetEntityCustomFieldsInTransaction(conn, tx, "book", pkg.BookId, effectiveCustomInputs);
        }

        RebuildFtsForBookInTransaction(conn, tx, pkg.BookId);
        tx.Commit();

        return GetBookDetail(pkg.BookId)
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
                    assignedFolderId = ResolveOrCreateFolderForDiskDirectory(conn, tx, fullRoot, bookDir, dirToFolderId);
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
                        SetEntityCustomFieldsInTransaction(
                            conn,
                            tx,
                            "book",
                            item.Package.BookId,
                            [new CustomFieldValueInput("vocab_deck_csv", firstCsv)]);
                    }
                }

                RebuildFtsForBookInTransaction(conn, tx, item.Package.BookId);

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

    private HashSet<string> LoadExistingBookIds()
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

    private string? ResolveOrCreateFolderForDiskDirectory(
        SqliteConnection conn,
        SqliteTransaction tx,
        string rootDir,
        string currentDir,
        Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(currentDir, out string? cachedId))
        {
            return cachedId;
        }

        string rel = Path.GetRelativePath(rootDir, currentDir);
        if (rel == "." || string.IsNullOrWhiteSpace(rel) || rel.StartsWith("..", StringComparison.Ordinal))
        {
            return cache.GetValueOrDefault(rootDir);
        }

        // If the folder contains only 1 EPUB and matches the book name (like enrichement-extractor/books/<book>),
        // don't create a redundant subfolder per single book unless it's a multi-volume directory.
        int epubCountInDir = Directory.EnumerateFiles(currentDir, "*.epub", SearchOption.TopDirectoryOnly).Take(2).Count();
        int subDirCount = Directory.EnumerateDirectories(currentDir).Take(1).Count();
        string? parentDir = Path.GetDirectoryName(currentDir);

        if (epubCountInDir == 1 && subDirCount == 0 && parentDir is not null)
        {
            string? parentFolderId = ResolveOrCreateFolderForDiskDirectory(conn, tx, rootDir, parentDir, cache);
            cache[currentDir] = parentFolderId;
            return parentFolderId;
        }

        string? parentId = parentDir is not null
            ? ResolveOrCreateFolderForDiskDirectory(conn, tx, rootDir, parentDir, cache)
            : cache.GetValueOrDefault(rootDir);

        string dirName = Path.GetFileName(currentDir);
        string folderId = EnsureChildFolderInTransaction(conn, tx, parentId, dirName);
        cache[currentDir] = folderId;
        return folderId;
    }

    private static string EnsureChildFolderInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string? parentFolderId,
        string name)
    {
        using (var findCmd = conn.CreateCommand())
        {
            findCmd.Transaction = tx;
            findCmd.CommandText = parentFolderId is null
                ? "SELECT folder_id FROM library_folders WHERE parent_folder_id IS NULL AND name = $name LIMIT 1;"
                : "SELECT folder_id FROM library_folders WHERE parent_folder_id = $parent AND name = $name LIMIT 1;";
            findCmd.Parameters.AddWithValue("$name", name);
            if (parentFolderId is not null)
            {
                findCmd.Parameters.AddWithValue("$parent", parentFolderId);
            }
            if (findCmd.ExecuteScalar() is string existingId)
            {
                return existingId;
            }
        }

        string newId = $"fld-{Guid.NewGuid():N}"[..20];
        string now = DateTime.UtcNow.ToString("O");
        using var insCmd = conn.CreateCommand();
        insCmd.Transaction = tx;
        insCmd.CommandText = """
            INSERT INTO library_folders (
                folder_id, parent_folder_id, name, folder_kind, description, icon, sort_order, default_book_sort, created_at_utc, updated_at_utc
            ) VALUES (
                $id, $parent, $name, 'folder', '', 'folder', 0, 'series_or_title', $now, $now
            );
            """;
        insCmd.Parameters.AddWithValue("$id", newId);
        insCmd.Parameters.AddWithValue("$parent", (object?)parentFolderId ?? DBNull.Value);
        insCmd.Parameters.AddWithValue("$name", name);
        insCmd.Parameters.AddWithValue("$now", now);
        insCmd.ExecuteNonQuery();
        return newId;
    }

    private static void UpsertExtractedBookInTransaction(
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
        string now = DateTime.UtcNow.ToString("O");
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

    private string? SaveCoverToCache(string bookId, byte[]? coverBytes, string? ext)
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

    private bool CheckStudyPackExists(string bookId)
    {
        if (string.IsNullOrWhiteSpace(_enrichmentExtractorOutputDir)) return false;
        string readerJson = Path.Combine(_enrichmentExtractorOutputDir, "data", bookId, "reader.json");
        return File.Exists(readerJson);
    }

    // =========================================================================
    // 2. Editable Bibliographic Metadata (Single & Bulk + OPF Reset)
    // =========================================================================

    public BookDetail UpdateBookMetadata(BookMetadataUpdateRequest req)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        string now = DateTime.UtcNow.ToString("O");
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE books
                SET title = COALESCE($title, title),
                    sort_title = COALESCE($sortTitle, sort_title),
                    authors = COALESCE($authors, authors),
                    author_sort = COALESCE($authorSort, author_sort),
                    isbn = COALESCE($isbn, isbn),
                    publisher = COALESCE($publisher, publisher),
                    imprint = COALESCE($imprint, imprint),
                    published_date = COALESCE($pubDate, published_date),
                    series_name = COALESCE($seriesName, series_name),
                    series_index = CASE WHEN $clearSeriesIdx = 1 THEN NULL ELSE COALESCE($seriesIdx, series_index) END,
                    description = COALESCE($desc, description),
                    language = COALESCE($lang, language),
                    writing_mode = COALESCE($writingMode, writing_mode),
                    folder_id = CASE WHEN $clearFolder = 1 THEN NULL ELSE COALESCE($folderId, folder_id) END,
                    custom_sort_order = COALESCE($customSort, custom_sort_order),
                    is_metadata_edited = 1,
                    updated_at_utc = $now
                WHERE book_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", req.BookId);
            cmd.Parameters.AddWithValue("$title", (object?)req.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sortTitle", (object?)req.SortTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authors", (object?)req.Authors ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$authorSort", (object?)req.AuthorSort ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$isbn", (object?)req.Isbn ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$publisher", (object?)req.Publisher ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imprint", (object?)req.Imprint ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pubDate", (object?)req.PublishedDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seriesName", (object?)req.SeriesName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seriesIdx", (object?)req.SeriesIndex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clearSeriesIdx", req.ClearSeriesIndex ? 1 : 0);
            cmd.Parameters.AddWithValue("$desc", (object?)req.Description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lang", (object?)req.Language ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$writingMode", (object?)req.WritingMode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$folderId", (object?)req.FolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$clearFolder", req.ClearFolderId ? 1 : 0);
            cmd.Parameters.AddWithValue("$customSort", (object?)req.CustomSortOrder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            int affected = cmd.ExecuteNonQuery();
            if (affected == 0)
            {
                throw new KeyNotFoundException($"Book '{req.BookId}' not found.");
            }
        }

        if (req.TagIds is not null)
        {
            SetBookTagsInTransaction(conn, tx, req.BookId, req.TagIds);
        }

        if (req.CustomFields is not null)
        {
            SetEntityCustomFieldsInTransaction(conn, tx, "book", req.BookId, req.CustomFields);
        }

        RebuildFtsForBookInTransaction(conn, tx, req.BookId);
        tx.Commit();

        return GetBookDetail(req.BookId)!;
    }

    public int BulkUpdateBooks(BulkBookUpdatePatch patch)
    {
        if (patch.BookIds is null || patch.BookIds.Count == 0) return 0;

        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        string now = DateTime.UtcNow.ToString("O");
        int updated = 0;

        for (int i = 0; i < patch.BookIds.Count; i++)
        {
            string bookId = patch.BookIds[i];
            double? volumeNumber = patch.AutoNumberVolumes ? (patch.AutoNumberStart + i) : null;

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE books
                    SET folder_id = CASE WHEN $clearFolder = 1 THEN NULL ELSE COALESCE($folderId, folder_id) END,
                        series_name = COALESCE($seriesName, series_name),
                        series_index = COALESCE($volNum, series_index),
                        authors = COALESCE($authors, authors),
                        author_sort = COALESCE($authorSort, author_sort),
                        publisher = COALESCE($publisher, publisher),
                        imprint = COALESCE($imprint, imprint),
                        language = COALESCE($lang, language),
                        is_metadata_edited = 1,
                        updated_at_utc = $now
                    WHERE book_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", bookId);
                cmd.Parameters.AddWithValue("$clearFolder", patch.ClearFolderId ? 1 : 0);
                cmd.Parameters.AddWithValue("$folderId", (object?)patch.FolderId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$seriesName", (object?)patch.SeriesName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$volNum", (object?)volumeNumber ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$authors", (object?)patch.Authors ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$authorSort", (object?)patch.AuthorSort ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$publisher", (object?)patch.Publisher ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$imprint", (object?)patch.Imprint ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lang", (object?)patch.Language ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$now", now);
                updated += cmd.ExecuteNonQuery();
            }

            if (patch.AddTagIds is { Count: > 0 })
            {
                foreach (string tagId in patch.AddTagIds)
                {
                    using var tCmd = conn.CreateCommand();
                    tCmd.Transaction = tx;
                    tCmd.CommandText = "INSERT OR IGNORE INTO book_tags (book_id, tag_id, assigned_at_utc) VALUES ($b, $t, $now);";
                    tCmd.Parameters.AddWithValue("$b", bookId);
                    tCmd.Parameters.AddWithValue("$t", tagId);
                    tCmd.Parameters.AddWithValue("$now", now);
                    tCmd.ExecuteNonQuery();
                }
            }

            if (patch.RemoveTagIds is { Count: > 0 })
            {
                foreach (string tagId in patch.RemoveTagIds)
                {
                    using var tCmd = conn.CreateCommand();
                    tCmd.Transaction = tx;
                    tCmd.CommandText = "DELETE FROM book_tags WHERE book_id = $b AND tag_id = $t;";
                    tCmd.Parameters.AddWithValue("$b", bookId);
                    tCmd.Parameters.AddWithValue("$t", tagId);
                    tCmd.ExecuteNonQuery();
                }
            }

            if (patch.CustomFields is { Count: > 0 })
            {
                SetEntityCustomFieldsInTransaction(conn, tx, "book", bookId, patch.CustomFields);
            }

            RebuildFtsForBookInTransaction(conn, tx, bookId);
        }

        tx.Commit();
        return updated;
    }

    public BookDetail ResetBookMetadataToOpf(string bookId)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        string? opfJson = null;
        using (var getCmd = conn.CreateCommand())
        {
            getCmd.Transaction = tx;
            getCmd.CommandText = "SELECT opf_original_json FROM books WHERE book_id = $id;";
            getCmd.Parameters.AddWithValue("$id", bookId);
            opfJson = getCmd.ExecuteScalar() as string;
        }

        if (opfJson is null)
        {
            throw new KeyNotFoundException($"Book '{bookId}' not found.");
        }

        var opf = JsonSerializer.Deserialize<OpfOriginalMetadata>(opfJson) ?? new OpfOriginalMetadata();
        string now = DateTime.UtcNow.ToString("O");

        using (var upd = conn.CreateCommand())
        {
            upd.Transaction = tx;
            upd.CommandText = """
                UPDATE books
                SET title = $title,
                    sort_title = $sortTitle,
                    authors = $authors,
                    author_sort = $authorSort,
                    isbn = $isbn,
                    publisher = $publisher,
                    imprint = $imprint,
                    published_date = $pubDate,
                    language = $lang,
                    description = $desc,
                    series_name = $seriesName,
                    series_index = $seriesIdx,
                    writing_mode = $writingMode,
                    is_metadata_edited = 0,
                    updated_at_utc = $now
                WHERE book_id = $id;
                """;
            upd.Parameters.AddWithValue("$id", bookId);
            upd.Parameters.AddWithValue("$title", opf.Title);
            upd.Parameters.AddWithValue("$sortTitle", !string.IsNullOrWhiteSpace(opf.SortTitle) ? opf.SortTitle : opf.Title);
            upd.Parameters.AddWithValue("$authors", opf.Authors);
            upd.Parameters.AddWithValue("$authorSort", !string.IsNullOrWhiteSpace(opf.AuthorSort) ? opf.AuthorSort : opf.Authors);
            upd.Parameters.AddWithValue("$isbn", opf.Isbn);
            upd.Parameters.AddWithValue("$publisher", opf.Publisher);
            upd.Parameters.AddWithValue("$imprint", opf.Imprint);
            upd.Parameters.AddWithValue("$pubDate", opf.PublishedDate);
            upd.Parameters.AddWithValue("$lang", opf.Language);
            upd.Parameters.AddWithValue("$desc", opf.Description);
            upd.Parameters.AddWithValue("$seriesName", opf.SeriesName);
            upd.Parameters.AddWithValue("$seriesIdx", (object?)opf.SeriesIndex ?? DBNull.Value);
            upd.Parameters.AddWithValue("$writingMode", opf.WritingMode);
            upd.Parameters.AddWithValue("$now", now);
            upd.ExecuteNonQuery();
        }

        RebuildFtsForBookInTransaction(conn, tx, bookId);
        tx.Commit();

        return GetBookDetail(bookId)!;
    }

    // =========================================================================
    // 3. Hierarchical Virtual Folders (Collection -> Category -> Series)
    // =========================================================================

    public IReadOnlyList<LibraryFolderNode> GetFolderTree()
    {
        using var conn = _db.OpenConnection();
        var rawFolders = LoadAllFoldersRaw(conn);
        var directCounts = LoadDirectBookCountsPerFolder(conn);
        var folderTagsMap = LoadAllFolderTags(conn);
        var fieldDefs = LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = LoadAllCustomFieldValues(conn);

        // Build parent -> children map
        var childrenMap = new Dictionary<string, List<RawFolderRow>>(StringComparer.Ordinal);
        var roots = new List<RawFolderRow>();
        var folderLookup = rawFolders.ToDictionary(f => f.FolderId, StringComparer.Ordinal);

        foreach (var f in rawFolders)
        {
            if (string.IsNullOrEmpty(f.ParentFolderId) || !folderLookup.ContainsKey(f.ParentFolderId))
            {
                roots.Add(f);
            }
            else
            {
                if (!childrenMap.TryGetValue(f.ParentFolderId, out var list))
                {
                    list = [];
                    childrenMap[f.ParentFolderId] = list;
                }
                list.Add(f);
            }
        }

        LibraryFolderNode BuildNode(RawFolderRow row)
        {
            var childNodes = new List<LibraryFolderNode>();
            if (childrenMap.TryGetValue(row.FolderId, out var childRows))
            {
                foreach (var cr in childRows.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    childNodes.Add(BuildNode(cr));
                }
            }

            int direct = directCounts.GetValueOrDefault(row.FolderId, 0);
            int subtree = direct + childNodes.Sum(c => c.TotalSubtreeBookCount);
            var tags = folderTagsMap.GetValueOrDefault(row.FolderId) ?? [];
            var effFields = ResolveEffectiveCustomFieldsForFolder(row.FolderId, folderLookup, fieldDefs, allCustomValues);

            return new LibraryFolderNode(
                FolderId: row.FolderId,
                ParentFolderId: row.ParentFolderId,
                Name: row.Name,
                FolderKind: row.FolderKind,
                Description: row.Description,
                Icon: row.Icon,
                ColorHex: row.ColorHex,
                SortOrder: row.SortOrder,
                DefaultBookSort: row.DefaultBookSort,
                DirectBookCount: direct,
                TotalSubtreeBookCount: subtree,
                Tags: tags,
                CustomFields: effFields,
                Children: childNodes);
        }

        return roots
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildNode)
            .ToList();
    }

    public LibraryFolderNode SaveFolder(
        string? folderId,
        string? parentFolderId,
        string name,
        string folderKind = "folder",
        string description = "",
        string icon = "folder",
        string? colorHex = null,
        int sortOrder = 0,
        string defaultBookSort = "series_or_title",
        IReadOnlyList<string>? tagIds = null,
        IReadOnlyList<CustomFieldValueInput>? customFields = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Folder name cannot be empty.", nameof(name));
        }

        string id = string.IsNullOrWhiteSpace(folderId)
            ? $"fld-{Guid.NewGuid():N}"[..20]
            : folderId.Trim();

        if (string.Equals(id, parentFolderId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A folder cannot be its own parent.");
        }

        string now = DateTime.UtcNow.ToString("O");
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO library_folders (
                    folder_id, parent_folder_id, name, folder_kind, description, icon, color_hex,
                    sort_order, default_book_sort, created_at_utc, updated_at_utc
                ) VALUES (
                    $id, $parent, $name, $kind, $desc, $icon, $color,
                    $order, $sort, $now, $now
                )
                ON CONFLICT(folder_id) DO UPDATE SET
                    parent_folder_id = excluded.parent_folder_id,
                    name = excluded.name,
                    folder_kind = excluded.folder_kind,
                    description = excluded.description,
                    icon = excluded.icon,
                    color_hex = excluded.color_hex,
                    sort_order = excluded.sort_order,
                    default_book_sort = excluded.default_book_sort,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$parent", (object?)parentFolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$name", name.Trim());
            cmd.Parameters.AddWithValue("$kind", folderKind);
            cmd.Parameters.AddWithValue("$desc", description);
            cmd.Parameters.AddWithValue("$icon", icon);
            cmd.Parameters.AddWithValue("$color", (object?)colorHex ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$order", sortOrder);
            cmd.Parameters.AddWithValue("$sort", defaultBookSort);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        if (tagIds is not null)
        {
            using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM folder_tags WHERE folder_id = $f;";
                del.Parameters.AddWithValue("$f", id);
                del.ExecuteNonQuery();
            }
            foreach (string tid in tagIds)
            {
                using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = "INSERT OR IGNORE INTO folder_tags (folder_id, tag_id, assigned_at_utc) VALUES ($f, $t, $now);";
                ins.Parameters.AddWithValue("$f", id);
                ins.Parameters.AddWithValue("$t", tid);
                ins.Parameters.AddWithValue("$now", now);
                ins.ExecuteNonQuery();
            }
        }

        if (customFields is not null)
        {
            SetEntityCustomFieldsInTransaction(conn, tx, "folder", id, customFields);
        }

        tx.Commit();

        return FindFolderNodeInTree(GetFolderTree(), id)
            ?? new LibraryFolderNode(id, parentFolderId, name, folderKind, description, icon, colorHex, sortOrder, defaultBookSort);
    }

    public void DeleteFolder(string folderId, bool reassignBooksToParent = true)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        if (reassignBooksToParent)
        {
            string? parentId = null;
            using (var getP = conn.CreateCommand())
            {
                getP.Transaction = tx;
                getP.CommandText = "SELECT parent_folder_id FROM library_folders WHERE folder_id = $id;";
                getP.Parameters.AddWithValue("$id", folderId);
                parentId = getP.ExecuteScalar() as string;
            }

            using (var updB = conn.CreateCommand())
            {
                updB.Transaction = tx;
                updB.CommandText = "UPDATE books SET folder_id = $parent WHERE folder_id = $id;";
                updB.Parameters.AddWithValue("$id", folderId);
                updB.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
                updB.ExecuteNonQuery();
            }

            using (var updF = conn.CreateCommand())
            {
                updF.Transaction = tx;
                updF.CommandText = "UPDATE library_folders SET parent_folder_id = $parent WHERE parent_folder_id = $id;";
                updF.Parameters.AddWithValue("$id", folderId);
                updF.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
                updF.ExecuteNonQuery();
            }
        }

        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM library_folders WHERE folder_id = $id;";
            del.Parameters.AddWithValue("$id", folderId);
            del.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static LibraryFolderNode? FindFolderNodeInTree(IEnumerable<LibraryFolderNode> nodes, string targetId)
    {
        foreach (var n in nodes)
        {
            if (string.Equals(n.FolderId, targetId, StringComparison.Ordinal)) return n;
            if (n.Children is { Count: > 0 })
            {
                var found = FindFolderNodeInTree(n.Children, targetId);
                if (found is not null) return found;
            }
        }
        return null;
    }

    // =========================================================================
    // 4. Manual User Tags
    // =========================================================================

    public IReadOnlyList<TagRecord> ListTags()
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT t.tag_id, t.name, t.color_hex, t.sort_order, t.created_at_utc,
                   COUNT(bt.book_id) AS book_count
            FROM tags t
            LEFT JOIN book_tags bt ON bt.tag_id = t.tag_id
            GROUP BY t.tag_id
            ORDER BY t.sort_order ASC, t.name COLLATE NOCASE ASC;
            """;

        var list = new List<TagRecord>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new TagRecord(
                TagId: r.GetString(0),
                Name: r.GetString(1),
                ColorHex: r.GetString(2),
                SortOrder: r.GetInt32(3),
                BookCount: r.GetInt32(5),
                CreatedAtUtc: r.GetString(4)));
        }
        return list;
    }

    public TagRecord SaveTag(string? tagId, string name, string colorHex = "#64748b", int sortOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));
        }

        string cleanName = name.Trim();
        string id = string.IsNullOrWhiteSpace(tagId)
            ? $"tag-{Guid.NewGuid():N}"[..16]
            : tagId.Trim();
        string now = DateTime.UtcNow.ToString("O");

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tags (tag_id, name, color_hex, sort_order, created_at_utc)
            VALUES ($id, $name, $color, $order, $now)
            ON CONFLICT(name) DO UPDATE SET
                color_hex = excluded.color_hex,
                sort_order = excluded.sort_order
            RETURNING tag_id, name, color_hex, sort_order, created_at_utc;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", cleanName);
        cmd.Parameters.AddWithValue("$color", colorHex);
        cmd.Parameters.AddWithValue("$order", sortOrder);
        cmd.Parameters.AddWithValue("$now", now);

        using var r = cmd.ExecuteReader();
        if (r.Read())
        {
            return new TagRecord(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), 0, r.GetString(4));
        }
        return new TagRecord(id, cleanName, colorHex, sortOrder, 0, now);
    }

    public void DeleteTag(string tagId)
    {
        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM tags WHERE tag_id = $id;";
        cmd.Parameters.AddWithValue("$id", tagId);
        cmd.ExecuteNonQuery();
    }

    // =========================================================================
    // 5. Multi-Level Custom Fields & Hierarchical Inheritance
    // =========================================================================

    public IReadOnlyList<CustomFieldDefinition> ListCustomFieldDefinitions()
    {
        using var conn = _db.OpenConnection();
        return LoadCustomFieldDefinitionsInternal(conn);
    }

    public CustomFieldDefinition SaveCustomFieldDefinition(
        string fieldKey,
        string displayName,
        string fieldType = "text",
        string targetScope = "any",
        bool inheritToChildren = true,
        IReadOnlyList<string>? options = null,
        bool showOnCard = true,
        int sortOrder = 100)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            throw new ArgumentException("Field key cannot be empty.", nameof(fieldKey));
        }

        string cleanKey = fieldKey.Trim().ToLowerInvariant().Replace(' ', '_');
        string now = DateTime.UtcNow.ToString("O");
        string optionsJson = JsonSerializer.Serialize(options ?? []);

        using var conn = _db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO custom_field_definitions (
                field_key, display_name, field_type, target_scope, inherit_to_children, options_json, show_on_card, sort_order, created_at_utc
            ) VALUES (
                $key, $name, $type, $scope, $inherit, $opts, $show, $order, $now
            )
            ON CONFLICT(field_key) DO UPDATE SET
                display_name = excluded.display_name,
                field_type = excluded.field_type,
                target_scope = excluded.target_scope,
                inherit_to_children = excluded.inherit_to_children,
                options_json = excluded.options_json,
                show_on_card = excluded.show_on_card,
                sort_order = excluded.sort_order;
            """;
        cmd.Parameters.AddWithValue("$key", cleanKey);
        cmd.Parameters.AddWithValue("$name", displayName.Trim());
        cmd.Parameters.AddWithValue("$type", fieldType.Trim().ToLowerInvariant());
        cmd.Parameters.AddWithValue("$scope", targetScope);
        cmd.Parameters.AddWithValue("$inherit", inheritToChildren ? 1 : 0);
        cmd.Parameters.AddWithValue("$opts", optionsJson);
        cmd.Parameters.AddWithValue("$show", showOnCard ? 1 : 0);
        cmd.Parameters.AddWithValue("$order", sortOrder);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();

        return new CustomFieldDefinition(cleanKey, displayName.Trim(), fieldType, targetScope, inheritToChildren, options ?? [], showOnCard, sortOrder, now);
    }

    public void SetCustomFieldValue(
        string entityType,
        string entityId,
        string fieldKey,
        string valueText,
        double? valueNumber = null)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        SetEntityCustomFieldsInTransaction(
            conn,
            tx,
            entityType,
            entityId,
            [new CustomFieldValueInput(fieldKey, valueText, valueNumber)]);

        if (string.Equals(entityType, "book", StringComparison.OrdinalIgnoreCase))
        {
            RebuildFtsForBookInTransaction(conn, tx, entityId);
        }

        tx.Commit();
    }

    // =========================================================================
    // 6. Server-Side Paginated & FTS5 Search Query (24,000+ Books)
    // =========================================================================

    public PagedBookListResult ListBooks(BookListQuery query)
    {
        var sw = Stopwatch.StartNew();
        using var conn = _db.OpenConnection();

        var rawFolders = LoadAllFoldersRaw(conn);
        var folderLookup = rawFolders.ToDictionary(f => f.FolderId, StringComparer.Ordinal);
        var fieldDefs = LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = LoadAllCustomFieldValues(conn);

        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        // 1. Folder filter (direct or recursive subtree)
        if (query.UncategorizedOnly)
        {
            whereClauses.Add("b.folder_id IS NULL");
        }
        else if (!string.IsNullOrWhiteSpace(query.FolderId))
        {
            if (query.IncludeDescendants)
            {
                var subtreeIds = GetSubtreeFolderIds(query.FolderId, rawFolders);
                var paramNames = new List<string>(subtreeIds.Count);
                int idx = 0;
                foreach (string fid in subtreeIds)
                {
                    string pName = $"$fid_{idx++}";
                    paramNames.Add(pName);
                    parameters.Add(new SqliteParameter(pName, fid));
                }
                whereClauses.Add($"b.folder_id IN ({string.Join(", ", paramNames)})");
            }
            else
            {
                whereClauses.Add("b.folder_id = $folderId");
                parameters.Add(new SqliteParameter("$folderId", query.FolderId));
            }
        }

        // 2. Series filter
        if (!string.IsNullOrWhiteSpace(query.SeriesName))
        {
            whereClauses.Add("b.series_name = $seriesName");
            parameters.Add(new SqliteParameter("$seriesName", query.SeriesName));
        }

        // 3. Language filter
        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            whereClauses.Add("b.language = $lang");
            parameters.Add(new SqliteParameter("$lang", query.Language));
        }

        // 4. Multi-tag filter (AND / OR)
        if (query.TagIds is { Count: > 0 })
        {
            var tagParams = new List<string>(query.TagIds.Count);
            for (int i = 0; i < query.TagIds.Count; i++)
            {
                string pName = $"$tag_{i}";
                tagParams.Add(pName);
                parameters.Add(new SqliteParameter(pName, query.TagIds[i]));
            }

            if (string.Equals(query.TagMatchMode, "OR", StringComparison.OrdinalIgnoreCase))
            {
                whereClauses.Add($"EXISTS (SELECT 1 FROM book_tags bt WHERE bt.book_id = b.book_id AND bt.tag_id IN ({string.Join(", ", tagParams)}))");
            }
            else
            {
                whereClauses.Add($"(SELECT COUNT(DISTINCT bt.tag_id) FROM book_tags bt WHERE bt.book_id = b.book_id AND bt.tag_id IN ({string.Join(", ", tagParams)})) = {query.TagIds.Count}");
            }
        }

        // 5. Search filter (FTS5 + substring fallback for CJK mid-word queries)
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            string q = query.Search.Trim();
            string likePattern = $"%{q}%";
            parameters.Add(new SqliteParameter("$likeQ", likePattern));

            string ftsQuery = BuildSafeFtsPrefixQuery(q);
            if (!string.IsNullOrEmpty(ftsQuery))
            {
                parameters.Add(new SqliteParameter("$ftsQ", ftsQuery));
                whereClauses.Add("""
                    (b.book_id IN (SELECT book_id FROM books_fts WHERE books_fts MATCH $ftsQ)
                     OR b.title LIKE $likeQ
                     OR b.sort_title LIKE $likeQ
                     OR b.series_name LIKE $likeQ
                     OR b.authors LIKE $likeQ
                     OR b.isbn LIKE $likeQ
                     OR b.publisher LIKE $likeQ
                     OR b.imprint LIKE $likeQ)
                    """);
            }
            else
            {
                whereClauses.Add("""
                    (b.title LIKE $likeQ
                     OR b.sort_title LIKE $likeQ
                     OR b.series_name LIKE $likeQ
                     OR b.authors LIKE $likeQ
                     OR b.isbn LIKE $likeQ)
                    """);
            }
        }

        // 6. Custom Field Filters
        if (query.CustomFieldFilters is { Count: > 0 })
        {
            for (int i = 0; i < query.CustomFieldFilters.Count; i++)
            {
                var cf = query.CustomFieldFilters[i];
                string pKey = $"$cf_k_{i}";
                parameters.Add(new SqliteParameter(pKey, cf.FieldKey));

                switch (cf.Operator.ToLowerInvariant())
                {
                    case "lte":
                        parameters.Add(new SqliteParameter($"$cf_v_{i}", cf.ValueNumber ?? 0.0));
                        whereClauses.Add($"EXISTS (SELECT 1 FROM custom_field_values cfv WHERE cfv.entity_type = 'book' AND cfv.entity_id = b.book_id AND cfv.field_key = {pKey} AND cfv.value_number <= $cf_v_{i})");
                        break;
                    case "gte":
                        parameters.Add(new SqliteParameter($"$cf_v_{i}", cf.ValueNumber ?? 0.0));
                        whereClauses.Add($"EXISTS (SELECT 1 FROM custom_field_values cfv WHERE cfv.entity_type = 'book' AND cfv.entity_id = b.book_id AND cfv.field_key = {pKey} AND cfv.value_number >= $cf_v_{i})");
                        break;
                    case "eq":
                        if (cf.ValueNumber.HasValue)
                        {
                            parameters.Add(new SqliteParameter($"$cf_v_{i}", cf.ValueNumber.Value));
                            whereClauses.Add($"EXISTS (SELECT 1 FROM custom_field_values cfv WHERE cfv.entity_type = 'book' AND cfv.entity_id = b.book_id AND cfv.field_key = {pKey} AND cfv.value_number = $cf_v_{i})");
                        }
                        else
                        {
                            parameters.Add(new SqliteParameter($"$cf_v_{i}", cf.ValueText ?? ""));
                            whereClauses.Add($"EXISTS (SELECT 1 FROM custom_field_values cfv WHERE cfv.entity_type = 'book' AND cfv.entity_id = b.book_id AND cfv.field_key = {pKey} AND cfv.value_text = $cf_v_{i})");
                        }
                        break;
                    case "contains":
                        parameters.Add(new SqliteParameter($"$cf_v_{i}", $"%{cf.ValueText ?? ""}%"));
                        whereClauses.Add($"EXISTS (SELECT 1 FROM custom_field_values cfv WHERE cfv.entity_type = 'book' AND cfv.entity_id = b.book_id AND cfv.field_key = {pKey} AND cfv.value_text LIKE $cf_v_{i})");
                        break;
                    case "exists":
                        whereClauses.Add($"EXISTS (SELECT 1 FROM custom_field_values cfv WHERE cfv.entity_type = 'book' AND cfv.entity_id = b.book_id AND cfv.field_key = {pKey} AND cfv.value_text != '')");
                        break;
                }
            }
        }

        string whereSql = whereClauses.Count > 0
            ? "WHERE " + string.Join(" AND ", whereClauses)
            : "";

        string orderBySql = query.Sort.ToLowerInvariant() switch
        {
            "custom" => "ORDER BY b.custom_sort_order ASC, CASE WHEN b.series_index IS NULL THEN 1 ELSE 0 END ASC, b.series_index ASC, b.sort_title COLLATE NOCASE ASC",
            "title" => "ORDER BY b.sort_title COLLATE NOCASE ASC, b.series_index ASC",
            "author" => "ORDER BY b.author_sort COLLATE NOCASE ASC, b.series_name COLLATE NOCASE ASC, b.series_index ASC, b.sort_title COLLATE NOCASE ASC",
            "added" => "ORDER BY b.added_at_utc DESC, b.sort_title COLLATE NOCASE ASC",
            "last_read" => "ORDER BY CASE WHEN b.last_opened_at_utc IS NULL THEN 1 ELSE 0 END ASC, b.last_opened_at_utc DESC, b.sort_title COLLATE NOCASE ASC",
            "isbn" => "ORDER BY CASE WHEN b.isbn = '' THEN 1 ELSE 0 END ASC, b.isbn ASC, b.sort_title COLLATE NOCASE ASC",
            _ => "ORDER BY CASE WHEN b.series_name = '' THEN 1 ELSE 0 END ASC, b.series_name COLLATE NOCASE ASC, CASE WHEN b.series_index IS NULL THEN 1 ELSE 0 END ASC, b.series_index ASC, b.custom_sort_order ASC, b.sort_title COLLATE NOCASE ASC"
        };

        // Count total matching rows
        int totalCount;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM books b {whereSql};";
            foreach (var p in parameters)
            {
                countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            totalCount = Convert.ToInt32(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        int safeLimit = Math.Clamp(query.Limit <= 0 ? 100 : query.Limit, 1, 1000);
        int safeOffset = Math.Max(0, query.Offset);

        var items = new List<BookSummary>();
        using (var listCmd = conn.CreateCommand())
        {
            listCmd.CommandText = $"""
                SELECT b.book_id, b.epub_sha256, b.folder_id, b.title, b.sort_title,
                       b.series_name, b.series_index, b.custom_sort_order, b.authors, b.author_sort,
                       b.isbn, b.publisher, b.imprint, b.published_date, b.language, b.description,
                       b.is_metadata_edited, b.folder_path, b.epub_file_name, b.file_size_bytes,
                       b.cover_cache_path, b.chapter_count, b.total_char_count, b.stats_computed,
                       b.writing_mode, b.added_at_utc, b.updated_at_utc, b.last_opened_at_utc,
                       b.has_study_pack, COALESCE(rp.overall_completion, 0.0)
                FROM books b
                LEFT JOIN reading_progress rp ON rp.book_id = b.book_id
                {whereSql}
                {orderBySql}
                LIMIT $limit OFFSET $offset;
                """;
            foreach (var p in parameters)
            {
                listCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            listCmd.Parameters.AddWithValue("$limit", safeLimit);
            listCmd.Parameters.AddWithValue("$offset", safeOffset);

            var rawRows = new List<(string BookId, Func<IReadOnlyList<TagRecord>, IReadOnlyDictionary<string, EffectiveCustomFieldValue>, BookSummary> Builder, string? FolderId, string Title)>();

            using (var r = listCmd.ExecuteReader())
            {
                while (r.Read())
                {
                    string bookId = r.GetString(0);
                    string sha = r.GetString(1);
                    string? folderId = r.IsDBNull(2) ? null : r.GetString(2);
                    string title = r.GetString(3);
                    string sortTitle = r.GetString(4);
                    string seriesName = r.GetString(5);
                    double? seriesIdx = r.IsDBNull(6) ? null : r.GetDouble(6);
                    int customSort = r.GetInt32(7);
                    string authors = r.GetString(8);
                    string authorSort = r.GetString(9);
                    string isbn = r.GetString(10);
                    string publisher = r.GetString(11);
                    string imprint = r.GetString(12);
                    string pubDate = r.GetString(13);
                    string lang = r.GetString(14);
                    string desc = r.GetString(15);
                    bool edited = r.GetInt32(16) != 0;
                    string folderPath = r.GetString(17);
                    string epubFile = r.GetString(18);
                    long fileSize = r.GetInt64(19);
                    string? cover = r.IsDBNull(20) ? null : r.GetString(20);
                    int chCount = r.GetInt32(21);
                    int charCount = r.GetInt32(22);
                    bool statsComp = r.GetInt32(23) != 0;
                    string writingMode = r.GetString(24);
                    string addedAt = r.GetString(25);
                    string updatedAt = r.GetString(26);
                    string? lastOpened = r.IsDBNull(27) ? null : r.GetString(27);
                    bool hasPack = r.GetInt32(28) != 0;
                    double completion = r.GetDouble(29);

                    string? breadcrumb = BuildFolderBreadcrumb(folderId, folderLookup);

                    rawRows.Add((
                        bookId,
                        (tags, cfMap) => new BookSummary(
                            BookId: bookId,
                            EpubSha256: sha,
                            FolderId: folderId,
                            FolderPathBreadcrumb: breadcrumb,
                            Title: title,
                            SortTitle: sortTitle,
                            SeriesName: seriesName,
                            SeriesIndex: seriesIdx,
                            CustomSortOrder: customSort,
                            Authors: authors,
                            AuthorSort: authorSort,
                            Isbn: isbn,
                            Publisher: publisher,
                            Imprint: imprint,
                            PublishedDate: pubDate,
                            Language: lang,
                            Description: desc,
                            IsMetadataEdited: edited,
                            FolderPath: folderPath,
                            EpubFileName: epubFile,
                            FileSizeBytes: fileSize,
                            CoverCachePath: cover,
                            ChapterCount: chCount,
                            TotalCharCount: charCount,
                            StatsComputed: statsComp,
                            WritingMode: writingMode,
                            AddedAtUtc: addedAt,
                            UpdatedAtUtc: updatedAt,
                            LastOpenedAtUtc: lastOpened,
                            HasStudyPack: hasPack,
                            OverallCompletion: completion,
                            Tags: tags,
                            CustomFields: cfMap),
                        folderId,
                        title));
                }
            }

            if (rawRows.Count > 0)
            {
                var pageBookIds = rawRows.Select(x => x.BookId).ToList();
                var tagsByBook = LoadTagsForBooks(conn, pageBookIds);

                foreach (var row in rawRows)
                {
                    var bTags = tagsByBook.GetValueOrDefault(row.BookId) ?? [];
                    var effFields = ResolveEffectiveCustomFieldsForBook(
                        row.BookId,
                        row.Title,
                        row.FolderId,
                        folderLookup,
                        fieldDefs,
                        allCustomValues);
                    var cfDict = effFields.ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase);
                    items.Add(row.Builder(bTags, cfDict));
                }
            }
        }

        sw.Stop();
        return new PagedBookListResult(
            Items: items,
            TotalCount: totalCount,
            Offset: safeOffset,
            Limit: safeLimit,
            QueryElapsedMs: Math.Round(sw.Elapsed.TotalMilliseconds, 2));
    }

    public BookDetail? GetBookDetail(string bookId)
    {
        using var conn = _db.OpenConnection();
        var page = ListBooksBySpecificId(conn, bookId);
        if (page is null) return null;

        var rawFolders = LoadAllFoldersRaw(conn).ToDictionary(f => f.FolderId, StringComparer.Ordinal);
        var fieldDefs = LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = LoadAllCustomFieldValues(conn);

        string opfJson = "{}";
        using (var opfCmd = conn.CreateCommand())
        {
            opfCmd.CommandText = "SELECT opf_original_json FROM books WHERE book_id = $id;";
            opfCmd.Parameters.AddWithValue("$id", bookId);
            if (opfCmd.ExecuteScalar() is string s) opfJson = s;
        }

        var opfOriginal = JsonSerializer.Deserialize<OpfOriginalMetadata>(opfJson) ?? new OpfOriginalMetadata();

        var chapters = new List<BookChapterRecord>();
        using (var chCmd = conn.CreateCommand())
        {
            chCmd.CommandText = """
                SELECT chapter_index, chapter_id, title, spine_href, fragment_id,
                       end_spine_href, end_fragment_id, char_count, cumulative_chars, is_excluded
                FROM book_chapters
                WHERE book_id = $id
                ORDER BY chapter_index ASC;
                """;
            chCmd.Parameters.AddWithValue("$id", bookId);
            using var r = chCmd.ExecuteReader();
            while (r.Read())
            {
                chapters.Add(new BookChapterRecord(
                    BookId: bookId,
                    ChapterIndex: r.GetInt32(0),
                    ChapterId: r.GetString(1),
                    Title: r.GetString(2),
                    SpineHref: r.GetString(3),
                    FragmentId: r.GetString(4),
                    EndSpineHref: r.IsDBNull(5) ? null : r.GetString(5),
                    EndFragmentId: r.IsDBNull(6) ? null : r.GetString(6),
                    CharCount: r.GetInt32(7),
                    CumulativeChars: r.GetInt32(8),
                    IsExcluded: r.GetInt32(9) != 0));
            }
        }

        var effList = ResolveEffectiveCustomFieldsForBook(
            bookId,
            page.Title,
            page.FolderId,
            rawFolders,
            fieldDefs,
            allCustomValues);

        var directMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (allCustomValues.TryGetValue(("book", bookId), out var directVals))
        {
            foreach (var kv in directVals)
            {
                directMap[kv.Key] = kv.Value.ValueText;
            }
        }

        return new BookDetail(
            Summary: page,
            OpfOriginal: opfOriginal,
            Chapters: chapters,
            EffectiveCustomFields: effList,
            DirectBookCustomFields: directMap);
    }

    public void DeleteBook(string bookId)
    {
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var delFts = conn.CreateCommand())
        {
            delFts.Transaction = tx;
            delFts.CommandText = "DELETE FROM books_fts WHERE book_id = $id;";
            delFts.Parameters.AddWithValue("$id", bookId);
            delFts.ExecuteNonQuery();
        }

        using (var delCf = conn.CreateCommand())
        {
            delCf.Transaction = tx;
            delCf.CommandText = "DELETE FROM custom_field_values WHERE entity_type = 'book' AND entity_id = $id;";
            delCf.Parameters.AddWithValue("$id", bookId);
            delCf.ExecuteNonQuery();
        }

        using (var delB = conn.CreateCommand())
        {
            delB.Transaction = tx;
            delB.CommandText = "DELETE FROM books WHERE book_id = $id;";
            delB.Parameters.AddWithValue("$id", bookId);
            delB.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public int BulkDeleteBooks(IReadOnlyList<string> bookIds)
    {
        if (bookIds is null || bookIds.Count == 0) return 0;
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();

        int count = 0;
        foreach (string bookId in bookIds)
        {
            using (var delFts = conn.CreateCommand())
            {
                delFts.Transaction = tx;
                delFts.CommandText = "DELETE FROM books_fts WHERE book_id = $id;";
                delFts.Parameters.AddWithValue("$id", bookId);
                delFts.ExecuteNonQuery();
            }

            using (var delCf = conn.CreateCommand())
            {
                delCf.Transaction = tx;
                delCf.CommandText = "DELETE FROM custom_field_values WHERE entity_type = 'book' AND entity_id = $id;";
                delCf.Parameters.AddWithValue("$id", bookId);
                delCf.ExecuteNonQuery();
            }

            using (var delB = conn.CreateCommand())
            {
                delB.Transaction = tx;
                delB.CommandText = "DELETE FROM books WHERE book_id = $id;";
                delB.Parameters.AddWithValue("$id", bookId);
                count += delB.ExecuteNonQuery();
            }
        }

        tx.Commit();
        return count;
    }

    public IReadOnlyList<string> PruneEmptyFolders()
    {
        using var conn = _db.OpenConnection();
        var prunedNames = new List<string>();

        while (true)
        {
            var leafEmpty = new List<(string Id, string Name)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT f.folder_id, f.name
                    FROM library_folders f
                    WHERE (SELECT COUNT(*) FROM books b WHERE b.folder_id = f.folder_id) = 0
                      AND (SELECT COUNT(*) FROM library_folders c WHERE c.parent_folder_id = f.folder_id) = 0;
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    leafEmpty.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            if (leafEmpty.Count == 0) break;

            using var tx = conn.BeginTransaction();
            foreach (var (fId, name) in leafEmpty)
            {
                using var delCmd = conn.CreateCommand();
                delCmd.Transaction = tx;
                delCmd.CommandText = "DELETE FROM library_folders WHERE folder_id = $id;";
                delCmd.Parameters.AddWithValue("$id", fId);
                delCmd.ExecuteNonQuery();
                prunedNames.Add(name);
            }
            tx.Commit();
        }

        return prunedNames;
    }

    public bool MoveFolder(string folderId, string? newParentFolderId, int? newSortOrder = null)
    {
        if (string.IsNullOrWhiteSpace(folderId)) return false;
        if (folderId == newParentFolderId) return false;

        using var conn = _db.OpenConnection();
        if (!string.IsNullOrWhiteSpace(newParentFolderId))
        {
            string? curr = newParentFolderId;
            while (!string.IsNullOrWhiteSpace(curr))
            {
                if (curr == folderId) return false; // Cycle detected
                using var pCmd = conn.CreateCommand();
                pCmd.CommandText = "SELECT parent_folder_id FROM library_folders WHERE folder_id = $id;";
                pCmd.Parameters.AddWithValue("$id", curr);
                curr = pCmd.ExecuteScalar() as string;
            }
        }

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE library_folders
                SET parent_folder_id = $parent,
                    sort_order = COALESCE($order, sort_order)
                WHERE folder_id = $id;
                """;
            cmd.Parameters.AddWithValue("$parent", (object?)newParentFolderId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$order", (object?)newSortOrder ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", folderId);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return true;
    }

    public void ReorderBooks(IReadOnlyList<string> orderedBookIds)
    {
        if (orderedBookIds is null || orderedBookIds.Count == 0) return;
        using var conn = _db.OpenConnection();
        using var tx = conn.BeginTransaction();
        string now = DateTime.UtcNow.ToString("O");

        for (int i = 0; i < orderedBookIds.Count; i++)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE books SET custom_sort_order = $order, updated_at_utc = $now WHERE book_id = $id;";
            cmd.Parameters.AddWithValue("$order", i + 1);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.Parameters.AddWithValue("$id", orderedBookIds[i]);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // =========================================================================
    // Internal Helpers: Custom Field Inheritance, FTS5 Sync & Folder Trees
    // =========================================================================

    private BookSummary? ListBooksBySpecificId(SqliteConnection conn, string bookId)
    {
        var rawFolders = LoadAllFoldersRaw(conn).ToDictionary(f => f.FolderId, StringComparer.Ordinal);
        var fieldDefs = LoadCustomFieldDefinitionsInternal(conn);
        var allCustomValues = LoadAllCustomFieldValues(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.book_id, b.epub_sha256, b.folder_id, b.title, b.sort_title,
                   b.series_name, b.series_index, b.custom_sort_order, b.authors, b.author_sort,
                   b.isbn, b.publisher, b.imprint, b.published_date, b.language, b.description,
                   b.is_metadata_edited, b.folder_path, b.epub_file_name, b.file_size_bytes,
                   b.cover_cache_path, b.chapter_count, b.total_char_count, b.stats_computed,
                   b.writing_mode, b.added_at_utc, b.updated_at_utc, b.last_opened_at_utc,
                   b.has_study_pack, COALESCE(rp.overall_completion, 0.0)
            FROM books b
            LEFT JOIN reading_progress rp ON rp.book_id = b.book_id
            WHERE b.book_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", bookId);

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        string? folderId = r.IsDBNull(2) ? null : r.GetString(2);
        string title = r.GetString(3);
        string? breadcrumb = BuildFolderBreadcrumb(folderId, rawFolders);
        var tags = LoadTagsForBooks(conn, [bookId]).GetValueOrDefault(bookId) ?? [];
        var effFields = ResolveEffectiveCustomFieldsForBook(bookId, title, folderId, rawFolders, fieldDefs, allCustomValues);

        return new BookSummary(
            BookId: r.GetString(0),
            EpubSha256: r.GetString(1),
            FolderId: folderId,
            FolderPathBreadcrumb: breadcrumb,
            Title: title,
            SortTitle: r.GetString(4),
            SeriesName: r.GetString(5),
            SeriesIndex: r.IsDBNull(6) ? null : r.GetDouble(6),
            CustomSortOrder: r.GetInt32(7),
            Authors: r.GetString(8),
            AuthorSort: r.GetString(9),
            Isbn: r.GetString(10),
            Publisher: r.GetString(11),
            Imprint: r.GetString(12),
            PublishedDate: r.GetString(13),
            Language: r.GetString(14),
            Description: r.GetString(15),
            IsMetadataEdited: r.GetInt32(16) != 0,
            FolderPath: r.GetString(17),
            EpubFileName: r.GetString(18),
            FileSizeBytes: r.GetInt64(19),
            CoverCachePath: r.IsDBNull(20) ? null : r.GetString(20),
            ChapterCount: r.GetInt32(21),
            TotalCharCount: r.GetInt32(22),
            StatsComputed: r.GetInt32(23) != 0,
            WritingMode: r.GetString(24),
            AddedAtUtc: r.GetString(25),
            UpdatedAtUtc: r.GetString(26),
            LastOpenedAtUtc: r.IsDBNull(27) ? null : r.GetString(27),
            HasStudyPack: r.GetInt32(28) != 0,
            OverallCompletion: r.GetDouble(29),
            Tags: tags,
            CustomFields: effFields.ToDictionary(f => f.FieldKey, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<EffectiveCustomFieldValue> ResolveEffectiveCustomFieldsForBook(
        string bookId,
        string bookTitle,
        string? folderId,
        Dictionary<string, RawFolderRow> folderLookup,
        IReadOnlyList<CustomFieldDefinition> definitions,
        Dictionary<(string EntityType, string EntityId), Dictionary<string, (string ValueText, double? ValueNumber)>> allValues)
    {
        var result = new List<EffectiveCustomFieldValue>();
        allValues.TryGetValue(("book", bookId), out var directBookVals);

        foreach (var def in definitions)
        {
            // 1. Check direct value on the book itself
            if (directBookVals is not null &&
                directBookVals.TryGetValue(def.FieldKey, out var directVal) &&
                (!string.IsNullOrWhiteSpace(directVal.ValueText) || directVal.ValueNumber.HasValue))
            {
                result.Add(new EffectiveCustomFieldValue(
                    FieldKey: def.FieldKey,
                    DisplayName: def.DisplayName,
                    FieldType: def.FieldType,
                    EffectiveValueText: directVal.ValueText,
                    EffectiveValueNumber: directVal.ValueNumber,
                    IsInherited: false,
                    SourceEntityType: "book",
                    SourceEntityId: bookId,
                    SourceEntityName: bookTitle));
                continue;
            }

            // 2. If InheritToChildren is enabled, walk up the folder ancestor chain
            if (def.InheritToChildren && !string.IsNullOrEmpty(folderId))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal);
                string? curFolderId = folderId;
                while (!string.IsNullOrEmpty(curFolderId) &&
                       visited.Add(curFolderId) &&
                       folderLookup.TryGetValue(curFolderId, out var folderRow))
                {
                    if (allValues.TryGetValue(("folder", curFolderId), out var fVals) &&
                        fVals.TryGetValue(def.FieldKey, out var fVal) &&
                        (!string.IsNullOrWhiteSpace(fVal.ValueText) || fVal.ValueNumber.HasValue))
                    {
                        result.Add(new EffectiveCustomFieldValue(
                            FieldKey: def.FieldKey,
                            DisplayName: def.DisplayName,
                            FieldType: def.FieldType,
                            EffectiveValueText: fVal.ValueText,
                            EffectiveValueNumber: fVal.ValueNumber,
                            IsInherited: true,
                            SourceEntityType: "folder",
                            SourceEntityId: curFolderId,
                            SourceEntityName: folderRow.Name));
                        break;
                    }
                    curFolderId = folderRow.ParentFolderId;
                }
            }
        }

        return result;
    }

    private static IReadOnlyList<EffectiveCustomFieldValue> ResolveEffectiveCustomFieldsForFolder(
        string folderId,
        Dictionary<string, RawFolderRow> folderLookup,
        IReadOnlyList<CustomFieldDefinition> definitions,
        Dictionary<(string EntityType, string EntityId), Dictionary<string, (string ValueText, double? ValueNumber)>> allValues)
    {
        var result = new List<EffectiveCustomFieldValue>();
        if (!folderLookup.TryGetValue(folderId, out var selfFolder)) return result;

        foreach (var def in definitions)
        {
            if (allValues.TryGetValue(("folder", folderId), out var directVals) &&
                directVals.TryGetValue(def.FieldKey, out var dVal) &&
                (!string.IsNullOrWhiteSpace(dVal.ValueText) || dVal.ValueNumber.HasValue))
            {
                result.Add(new EffectiveCustomFieldValue(
                    FieldKey: def.FieldKey,
                    DisplayName: def.DisplayName,
                    FieldType: def.FieldType,
                    EffectiveValueText: dVal.ValueText,
                    EffectiveValueNumber: dVal.ValueNumber,
                    IsInherited: false,
                    SourceEntityType: "folder",
                    SourceEntityId: folderId,
                    SourceEntityName: selfFolder.Name));
                continue;
            }

            if (def.InheritToChildren && !string.IsNullOrEmpty(selfFolder.ParentFolderId))
            {
                var visited = new HashSet<string>(StringComparer.Ordinal) { folderId };
                string? curId = selfFolder.ParentFolderId;
                while (!string.IsNullOrEmpty(curId) &&
                       visited.Add(curId) &&
                       folderLookup.TryGetValue(curId, out var ancRow))
                {
                    if (allValues.TryGetValue(("folder", curId), out var ancVals) &&
                        ancVals.TryGetValue(def.FieldKey, out var aVal) &&
                        (!string.IsNullOrWhiteSpace(aVal.ValueText) || aVal.ValueNumber.HasValue))
                    {
                        result.Add(new EffectiveCustomFieldValue(
                            FieldKey: def.FieldKey,
                            DisplayName: def.DisplayName,
                            FieldType: def.FieldType,
                            EffectiveValueText: aVal.ValueText,
                            EffectiveValueNumber: aVal.ValueNumber,
                            IsInherited: true,
                            SourceEntityType: "folder",
                            SourceEntityId: curId,
                            SourceEntityName: ancRow.Name));
                        break;
                    }
                    curId = ancRow.ParentFolderId;
                }
            }
        }

        return result;
    }

    private static string? BuildFolderBreadcrumb(string? folderId, Dictionary<string, RawFolderRow> folderLookup)
    {
        if (string.IsNullOrEmpty(folderId) || !folderLookup.ContainsKey(folderId)) return null;
        var parts = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? cur = folderId;
        while (!string.IsNullOrEmpty(cur) && visited.Add(cur) && folderLookup.TryGetValue(cur, out var row))
        {
            parts.Add(row.Name);
            cur = row.ParentFolderId;
        }
        parts.Reverse();
        return string.Join(" / ", parts);
    }

    private static HashSet<string> GetSubtreeFolderIds(string rootFolderId, List<RawFolderRow> allFolders)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { rootFolderId };
        bool added = true;
        while (added)
        {
            added = false;
            foreach (var f in allFolders)
            {
                if (f.ParentFolderId is not null && result.Contains(f.ParentFolderId) && result.Add(f.FolderId))
                {
                    added = true;
                }
            }
        }
        return result;
    }

    private static string BuildSafeFtsPrefixQuery(string rawSearch)
    {
        var tokens = rawSearch
            .Split([' ', '\t', '　', '"', '\'', '*', ':', '-', '(', ')', '[', ']', '{', '}'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "";
        return string.Join(" AND ", tokens.Select(t => $"\"{t}\"*"));
    }

    private static void SetBookTagsInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string bookId,
        IReadOnlyList<string> tagIds)
    {
        string now = DateTime.UtcNow.ToString("O");
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM book_tags WHERE book_id = $b;";
            del.Parameters.AddWithValue("$b", bookId);
            del.ExecuteNonQuery();
        }

        foreach (string tid in tagIds)
        {
            using var ins = conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO book_tags (book_id, tag_id, assigned_at_utc) VALUES ($b, $t, $now);";
            ins.Parameters.AddWithValue("$b", bookId);
            ins.Parameters.AddWithValue("$t", tid);
            ins.Parameters.AddWithValue("$now", now);
            ins.ExecuteNonQuery();
        }
    }

    private static void SetEntityCustomFieldsInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string entityType,
        string entityId,
        IReadOnlyList<CustomFieldValueInput> fields)
    {
        string now = DateTime.UtcNow.ToString("O");
        foreach (var f in fields)
        {
            if (string.IsNullOrWhiteSpace(f.ValueText) && !f.ValueNumber.HasValue)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM custom_field_values WHERE entity_type = $et AND entity_id = $eid AND field_key = $k;";
                del.Parameters.AddWithValue("$et", entityType);
                del.Parameters.AddWithValue("$eid", entityId);
                del.Parameters.AddWithValue("$k", f.FieldKey);
                del.ExecuteNonQuery();
                continue;
            }

            double? numVal = f.ValueNumber;
            if (!numVal.HasValue && double.TryParse(f.ValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                numVal = parsed;
            }

            string textVal = !string.IsNullOrWhiteSpace(f.ValueText)
                ? f.ValueText.Trim()
                : (numVal?.ToString(CultureInfo.InvariantCulture) ?? "");

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO custom_field_values (entity_type, entity_id, field_key, value_text, value_number, updated_at_utc)
                VALUES ($et, $eid, $k, $vt, $vn, $now)
                ON CONFLICT(entity_type, entity_id, field_key) DO UPDATE SET
                    value_text = excluded.value_text,
                    value_number = excluded.value_number,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$et", entityType);
            cmd.Parameters.AddWithValue("$eid", entityId);
            cmd.Parameters.AddWithValue("$k", f.FieldKey);
            cmd.Parameters.AddWithValue("$vt", textVal);
            cmd.Parameters.AddWithValue("$vn", (object?)numVal ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
    }

    private static void RebuildFtsForBookInTransaction(
        SqliteConnection conn,
        SqliteTransaction tx,
        string bookId)
    {
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM books_fts WHERE book_id = $id;";
            del.Parameters.AddWithValue("$id", bookId);
            del.ExecuteNonQuery();
        }

        string customText = "";
        using (var cfCmd = conn.CreateCommand())
        {
            cfCmd.Transaction = tx;
            cfCmd.CommandText = "SELECT value_text FROM custom_field_values WHERE entity_type = 'book' AND entity_id = $id;";
            cfCmd.Parameters.AddWithValue("$id", bookId);
            var parts = new List<string>();
            using var r = cfCmd.ExecuteReader();
            while (r.Read())
            {
                parts.Add(r.GetString(0));
            }
            customText = string.Join(' ', parts);
        }

        using (var ins = conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO books_fts (
                    book_id, title, sort_title, series_name, authors, author_sort, publisher, imprint, isbn, description, custom_fields_text
                )
                SELECT book_id, title, sort_title, series_name, authors, author_sort, publisher, imprint, isbn, description, $cfText
                FROM books
                WHERE book_id = $id;
                """;
            ins.Parameters.AddWithValue("$id", bookId);
            ins.Parameters.AddWithValue("$cfText", customText);
            ins.ExecuteNonQuery();
        }
    }

    private sealed record RawFolderRow(
        string FolderId,
        string? ParentFolderId,
        string Name,
        string FolderKind,
        string Description,
        string Icon,
        string? ColorHex,
        int SortOrder,
        string DefaultBookSort
    );

    private static List<RawFolderRow> LoadAllFoldersRaw(SqliteConnection conn)
    {
        var list = new List<RawFolderRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT folder_id, parent_folder_id, name, folder_kind, description, icon, color_hex, sort_order, default_book_sort
            FROM library_folders
            ORDER BY sort_order ASC, name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new RawFolderRow(
                FolderId: r.GetString(0),
                ParentFolderId: r.IsDBNull(1) ? null : r.GetString(1),
                Name: r.GetString(2),
                FolderKind: r.GetString(3),
                Description: r.GetString(4),
                Icon: r.GetString(5),
                ColorHex: r.IsDBNull(6) ? null : r.GetString(6),
                SortOrder: r.GetInt32(7),
                DefaultBookSort: r.GetString(8)));
        }
        return list;
    }

    private static Dictionary<string, int> LoadDirectBookCountsPerFolder(SqliteConnection conn)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT folder_id, COUNT(*) FROM books WHERE folder_id IS NOT NULL GROUP BY folder_id;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            map[r.GetString(0)] = r.GetInt32(1);
        }
        return map;
    }

    private static Dictionary<string, List<TagRecord>> LoadAllFolderTags(SqliteConnection conn)
    {
        var map = new Dictionary<string, List<TagRecord>>(StringComparer.Ordinal);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ft.folder_id, t.tag_id, t.name, t.color_hex, t.sort_order, t.created_at_utc
            FROM folder_tags ft
            JOIN tags t ON t.tag_id = ft.tag_id
            ORDER BY t.sort_order ASC, t.name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string fid = r.GetString(0);
            if (!map.TryGetValue(fid, out var list))
            {
                list = [];
                map[fid] = list;
            }
            list.Add(new TagRecord(r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), 0, r.GetString(5)));
        }
        return map;
    }

    private static Dictionary<string, List<TagRecord>> LoadTagsForBooks(SqliteConnection conn, IReadOnlyList<string> bookIds)
    {
        var map = new Dictionary<string, List<TagRecord>>(StringComparer.Ordinal);
        if (bookIds.Count == 0) return map;

        using var cmd = conn.CreateCommand();
        var pNames = new List<string>(bookIds.Count);
        for (int i = 0; i < bookIds.Count; i++)
        {
            string p = $"$b_{i}";
            pNames.Add(p);
            cmd.Parameters.AddWithValue(p, bookIds[i]);
        }

        cmd.CommandText = $"""
            SELECT bt.book_id, t.tag_id, t.name, t.color_hex, t.sort_order, t.created_at_utc
            FROM book_tags bt
            JOIN tags t ON t.tag_id = bt.tag_id
            WHERE bt.book_id IN ({string.Join(", ", pNames)})
            ORDER BY t.sort_order ASC, t.name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string bid = r.GetString(0);
            if (!map.TryGetValue(bid, out var list))
            {
                list = [];
                map[bid] = list;
            }
            list.Add(new TagRecord(r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), 0, r.GetString(5)));
        }
        return map;
    }

    private static List<CustomFieldDefinition> LoadCustomFieldDefinitionsInternal(SqliteConnection conn)
    {
        var list = new List<CustomFieldDefinition>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT field_key, display_name, field_type, target_scope, inherit_to_children,
                   options_json, show_on_card, sort_order, created_at_utc
            FROM custom_field_definitions
            ORDER BY sort_order ASC, display_name COLLATE NOCASE ASC;
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var opts = JsonSerializer.Deserialize<List<string>>(r.GetString(5)) ?? [];
            list.Add(new CustomFieldDefinition(
                FieldKey: r.GetString(0),
                DisplayName: r.GetString(1),
                FieldType: r.GetString(2),
                TargetScope: r.GetString(3),
                InheritToChildren: r.GetInt32(4) != 0,
                Options: opts,
                ShowOnCard: r.GetInt32(6) != 0,
                SortOrder: r.GetInt32(7),
                CreatedAtUtc: r.GetString(8)));
        }
        return list;
    }

    private static Dictionary<(string EntityType, string EntityId), Dictionary<string, (string ValueText, double? ValueNumber)>> LoadAllCustomFieldValues(SqliteConnection conn)
    {
        var map = new Dictionary<(string, string), Dictionary<string, (string, double?)>>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entity_type, entity_id, field_key, value_text, value_number FROM custom_field_values;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var key = (r.GetString(0), r.GetString(1));
            if (!map.TryGetValue(key, out var inner))
            {
                inner = new Dictionary<string, (string, double?)>(StringComparer.OrdinalIgnoreCase);
                map[key] = inner;
            }
            string fk = r.GetString(2);
            string vt = r.GetString(3);
            double? vn = r.IsDBNull(4) ? null : r.GetDouble(4);
            inner[fk] = (vt, vn);
        }
        return map;
    }
}

