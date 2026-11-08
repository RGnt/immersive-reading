using ImmersiveReading.Core.Library;
using ImmersiveReading.Library.Books;
using ImmersiveReading.Library.Folders;
using Xunit;

namespace ImmersiveReading.Core.Tests.Books;

public sealed class BookListSqlBuilderTests
{
    [Fact]
    public void BuildsEmptyFilterQueryWithDefaultSort()
    {
        var query = new BookListQuery();
        var folders = new List<RawFolderRow>();

        var (whereSql, orderBySql, parameters) = BookListSqlBuilder.Build(query, folders);

        Assert.Equal("", whereSql);
        Assert.Contains("series_name", orderBySql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildsUncategorizedFilter()
    {
        var query = new BookListQuery(UncategorizedOnly: true);
        var (whereSql, _, _) = BookListSqlBuilder.Build(query, []);

        Assert.Equal("WHERE b.folder_id IS NULL", whereSql);
    }

    [Fact]
    public void BuildsSeriesAndLanguageFilter()
    {
        var query = new BookListQuery(SeriesName: "Spice and Wolf", Language: "ja");
        var (whereSql, _, parameters) = BookListSqlBuilder.Build(query, []);

        Assert.Contains("b.series_name = $seriesName", whereSql);
        Assert.Contains("b.language = $lang", whereSql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildsTagFiltersForAndMode()
    {
        var query = new BookListQuery(TagIds: ["tag-1", "tag-2"], TagMatchMode: "AND");
        var (whereSql, _, parameters) = BookListSqlBuilder.Build(query, []);

        Assert.Contains("COUNT(DISTINCT bt.tag_id)", whereSql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildsTagFiltersForOrMode()
    {
        var query = new BookListQuery(TagIds: ["tag-1", "tag-2"], TagMatchMode: "OR");
        var (whereSql, _, parameters) = BookListSqlBuilder.Build(query, []);

        Assert.Contains("EXISTS (SELECT 1 FROM book_tags", whereSql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildsFolderSubtreeFilter()
    {
        var folders = new List<RawFolderRow>
        {
            new("f1", null, "Root", "folder", "", "folder", null, 0, "series_or_title"),
            new("f2", "f1", "Child", "folder", "", "folder", null, 0, "series_or_title")
        };
        var query = new BookListQuery(FolderId: "f1", IncludeDescendants: true);
        var (whereSql, _, parameters) = BookListSqlBuilder.Build(query, folders);

        Assert.Contains("b.folder_id IN", whereSql);
        Assert.Equal(2, parameters.Count);
    }
}

