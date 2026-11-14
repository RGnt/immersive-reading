using System.Text.Json;
using ImmersiveReading.Core.Library;
using Microsoft.Data.Sqlite;

namespace ImmersiveReading.Library.Books;

/// <summary>Writes a book's chapter rows. Shared by import and the chapter model upgrader.</summary>
internal static class ChapterStore
{
    public static void ReplaceChapters(
        SqliteConnection conn,
        SqliteTransaction tx,
        string bookId,
        IReadOnlyList<BookChapterRecord> chapters)
    {
        using (var delCh = conn.CreateCommand())
        {
            delCh.Transaction = tx;
            delCh.CommandText = "DELETE FROM book_chapters WHERE book_id = $id;";
            delCh.Parameters.AddWithValue("$id", bookId);
            delCh.ExecuteNonQuery();
        }

        if (chapters.Count == 0) return;

        using var chCmd = conn.CreateCommand();
        chCmd.Transaction = tx;
        chCmd.CommandText = """
            INSERT INTO book_chapters (
                book_id, chapter_index, chapter_id, title, spine_href, fragment_id,
                end_spine_href, end_fragment_id, char_count, cumulative_chars, is_excluded,
                member_spine_hrefs
            ) VALUES (
                $bookId, $idx, $chId, $title, $href, $frag,
                $endHref, $endFrag, $chars, $cumChars, $excl,
                $members
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
        var pMembers = chCmd.Parameters.Add("$members", SqliteType.Text);

        pBookId.Value = bookId;
        foreach (var ch in chapters)
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
            pMembers.Value = JsonSerializer.Serialize(ch.Members);
            chCmd.ExecuteNonQuery();
        }
    }
}
