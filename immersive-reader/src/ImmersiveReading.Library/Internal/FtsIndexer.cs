using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Internal;

internal static class FtsIndexer
{
    public static string BuildSafeFtsPrefixQuery(string rawSearch)
    {
        var tokens = rawSearch
            .Split([' ', '\t', '　', '"', '\'', '*', ':', '-', '(', ')', '[', ']', '{', '}'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return "";
        return string.Join(" AND ", tokens.Select(t => $"\"{t}\"*"));
    }

    public static void RebuildFtsForBookInTransaction(
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
}

