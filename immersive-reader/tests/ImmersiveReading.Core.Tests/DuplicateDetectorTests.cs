using ImmersiveReading.Library.Duplicates;

namespace ImmersiveReading.Core.Tests;

/// <summary>Spec 21 / features/17-library-duplicates.feature: normalization vectors and detection scenarios (pure, no database).</summary>
public sealed class DuplicateDetectorTests
{
    private static int _next;

    private static BookFacts Book(string title, string authors = "", string series = "", double? index = null, string isbn = "", string identifier = "",
        string language = "ja", int chars = 0, double progress = 0, bool studyPack = false, bool deck = false, long size = 1000, string added = "2024-01-01", string? file = null)
        => new($"b{++_next:D6}", title, series, index, authors, isbn, identifier, language, "", "", chars, chars > 0 ? 10 : 0, chars > 0, size,
            title + ".epub", "C:\\books", added, true, progress, studyPack, deck, 0);

    private static DuplicateSearchResult Find(params BookFacts[] books) => DuplicateDetector.Find(books);

    private static bool Together(DuplicateSearchResult r, BookFacts a, BookFacts b)
        => r.Groups.Any(g => g.Members.Any(m => m.Book.BookId == a.BookId) && g.Members.Any(m => m.Book.BookId == b.BookId));

    // ------------------------------------------------------------------ normalization
    [Theory]
    [InlineData("魔法科高校の劣等生 1", "魔法科高校の劣等生", 1)]
    [InlineData("魔法科高校の劣等生（1）【電子版】", "魔法科高校の劣等生", 1)]
    [InlineData("ソードアート・オンライン　第3巻", "そーどあーとおんらいん", 3)]
    [InlineData("ソードアート・オンライン 3", "そーどあーとおんらいん", 3)]
    [InlineData("Re:ゼロから始める異世界生活 第３巻", "reぜろから始める異世界生活", 3)]
    [InlineData("Re:ゼロから始める異世界生活 vol.4", "reぜろから始める異世界生活", 4)]
    [InlineData("Some Book Volume 12", "somebook", 12)]
    [InlineData("Some Book #7", "somebook", 7)]
    [InlineData("Some Book v2", "somebook", 2)]
    [InlineData("長い物語 第十二巻", "長い物語", 12)]
    [InlineData("長い物語 上巻", "長い物語", 1)]
    [InlineData("長い物語（下）", "長い物語", 3)]
    [InlineData("吾輩は猫である [eBook]", "吾輩は猫である", null)]
    [InlineData("吾輩は猫である", "吾輩は猫である", null)]
    [InlineData("2001年宇宙の旅", "2001年宇宙の旅", null)]            // a number that is part of the title is not a volume
    [InlineData("86-エイティシックス- 2", "86えいてぃしっくす", 2)]
    public void Title_NormalizationVectors(string title, string expectedBase, int? expectedVolume)
    {
        var key = DuplicateNormalizer.Title(title);
        Assert.Equal(expectedBase, key.Base);
        Assert.Equal(expectedVolume, key.Volume is null ? (int?)null : (int)key.Volume.Value);
    }

    // Real titles from a 2,000-book library (spec 21 section 3.1): volume numbers in all the shapes publishers and tools produce.
    [Theory]
    [InlineData("ロクでなし魔術講師と禁忌教典2", "ろくでなし魔術講師と禁忌教典", 2)]
    [InlineData("お隣の天使様にいつの間にか駄目人間にされていた件５", "お隣の天使様にいつの間にか駄目人間にされていた件", 5)]
    [InlineData("経験済みなキミと、 経験ゼロなオレが、 お付き合いする話。その２", "経験済みなきみと経験ぜろなおれがお付き合いする話", 2)]
    [InlineData("ロクでなし魔術講師と禁忌教典 (富士見ファンタジア文庫)", "ろくでなし魔術講師と禁忌教典", null)]
    [InlineData("ようこそ実力至上主義の教室へ 11 (MF文庫J)", "ようこそ実力至上主義の教室へ", 11)]
    [InlineData("TrymenT ―今を変えたいと願うあなたへ― VI", "trymeNt今を変えたいと願うあなたへ", 6)]
    [InlineData("僕のカノジョ先生２【電子特典付き】 (MF文庫J)", "僕のかのじょ先生", 2)]
    [InlineData("淡海乃海 水面が揺れる時～三英傑に嫌われた不運な男、朽木基綱の逆襲～十一【電子書籍限定書き下ろしSS付き】 (TOブックスラノベ)", "淡海乃海水面が揺れる時三英傑に嫌われた不運な男朽木基綱の逆襲", 11)]
    [InlineData("淡海乃海 水面が揺れる時～三英傑に嫌われた不運な男、朽木基綱の逆襲～弐 (TOブックスラノベ)", "淡海乃海水面が揺れる時三英傑に嫌われた不運な男朽木基綱の逆襲", 2)]
    [InlineData("終物語　上", "終物語", 1)]
    [InlineData("娘じゃなくて私が好きなの!?(5)", "娘じゃなくて私が好きなの", 5)]
    public void Title_RealWorldShapes(string title, string expectedBase, int? expectedVolume)
    {
        var key = DuplicateNormalizer.Title(title);
        Assert.Equal(expectedBase.ToLowerInvariant(), key.Base.ToLowerInvariant());
        Assert.Equal(expectedVolume, key.Volume is null ? (int?)null : (int)key.Volume.Value);
    }

