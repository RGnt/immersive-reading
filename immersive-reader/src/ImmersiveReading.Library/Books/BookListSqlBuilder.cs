using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.Folders;
using ImmersiveReading.Library.Internal;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

internal static class BookListSqlBuilder
{
    public static (string WhereSql, string OrderBySql, List<SqliteParameter> Parameters) Build(
        BookListQuery query,
        List<RawFolderRow> rawFolders)
    {
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
                var subtreeIds = FolderService.GetSubtreeFolderIds(query.FolderId, rawFolders);
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

            string ftsQuery = FtsIndexer.BuildSafeFtsPrefixQuery(q);
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

        return (whereSql, orderBySql, parameters);
    }
}

