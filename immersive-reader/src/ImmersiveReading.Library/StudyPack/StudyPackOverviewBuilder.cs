using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmersiveReading.Core.Library;

namespace ImmersiveReading.Library.StudyPack;

public static class StudyPackOverviewBuilder
{
    private static readonly Regex SafeBookIdRegex = new("^book-[a-f0-9]{16}$", RegexOptions.Compiled);

    public static StudyPackBookOverview GetBookStudyPack(string outputRoot, string bookId)
    {
        if (string.IsNullOrWhiteSpace(bookId) || !SafeBookIdRegex.IsMatch(bookId))
        {
            return EmptyOverview(bookId);
        }

        var (generation, manifestPath, manifestDoc) = PublicationReader.TryReadActivePublication(outputRoot);
        using (manifestDoc)
        {
            string? studySnapshotRoot = null;
            if (manifestDoc is not null &&
                manifestDoc.RootElement.TryGetProperty("books", out var booksEl) &&
                booksEl.TryGetProperty(bookId, out var bookEntry))
            {
                if (bookEntry.TryGetProperty("study", out var studyEntry) &&
                    studyEntry.TryGetProperty("root", out var rootProp))
                {
                    string? relRoot = rootProp.GetString();
                    if (!string.IsNullOrEmpty(relRoot))
                    {
                        string candidate = Path.GetFullPath(Path.Combine(outputRoot, relRoot.Replace('/', Path.DirectorySeparatorChar)));
                        if (candidate.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(candidate))
                        {
                            studySnapshotRoot = candidate;
                        }
                    }
                }
            }

            string dataDir = PublicationReader.ResolveBookSubdir(outputRoot, studySnapshotRoot, "data", bookId);
            string pdfDir = PublicationReader.ResolveBookSubdir(outputRoot, studySnapshotRoot, "pdf", bookId);
            string htmlDir = PublicationReader.ResolveBookSubdir(outputRoot, studySnapshotRoot, "html", bookId);

            var masterPdfs = new List<StudyPackPdfArtifact>();
            var chapterPdfs = new List<StudyPackPdfArtifact>();

            if (Directory.Exists(pdfDir))
            {
                var masterSpec = new (string FileName, string Title)[]
                {
                    ("kanji-master.pdf", "Kanji Master Sheet (A4)"),
                    ("vocabulary-master.pdf", "Vocabulary Master Sheet (A4)"),
                    ("grammar-master.pdf", "Grammar Master Sheet (A4)")
                };

                foreach (var (fileName, title) in masterSpec)
                {
                    string fullPdf = Path.Combine(pdfDir, fileName);
                    if (File.Exists(fullPdf))
                    {
                        var fi = new FileInfo(fullPdf);
                        masterPdfs.Add(new StudyPackPdfArtifact(
                            FileName: fileName,
                            Title: title,
                            Kind: "master",
                            FullPath: fullPdf,
                            AppSchemeUrl: $"ir-app://studypack/pdf/{bookId}/{fileName}",
                            SizeBytes: fi.Length));
                    }
                }

                foreach (var file in Directory.GetFiles(pdfDir, "chapter-*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(file);
                    string fName = fi.Name;
                    string kind = fName.Contains("kanji", StringComparison.OrdinalIgnoreCase) ? "chapter-kanji" : "chapter-vocabulary";
                    chapterPdfs.Add(new StudyPackPdfArtifact(
                        FileName: fName,
                        Title: FormatChapterPdfTitle(fName),
                        Kind: kind,
                        FullPath: fi.FullName,
                        AppSchemeUrl: $"ir-app://studypack/pdf/{bookId}/{fName}",
                        SizeBytes: fi.Length));
                }
            }

            // Also check data directory for PDFs (e.g. modular master study sheet)
            if (Directory.Exists(dataDir))
            {
                foreach (var file in Directory.GetFiles(dataDir, "*.pdf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(file);
                    string fName = fi.Name;
                    if (masterPdfs.Any(p => p.FileName == fName) || chapterPdfs.Any(p => p.FileName == fName))
                        continue;

                    string title = fName.EndsWith("_study_sheet.pdf", StringComparison.OrdinalIgnoreCase)
                        ? "Master Study Sheet (A4)"
                        : fName.Equals("vocabulary-master.pdf", StringComparison.OrdinalIgnoreCase)
                        ? "Vocabulary Master Sheet (A4)"
                        : fName.Equals("kanji-master.pdf", StringComparison.OrdinalIgnoreCase)
                        ? "Kanji Master Sheet (A4)"
                        : fName.Equals("grammar-master.pdf", StringComparison.OrdinalIgnoreCase)
                        ? "Grammar Master Sheet (A4)"
                        : FormatChapterPdfTitle(fName);

                    masterPdfs.Add(new StudyPackPdfArtifact(
                        FileName: fName,
                        Title: title,
                        Kind: "master",
                        FullPath: fi.FullName,
                        AppSchemeUrl: $"ir-app://studypack/output/data/{bookId}/{fName}",
                        SizeBytes: fi.Length));
                }
            }

            var vocabSets = LoadVocabularySets(dataDir);
            int totalWordsInSets = vocabSets.Sum(s => s.Words.Count);

            string htmlIndex = Path.Combine(htmlDir, "index.html");
            bool hasHtml = File.Exists(htmlIndex);

            bool hasStudyData = File.Exists(Path.Combine(dataDir, "study_data.json"));
            bool hasReaderJson = File.Exists(Path.Combine(dataDir, "reader.json"));
            bool available = masterPdfs.Count > 0 || chapterPdfs.Count > 0 || vocabSets.Count > 0 || hasStudyData || hasReaderJson;

            return new StudyPackBookOverview(
                BookId: bookId,
                Available: available,
                Generation: generation,
                ActiveManifestPath: manifestPath,
                MasterPdfs: masterPdfs,
                ChapterPdfs: chapterPdfs,
                VocabularySets: vocabSets,
                TotalVocabularyInSets: totalWordsInSets,
                HasHtmlCompanion: hasHtml,
                HtmlEntryPath: hasHtml ? htmlIndex : null);
        }
    }

    public static List<VocabularyLearningSet> LoadVocabularySets(string dataDir)
    {
        var result = new List<VocabularyLearningSet>();
        string setsPath = Path.Combine(dataDir, "vocabulary-sets.json");
        if (File.Exists(setsPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(setsPath, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("sets", out var setsArr) && setsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var setEl in setsArr.EnumerateArray())
                    {
                        string id = setEl.TryGetProperty("id", out var idP) ? (idP.GetString() ?? "") : "";
                        int number = setEl.TryGetProperty("number", out var numP) ? numP.GetInt32() : 0;
                        int size = setEl.TryGetProperty("size", out var szP) ? szP.GetInt32() : 20;

                        var words = new List<VocabularySetWordItem>();
                        if (setEl.TryGetProperty("words", out var wordsArr) && wordsArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var w in wordsArr.EnumerateArray())
                            {
                                words.Add(new VocabularySetWordItem(
                                    CardId: w.TryGetProperty("card_id", out var cid) ? (cid.GetString() ?? "") : "",
                                    Word: w.TryGetProperty("word", out var wd) ? (wd.GetString() ?? "") : "",
                                    Reading: w.TryGetProperty("reading", out var rd) ? (rd.GetString() ?? "") : "",
                                    ChapterId: w.TryGetProperty("chapter_id", out var chid) ? (chid.GetString() ?? "") : "",
                                    StudyNumber: w.TryGetProperty("study_number", out var snum) ? snum.GetInt32() : 0
                                ));
                            }
                        }

                        result.Add(new VocabularyLearningSet(id, number, size, words));
                    }
                }
            }
            catch
            {
                // Ignore malformed JSON
            }
        }

        // If no sets loaded yet, check study_data.json
        if (result.Count == 0)
        {
            string studyDataPath = Path.Combine(dataDir, "study_data.json");
            if (File.Exists(studyDataPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(studyDataPath, Encoding.UTF8));
                    if (doc.RootElement.TryGetProperty("items", out var itemsArr) && itemsArr.ValueKind == JsonValueKind.Array)
                    {
                        var allWords = new List<VocabularySetWordItem>();
                        int studyNumber = 1;
                        foreach (var item in itemsArr.EnumerateArray())
                        {
                            string word = item.TryGetProperty("headword", out var hw) ? (hw.GetString() ?? "") : "";
                            if (string.IsNullOrWhiteSpace(word))
                            {
                                word = item.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                            }
                            string reading = item.TryGetProperty("reading", out var rd) ? (rd.GetString() ?? "") : "";
                            string key = item.TryGetProperty("key", out var kProp) ? (kProp.GetString() ?? word) : word;

                            allWords.Add(new VocabularySetWordItem(
                                CardId: key,
                                Word: word,
                                Reading: reading,
                                ChapterId: "",
                                StudyNumber: studyNumber++
                            ));
                        }

                        int setNumber = 1;
                        for (int i = 0; i < allWords.Count; i += 20)
                        {
                            var chunk = allWords.Skip(i).Take(20).ToList();
                            result.Add(new VocabularyLearningSet(
                                Id: $"set-{setNumber:D2}",
                                Number: setNumber,
                                Size: chunk.Count,
                                Words: chunk
                            ));
                            setNumber++;
                        }
                    }
                }
                catch
                {
                    // Ignore malformed study_data.json
                }
            }
        }

        return result;
    }

    public static string FormatChapterPdfTitle(string fileName)
    {
        string stem = Path.GetFileNameWithoutExtension(fileName);
        return stem.Replace('-', ' ').ToUpperInvariant();
    }

    public static StudyPackBookOverview EmptyOverview(string bookId)
    {
        return new StudyPackBookOverview(
            BookId: bookId ?? "",
            Available: false,
            Generation: null,
            ActiveManifestPath: null,
            MasterPdfs: [],
            ChapterPdfs: [],
            VocabularySets: [],
            TotalVocabularyInSets: 0,
            HasHtmlCompanion: false,
            HtmlEntryPath: null);
    }
}