    [Theory]
    [InlineData("やはり俺の青春ラブコメはまちがっている。14.5", 14.5)]
    [InlineData("ようこそ実力至上主義の教室へ 4.5 (MF文庫J)", 4.5)]
    [InlineData("ＧＥＮＥＳＩＳシリーズ 境界線上のホライゾンＩＩ＜上＞", 2.1)]
    [InlineData("ＧＥＮＥＳＩＳシリーズ 境界線上のホライゾンＩ＜下＞", 1.3)]
    public void Title_DecimalAndPartVolumes(string title, double expected)
        => Assert.Equal(expected, DuplicateNormalizer.Title(title).Volume!.Value, 6);

    [Fact]
    public void Authors_RolesSpacesAndSeparatorsAreIgnored()
    {
        Assert.Equal(["川原礫"], DuplicateNormalizer.Authors("川原 礫（著）"));
        Assert.Equal(["川原礫", "abec"], DuplicateNormalizer.Authors("川原礫、abec"));
        Assert.Equal(["夏目漱石"], DuplicateNormalizer.Authors("夏目 漱石 著"));
        Assert.Empty(DuplicateNormalizer.Authors(""));
        Assert.True(DuplicateNormalizer.AuthorsShareName(["川原礫"], ["川原礫", "abec"]));
        Assert.True(DuplicateNormalizer.AuthorsShareName(["夏目"], ["夏目漱石"]));            // surname only
        Assert.False(DuplicateNormalizer.AuthorsShareName(["夏目漱石"], ["芥川龍之介"]));
    }

    [Fact]
    public void Isbn13_DigitsOnly() => Assert.Equal("9784048931502", DuplicateNormalizer.Isbn("978-4-04-893150-2"));

    [Fact]
    public void Isbn10_IsConvertedToIsbn13()
    {
        Assert.Equal("9780306406157", DuplicateNormalizer.Isbn("0-306-40615-2"));      // published ISBN-10 / ISBN-13 pair
        Assert.Null(DuplicateNormalizer.Isbn("urn:uuid:123e4567-e89b-12d3-a456-426614174000"));
        Assert.Null(DuplicateNormalizer.Isbn(""));
    }

    // ------------------------------------------------------------------ grouping scenarios
    [Fact]
    public void EditionsOfTheSameVolume_AreCertain()
    {
        var a = Book("魔法科高校の劣等生 1", "佐島 勤");
        var b = Book("魔法科高校の劣等生（1）【電子版】", "佐島勤");
        var r = Find(a, b);

        var group = Assert.Single(r.Groups);
        Assert.Equal("certain", group.Confidence);
        Assert.Contains(group.Pairs, p => p.Reasons.Any(x => x.Contains("same title")));
    }

    [Fact]
    public void VolumeMarkersInDifferentNotations_AreTheSameVolume()
    {
        var a = Book("ソードアート・オンライン 3", "川原 礫");
        var b = Book("ソードアート・オンライン　第3巻", "川原礫（著）");
        var c = Book("ソードアート・オンライン vol.4", "川原礫");
        var r = Find(a, b, c);

        Assert.True(Together(r, a, b));
        Assert.False(Together(r, a, c));
    }

