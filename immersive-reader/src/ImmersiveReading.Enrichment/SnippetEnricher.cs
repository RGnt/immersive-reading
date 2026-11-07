using System.Net;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment;

public sealed class SnippetEnricher : ISnippetEnricher
{
    private readonly LanguagePluginRegistry _pluginRegistry;
    private readonly ChapterEnrichmentCache _cache;
    private readonly Func<string, string, WordStatus>? _getWordStatus;
    private int _snippetSequence;

    public SnippetEnricher(
        LanguagePluginRegistry pluginRegistry,
        ChapterEnrichmentCache cache,
        Func<string, string, WordStatus>? getWordStatus = null)
    {
        _pluginRegistry = pluginRegistry;
        _cache = cache;
        _getWordStatus = getWordStatus;
    }

    public EnrichedSnippetResult EnrichSnippet(
        string bookId,
        string htmlOrTextSnippet,
        string languageCode = "ja",
        IReadOnlySet<string>? targetPlusOneLemmas = null)
    {
        int seq = Interlocked.Increment(ref _snippetSequence);
        string snippetId = $"snip-{seq:D5}";
        string syntheticChapterHref = $"__snippet__/{snippetId}.xhtml";

        if (string.IsNullOrWhiteSpace(htmlOrTextSnippet))
        {
            return new EnrichedSnippetResult(snippetId, syntheticChapterHref, "", 0, []);
        }

        string trimmed = htmlOrTextSnippet.Trim();
        string wrappedXhtml = trimmed.StartsWith('<')
            ? $"<html><body><div class=\"ir-snippet-root\">{trimmed}</div></body></html>"
            : $"<html><body><p class=\"ir-snippet-root\">{WebUtility.HtmlEncode(trimmed)}</p></body></html>";

        var plugin = _pluginRegistry.Resolve(languageCode);
        var options = new EnrichmentOptions(FuriganaMode.AllKanji, HighlightVocabulary: true);

        var enriched = AngleSharpDomEnricher.EnrichChapter(
            wrappedXhtml,
            string.IsNullOrWhiteSpace(bookId) ? "book-snippet" : bookId,
            syntheticChapterHref,
            plugin,
            options,
            getWordStatus: _getWordStatus);

        _cache.Set(enriched.TokenMap);

        var parser = new HtmlParser();
        using var doc = parser.ParseDocument(enriched.EnrichedHtml);
        var rootEl = doc.QuerySelector(".ir-snippet-root") ?? doc.Body;
        var highlightedPlusOne = new HashSet<string>(StringComparer.Ordinal);

        if (rootEl is not null)
        {
            foreach (var tokSpan in rootEl.QuerySelectorAll(".ir-tok").OfType<IHtmlElement>())
            {
                tokSpan.SetAttribute("data-ir-snippet-href", syntheticChapterHref);
                string? lemma = tokSpan.GetAttribute("data-lemma");
                string surface = tokSpan.TextContent;

                if (targetPlusOneLemmas is not null &&
                    ((!string.IsNullOrEmpty(lemma) && targetPlusOneLemmas.Contains(lemma)) ||
                     (!string.IsNullOrEmpty(surface) && targetPlusOneLemmas.Contains(surface))))
                {
                    tokSpan.ClassList.Add("ir-chat-target-plus1");
                    tokSpan.SetAttribute("data-plus1-target", "true");
                    highlightedPlusOne.Add(!string.IsNullOrEmpty(lemma) ? lemma! : surface);
                }
            }
        }

        string bodyInner = rootEl?.InnerHtml ?? enriched.EnrichedHtml;
        return new EnrichedSnippetResult(
            SnippetId: snippetId,
            SyntheticChapterHref: syntheticChapterHref,
            EnrichedHtml: bodyInner,
            TokenCount: enriched.TokenMap.Tokens.Count,
            HighlightedPlusOneLemmas: highlightedPlusOne.ToList());
    }
}

