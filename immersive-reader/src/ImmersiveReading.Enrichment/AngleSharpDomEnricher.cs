using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ImmersiveReading.Core.Epub;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment.Dom;

namespace ImmersiveReading.Enrichment;

public sealed record EnrichedChapterResult(
    string EnrichedHtml,
    ChapterTokenMap TokenMap
);

/// <summary>
/// Pre-render DOM enrichment using AngleSharp.
/// Tokenizes block text nodes, aligns okurigana, respects author EPUB ruby annotations,
/// wraps tokens in <span class="ir-tok" data-tid="...">, rewrites relative EPUB asset URLs
/// to ir-book://{bookId}/..., and serializes valid polyglot XHTML5.
/// </summary>
public static class AngleSharpDomEnricher
{
    public static EnrichedChapterResult EnrichChapter(
        string rawXhtml,
        string bookId,
        string chapterHref,
        ILanguagePlugin languagePlugin,
        EnrichmentOptions options,
        Func<string, string, WordStatus>? getWordStatus = null,
        Func<string, bool>? isWordInDeck = null,
        IReadOnlyList<IDomEnrichmentStage>? domStages = null)
    {
        var parser = new HtmlParser(new HtmlParserOptions
        {
            IsKeepingSourceReferences = false
        });
        string normalizedXhtml = EpubArchiveReader.NormalizeXhtmlSelfClosingTags(rawXhtml);
        var document = parser.ParseDocument(normalizedXhtml);

        // 1. Rewrite relative EPUB asset URLs (CSS, images, SVG covers, internal links)
        EpubUrlRewriter.RewriteRelativeAssetUrls(document, bookId, chapterHref);

        // 2. Inject Reader Assets & Furigana / Writing-Mode CSS Classes
        ReaderHeadInjector.InjectHeadAssetsAndClasses(document, options);

        // 3. Discover logical block elements
        var blockElements = BlockDiscovery.DiscoverBlocks(document);
        using var tokenizer = languagePlugin.CreateTokenizer();
        var rubyAligner = languagePlugin.GetRubyAligner();

        var chapterTokens = new List<EnrichedTokenEntry>();
        var paragraphTexts = new List<string>();
        int chapterTokenId = 0;

        for (int pIdx = 0; pIdx < blockElements.Count; pIdx++)
        {
            var blockEl = blockElements[pIdx];
            blockEl.SetAttribute("data-ir-pid", pIdx.ToString());

            // Collect text segments and atomic ruby spans inside this block
            var segments = new List<DomTextSlice>();
            var authorRubies = new List<SourceRubySpan>();
            var blockSb = new StringBuilder();

            BlockDiscovery.CollectBlockSlices(blockEl, segments, authorRubies, blockSb);
            string blockText = blockSb.ToString();
            paragraphTexts.Add(blockText);

            if (string.IsNullOrWhiteSpace(blockText))
            {
                continue;
            }

            // Tokenize block text while respecting author ruby spans
            var analyzedTokens = tokenizer.TokenizeParagraph(blockText, authorRubies);
            var tokenEntriesForBlock = new List<EnrichedTokenEntry>(analyzedTokens.Count);

            foreach (var tok in analyzedTokens)
            {
                var rubyUnits = rubyAligner?.AlignRuby(
                    tok.Surface,
                    tok.Reading,
                    authorRubies.Where(ar => Math.Max(tok.StartCharOffset, ar.StartCharOffset) < Math.Min(tok.EndCharOffset, ar.EndCharOffset)).ToList())
                    ?? [new RubyUnit(0, tok.Surface.Length, tok.Surface, null, RubySource.None)];

                var status = getWordStatus?.Invoke(tok.Lemma, tok.DictionaryReading) ?? WordStatus.New;
                bool inDeck = isWordInDeck?.Invoke(tok.Lemma) ?? false;

                var entry = new EnrichedTokenEntry(
                    TokenId: chapterTokenId++,
                    ParagraphId: pIdx,
                    Token: tok,
                    RubyUnits: rubyUnits,
                    Status: status,
                    InBookDeck: inDeck);

                chapterTokens.Add(entry);
                tokenEntriesForBlock.Add(entry);
            }

            // Mutate DOM nodes in this block
            TokenDomApplier.ApplyTokensToBlock(document, blockEl, segments, tokenEntriesForBlock);
        }

        if (domStages != null && domStages.Count > 0)
        {
            var domContext = new DomEnrichmentContext(bookId, chapterHref, chapterTokens, options);
            foreach (var stage in domStages)
            {
                stage.Enrich(document, domContext);
            }
        }

        var tokenMap = new ChapterTokenMap(bookId, chapterHref, chapterTokens, paragraphTexts);
        string enrichedHtml = document.DocumentElement.ToHtml(PolyglotXhtmlMarkupFormatter.Instance);
        return new EnrichedChapterResult(enrichedHtml, tokenMap);
    }

    public static string ResolveEpubRelativePath(string baseChapterHref, string relativeUrl, out string fragment) =>
        EpubUrlRewriter.ResolveEpubRelativePath(baseChapterHref, relativeUrl, out fragment);
}