    [Fact]
    public void DifferentVolumesOfOneSeries_AreNeverGrouped_EvenWithSimilarLength()
    {
        var books = Enumerable.Range(1, 12).Select(i => Book($"ソードアート・オンライン {i}", "川原礫", "ソードアート・オンライン", i, chars: 120_000 + i)).ToArray();
        Assert.Empty(Find(books).Groups);
    }

    [Fact]
    public void SameVolumeFoundThroughSeriesIndex_WhenTitlesCarryNoNumber()
    {
        var a = Book("Re:ゼロから始める異世界生活", "長月達平", "Re:ゼロから始める異世界生活", 3);
        var b = Book("Re:ゼロから始める異世界生活 3", "長月達平");
        var r = Find(a, b);
        Assert.True(Together(r, a, b));
    }

    [Fact]
    public void SeriesIndexConflict_SeparatesBooksWithTheSameTitle()
    {
        var a = Book("短編集", "同じ人", "シリーズ", 1);
        var b = Book("短編集", "同じ人", "シリーズ", 2);
        Assert.Empty(Find(a, b).Groups);
    }

    [Fact]
    public void DifferentLanguages_AreNotDuplicates()
    {
        var a = Book("Norwegian Wood", "Haruki Murakami", language: "en");
        var b = Book("Norwegian Wood", "Haruki Murakami", language: "ja");
        Assert.Empty(Find(a, b).Groups);
    }

    [Fact]
    public void SameIsbn_IsCertain_EvenWithDifferentTitles()
    {
        var a = Book("Title in Japanese", "A", isbn: "978-4-04-893150-2");
        var b = Book("Completely other spelling", "B", isbn: "9784048931502");
        var group = Assert.Single(Find(a, b).Groups);
        Assert.Equal("certain", group.Confidence);
        Assert.Equal(100, group.Score);
    }

    [Fact]
    public void SameIdentifier_IsStrong_ButAnIdentifierStampedOnManyBooksIsIgnored()
    {
        var a = Book("A story", "X", identifier: "urn:uuid:123e4567-e89b-12d3-a456-426614174000");
        var b = Book("A story!", "X", identifier: "urn:uuid:123e4567-e89b-12d3-a456-426614174000");
        Assert.Equal(95, Assert.Single(Find(a, b).Groups).Score);

        var stamped = Enumerable.Range(0, 8).Select(i => Book($"Unrelated book number {i} qqq{i}", $"Author{i}", identifier: "generic-tool-identifier")).ToArray();
        Assert.Empty(Find(stamped).Groups);
    }

    [Fact]
    public void SideVolumes_AreNotTheirParentVolume_EvenWhenTheSeriesIndexSaysSo()
    {
        // the library stores "14.5" under series index 14; the title is the better evidence
        var main = Book("やはり俺の青春ラブコメはまちがっている。14", "渡航", "やはり俺の青春ラブコメはまちがっている。", 14);
        var side = Book("やはり俺の青春ラブコメはまちがっている。14.5", "渡航", "やはり俺の青春ラブコメはまちがっている。", 14);
        Assert.Empty(Find(main, side).Groups);
    }

    [Fact]
    public void TitlesThatDifferOnlyByANumber_AreDifferentBooks()
    {
        var plain = Book("失格紋の最強賢者　～世界最強の賢者が更に強くなるために転生しました～", "進行諸島");
        var two = Book("失格紋の最強賢者２　～世界最強の賢者が更に強くなるために転生しました～", "進行諸島");
        var three = Book("失格紋の最強賢者３　～世界最強の賢者が更に強くなるために転生しました～", "進行諸島");
        var r = Find(plain, two, three);
        Assert.False(Together(r, plain, two));
        Assert.False(Together(r, two, three));
    }

    [Fact]
    public void ATitleWithoutANumber_IsVolumeOne_NotAnyVolume()
    {
        var bare = Book("魔女と猟犬", "カミツキレイニー");
        var one = Book("魔女と猟犬 1", "カミツキレイニー");
        var two = Book("魔女と猟犬 2", "カミツキレイニー");
        var r = Find(bare, one, two);

        Assert.True(Together(r, bare, one));
        Assert.False(Together(r, bare, two));
        Assert.Equal("likely", r.Groups.Single().Confidence);
    }

