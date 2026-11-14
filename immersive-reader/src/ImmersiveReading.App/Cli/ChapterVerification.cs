using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmersiveReading.App.Composition;
using ImmersiveReading.Core.Epub;

namespace ImmersiveReading.App.Cli;

/// <summary>
/// <c>--verify-chapters</c> (spec 12 NFR-3): dry run. Rebuilds the chapter list of every book in the library
/// with the current builder and compares it to what is stored, WITHOUT writing anything (it deliberately
/// never calls <c>LibraryManager.GetBookDetail</c>, which would trigger the lazy upgrade).
/// Invariant checked per book: the set of spine files covered must be identical before and after.
/// </summary>
public static partial class ChapterVerification
{
    [GeneratedRegex(@"\((Part \d+|Illustration)\)|^Illustration \d+$")]
    private static partial Regex GeneratedTitle();

    private const int BigChapterFiles = 40;

    private sealed record BookRow(string BookId, string Title, string Path, string FileName);
    private sealed record Result(string BookId, string Title, int Before, int After, int GeneratedBefore, int GeneratedAfter,
        int MaxMembers, int BigChapters, string? Failure, string? CoverageProblem);

    public static int Run(AppServices services)
    {
        var books = new List<BookRow>();
        using (var conn = services.Db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT book_id, title, folder_path, epub_file_name FROM books ORDER BY book_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) books.Add(new BookRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        var results = new ConcurrentBag<Result>();
        int done = 0;
        Parallel.ForEach(books, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, book =>
        {
            results.Add(Verify(services, book));
            int n = Interlocked.Increment(ref done);
            if (n % 250 == 0) Console.Error.WriteLine($"  ... {n}/{books.Count}");
        });

        var all = results.ToList();
        var failed = all.Where(r => r.Failure is not null).ToList();
        var mismatched = all.Where(r => r.CoverageProblem is not null).ToList();
        var ok = all.Where(r => r.Failure is null).ToList();

        Console.WriteLine($"chapters before={ok.Sum(r => r.Before)} after={ok.Sum(r => r.After)}; " +
                          $"generated (Part/Illustration) before={ok.Sum(r => r.GeneratedBefore)} after={ok.Sum(r => r.GeneratedAfter)}; " +
                          $"largest chapter has {ok.Max(r => (int?)r.MaxMembers) ?? 0} files");
        foreach (var r in ok.OrderByDescending(r => r.Before - r.After).Take(5))
        {
            Console.WriteLine($"  biggest reduction: {r.Before} -> {r.After}  {r.Title}");
        }
        var big = ok.Where(r => r.BigChapters > 0).OrderByDescending(r => r.MaxMembers).ToList();
        Console.WriteLine($"books with a chapter of more than {BigChapterFiles} files: {big.Count}");
        foreach (var r in big.Take(8)) Console.WriteLine($"  {r.MaxMembers} files in one chapter ({r.BigChapters} big): {r.Title}");
        foreach (var r in failed.Take(5)) Console.WriteLine($"  FAILED {r.Title}: {r.Failure}");
        foreach (var r in mismatched.Take(5)) Console.WriteLine($"  COVERAGE {r.Title}: {r.CoverageProblem}");

        bool passed = mismatched.Count == 0 && failed.Count <= Math.Max(1, books.Count / 100);
        string summary = $"books={books.Count}, verified={ok.Count}, unreadable={failed.Count}, coverageMismatches={mismatched.Count}";
        Console.WriteLine(passed ? $"VERIFY_CHAPTERS_OK: {summary}" : $"VERIFY_CHAPTERS_FAIL: {summary}");
        return passed ? 0 : 1;
    }

    private static Result Verify(AppServices services, BookRow book)
    {
        var oldTitles = new List<string>();
        var oldCovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var conn = services.Db.OpenConnection())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT title, spine_href, member_spine_hrefs FROM book_chapters WHERE book_id = $id ORDER BY chapter_index;";
            cmd.Parameters.AddWithValue("$id", book.BookId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                oldTitles.Add(r.GetString(0));
                oldCovered.Add(r.GetString(1));
                var members = r.GetString(2);
                if (members != "[]")
                {
                    foreach (var m in JsonSerializer.Deserialize<string[]>(members) ?? []) oldCovered.Add(m);
                }
            }
        }

        string epub = System.IO.Path.Combine(book.Path, book.FileName);
        try
        {
            if (!File.Exists(epub)) return new Result(book.BookId, book.Title, oldTitles.Count, 0, 0, 0, 0, 0, "EPUB file missing", null);

            ExtractedEpubPackage pkg;
            using (var fs = new FileStream(epub, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                pkg = EpubMetadataExtractor.ExtractFromStream(fs, book.FileName, computeDeepChapterStats: false);
            }

            var newCovered = pkg.Chapters.SelectMany(c => c.Members).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string? coverage = null;
            if (!newCovered.SetEquals(oldCovered))
            {
                var missing = oldCovered.Except(newCovered, StringComparer.OrdinalIgnoreCase).Take(2);
                var extra = newCovered.Except(oldCovered, StringComparer.OrdinalIgnoreCase).Take(2);
                coverage = $"missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}]";
            }

            return new Result(
                book.BookId, book.Title, oldTitles.Count, pkg.Chapters.Count,
                oldTitles.Count(t => GeneratedTitle().IsMatch(t)),
                pkg.Chapters.Count(c => GeneratedTitle().IsMatch(c.Title)),
                pkg.Chapters.Count == 0 ? 0 : pkg.Chapters.Max(c => c.Members.Count),
                pkg.Chapters.Count(c => c.Members.Count > BigChapterFiles),
                null, coverage);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new Result(book.BookId, book.Title, oldTitles.Count, 0, 0, 0, 0, 0, ex.Message, null);
        }
    }
}