    [Fact]
    public void AnIdentifierStampedOnEveryVolume_DoesNotGlueDifferentBooksTogether()
    {
        const string stamped = "urn:uuid:11111111-2222-3333-4444-555555555555";
        var vols = Enumerable.Range(1, 3).Select(i => Book($"終物語 {i}", "西尾維新", identifier: stamped)).ToList();
        var tail = Book("続・終物語", "西尾維新", identifier: stamped);
        Assert.Empty(Find([.. vols, tail]).Groups);
    }

    [Fact]
    public void VolumeFromTheFileNameOnly_DoesNotSeparateTheSameTitle()
    {
        // same number inside both titles; one file happens to be named "... 2.epub", the other has a reading-based name
        var a = Book("ソライロ♪プロジェクト　（２）恋愛経験ゼロたちの恋うたコンテスト", "一ノ瀬 三葉", file: "ソライロ♪プロジェクト 2.epub");
        var b = Book("ソライロ♪プロジェクト （２）恋愛経験ゼロたちの恋うたコンテスト", "一ノ瀬 三葉", file: "ソライロプロジェクト002レンアイケイケンゼロタチ.epub");
        Assert.True(Together(Find(a, b), a, b));
    }

    [Fact]
    public void ExtraVolumes_AreNotTheRegularVolume()
    {
        var regular = Book("終末なにしてますか？ 忙しいですか？ 救ってもらっていいですか？", "枯野 瑛");
        var extra = Book("終末なにしてますか？ 忙しいですか？ 救ってもらっていいですか？#EX", "枯野 瑛");
        Assert.Empty(Find(regular, extra).Groups);
    }

    [Fact]
    public void AuthorListsInADifferentOrder_AreTheSameAuthors()
    {
        var a = Book("沙耶の唄", "中央東口 · 大槻涼樹 · 虚淵玄（Nitroplus） · Nitroplus");
        var b = Book("沙耶の唄", "大槻涼樹 · 虚淵玄（Nitroplus） · Nitroplus · 中央東口");
        Assert.True(Together(Find(a, b), a, b));
    }

    [Fact]
    public void PublisherImprintsAndEditionRemarks_DoNotMakeTitlesDifferent()
    {
        var a = Book("落第騎士の英雄譚＜キャバルリィ＞２【電子特装版】 (GA文庫)", "海空 りく");
        var b = Book("落第騎士の英雄譚＜キャバルリィ＞２", "海空 りく");
        var group = Assert.Single(Find(a, b).Groups);
        Assert.Equal("certain", group.Confidence);
    }

    [Fact]
    public void ChainsCannotJoinDifferentVolumes()
    {
        var plain = Book("Title Story", "Same Author");
        var one = Book("Title Story 1", "Same Author");
        var two = Book("Title Story 2", "Same Author");
        var r = Find(plain, one, two);

        Assert.False(Together(r, one, two));
        foreach (var g in r.Groups)
            Assert.True(g.Members.Count(m => m.Book.BookId == one.BookId || m.Book.BookId == two.BookId) <= 1);
    }

    [Fact]
    public void LengthAdjustsTheScore_AndSaysWhy()
    {
        var a = Book("同じ本", "山田花子", chars: 100_000);
        var same = Book("同じ本", "山田花子", chars: 101_000);
        var shorter = Book("同じ本", "山田花子", chars: 60_000);

        var equal = Assert.Single(Find(a, same).Groups);
        var different = Assert.Single(Find(a, shorter).Groups);

        Assert.True(equal.Score > different.Score);
        Assert.Contains(equal.Pairs[0].Reasons, r => r == "same length");
        Assert.Contains(different.Pairs[0].Reasons, r => r.Contains("very different length"));
    }

    [Fact]
    public void FuzzyTitles_NeedAKnownSharedAuthor()
    {
        var a = Book("異世界で始める魔王討伐の旅路", "山田太郎");
        var b = Book("異世界で始める魔王討伐の旅路。", "山田太郎");          // punctuation only: equal base
        var c = Book("異世界で始める魔王討伐の旅路記", "山田太郎");           // one character more: fuzzy
        var d = Book("異世界で始める魔王討伐の旅路記", "別人");

        var r = Find(a, c, d);
        Assert.True(Together(r, a, c));
        Assert.False(Together(r, a, d));
        Assert.True(Together(Find(a, b), a, b));
    }

    [Fact]
    public void SameTitleDifferentKnownAuthors_AreNotDuplicates()
    {
        Assert.Empty(Find(Book("愛", "作者一"), Book("愛", "作者二")).Groups);
    }

    [Fact]
    public void SameTitleWithAnUnknownAuthor_IsOnlyPossible()
    {
        var group = Assert.Single(Find(Book("吾輩は猫である", "夏目漱石"), Book("吾輩は猫である", "")).Groups);
        Assert.Equal("possible", group.Confidence);
    }

    [Fact]
    public void DismissedPairs_AreNotProposedAgain()
    {
        var a = Book("吾輩は猫である", "夏目漱石");
        var b = Book("吾輩は猫である", "夏目漱石");
        var dismissed = new HashSet<(string, string)> { DuplicateDetector.PairKey(b.BookId, a.BookId) };

        Assert.Empty(DuplicateDetector.Find([a, b], dismissed).Groups);
        Assert.Single(DuplicateDetector.Find([a, b]).Groups);
    }

    [Fact]
    public void Recommendation_ProtectsReadingProgress_AndExplainsIt()
    {
        var plain = Book("吾輩は猫である", "夏目漱石", chars: 200_000, size: 5_000_000, added: "2023-01-01");
        var read = Book("吾輩は猫である [eBook]", "夏目漱石", chars: 199_000, progress: 42, size: 1_000_000, added: "2024-01-01");
        var group = Assert.Single(Find(plain, read).Groups);

        Assert.Equal(read.BookId, group.RecommendedKeepId);
        Assert.Contains(group.Members[0].KeepReasons, r => r.Contains("42%"));
    }

    [Fact]
    public void Recommendation_PrefersDeckAndStudyPackThenLongerText()
    {
        var bare = Book("同じ本", "山田花子", chars: 100_000);
        var withDeck = Book("同じ本", "山田花子", chars: 100_500, deck: true, studyPack: true);
        Assert.Equal(withDeck.BookId, Assert.Single(Find(bare, withDeck).Groups).RecommendedKeepId);

        var shortOne = Book("別の本", "山田花子", chars: 80_000);
        var longOne = Book("別の本", "山田花子", chars: 81_000);
        var g = Assert.Single(Find(shortOne, longOne).Groups);
        Assert.Equal(longOne.BookId, g.RecommendedKeepId);
        Assert.Contains(g.Members[0].KeepReasons, r => r == "longest text");
    }

    [Fact]
    public void GroupId_IsStableAcrossRuns_AndIndependentOfInputOrder()
    {
        var a = Book("安定した本", "山田花子");
        var b = Book("安定した本", "山田花子");
        string first = Assert.Single(Find(a, b).Groups).GroupId;
        string second = Assert.Single(Find(b, a).Groups).GroupId;
        Assert.Equal(first, second);
    }

    // ------------------------------------------------------------------ scale
    [Fact]
    public void TwentyThousandBooks_FindExactlyTheInjectedDuplicates_FastAndWithoutFalseGroups()
    {
        var rng = new Random(42);
        const string kana = "あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをん";
        string RandomWord(int len) => new(Enumerable.Range(0, len).Select(_ => kana[rng.Next(kana.Length)]).ToArray());

        var books = new List<BookFacts>();
        var authors = Enumerable.Range(0, 1500).Select(_ => RandomWord(3)).ToArray();
        var seriesPool = Enumerable.Range(0, 800).Select(_ => RandomWord(8)).ToArray();
        for (int i = 0; i < 19_800; i++)
        {
            // 800 series of up to 24 volumes each (same author within a series), plus standalones
            if (i < 800 * 24 && i % 24 < 24)
            {
                int s = i / 24, vol = i % 24 + 1;
                books.Add(Book($"{seriesPool[s]} {vol}", authors[s % authors.Length], seriesPool[s], vol, chars: 100_000 + rng.Next(50_000)));
            }
            else books.Add(Book(RandomWord(10) + i, authors[rng.Next(authors.Length)], chars: 50_000 + rng.Next(200_000)));
        }
        var injected = new List<(BookFacts, BookFacts)>();
        for (int i = 0; i < 200; i++)
        {
            var original = books[i * 97];
            var twin = Book(original.Title + "【電子書籍版】", original.Authors + "（著）", chars: original.TotalChars + 100);
            books.Add(twin);
            injected.Add((original, twin));
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Find([.. books]);
        watch.Stop();

        Assert.Equal(200, result.Groups.Count);
        foreach (var (original, twin) in injected) Assert.True(Together(result, original, twin));
        Assert.All(result.Groups, g => Assert.Equal(2, g.Members.Count));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }
}
