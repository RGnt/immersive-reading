using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html;
using AngleSharp.Html.Parser;
using ImmersiveReading.Core.Plugins;

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
    private static readonly HashSet<string> SkippedTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg", "math", "head", "rt", "rp"
    };

    private static readonly HashSet<string> BlockTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "dt", "dd", "caption"
    };

    public static EnrichedChapterResult EnrichChapter(
        string rawXhtml,
        string bookId,
        string chapterHref,
        ILanguagePlugin languagePlugin,
        EnrichmentOptions options,
        Func<string, string, WordStatus>? getWordStatus = null,
        Func<string, bool>? isWordInDeck = null)
    {
        var parser = new HtmlParser(new HtmlParserOptions
        {
            IsKeepingSourceReferences = false
        });
        var document = parser.ParseDocument(rawXhtml);

        // 1. Rewrite relative EPUB asset URLs (CSS, images, SVG covers, internal links)
        RewriteRelativeAssetUrls(document, bookId, chapterHref);

        // 2. Inject Reader Assets & Furigana / Writing-Mode CSS Classes
        InjectHeadAssetsAndClasses(document, options);

        // 3. Discover logical block elements
        var blockElements = DiscoverBlocks(document);
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

            CollectBlockSlices(blockEl, segments, authorRubies, blockSb);
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
            ApplyTokensToBlock(document, blockEl, segments, tokenEntriesForBlock);
        }

        var tokenMap = new ChapterTokenMap(bookId, chapterHref, chapterTokens, paragraphTexts);
        string enrichedHtml = document.DocumentElement.ToHtml(PolyglotXhtmlMarkupFormatter.Instance);
        return new EnrichedChapterResult(enrichedHtml, tokenMap);
    }

    public static string ResolveEpubRelativePath(string baseChapterHref, string relativeUrl, out string fragment)
    {
        fragment = "";
        string cleanUrl = relativeUrl.Trim();
        int hashIdx = cleanUrl.IndexOf('#');
        if (hashIdx >= 0)
        {
            fragment = cleanUrl[hashIdx..];
            cleanUrl = cleanUrl[..hashIdx];
        }

        int queryIdx = cleanUrl.IndexOf('?');
        if (queryIdx >= 0)
        {
            cleanUrl = cleanUrl[..queryIdx];
        }

        cleanUrl = Uri.UnescapeDataString(cleanUrl).Replace('\\', '/');
        string normBase = (baseChapterHref ?? "").Replace('\\', '/').TrimStart('/');
        int lastSlash = normBase.LastIndexOf('/');
        string baseDir = lastSlash >= 0 ? normBase[..lastSlash] : "";

        string combined = cleanUrl.StartsWith('/')
            ? cleanUrl.TrimStart('/')
            : (string.IsNullOrEmpty(baseDir) ? cleanUrl : $"{baseDir}/{cleanUrl}");

        var parts = combined.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                continue;
            }
            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static bool IsExternalOrSpecialUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        string trimmed = url.Trim();
        return trimmed.StartsWith('#')
            || trimmed.StartsWith("ir-book://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ir-app://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase);
    }

    private static void RewriteRelativeAssetUrls(IDocument document, string bookId, string chapterHref)
    {
        if (string.IsNullOrWhiteSpace(bookId)) return;

        foreach (var el in document.All)
        {
            string tag = el.LocalName.ToLowerInvariant();

            if (tag is "img" or "script" or "audio" or "video" or "source")
            {
                string? src = el.GetAttribute("src");
                if (!IsExternalOrSpecialUrl(src))
                {
                    string resolved = ResolveEpubRelativePath(chapterHref, src!, out _);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        el.SetAttribute("src", $"ir-book://{bookId}/{resolved}");
                    }
                }

                if (tag == "video")
                {
                    string? poster = el.GetAttribute("poster");
                    if (!IsExternalOrSpecialUrl(poster))
                    {
                        string resolved = ResolveEpubRelativePath(chapterHref, poster!, out _);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            el.SetAttribute("poster", $"ir-book://{bookId}/{resolved}");
                        }
                    }
                }
            }
            else if (tag == "link")
            {
                string? href = el.GetAttribute("href");
                if (!IsExternalOrSpecialUrl(href))
                {
                    string resolved = ResolveEpubRelativePath(chapterHref, href!, out _);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        el.SetAttribute("href", $"ir-book://{bookId}/{resolved}");
                    }
                }
            }
            else if (tag == "image")
            {
                // SVG <image xlink:href="..."> or <image href="...">
                var hrefAttrs = el.Attributes
                    .Where(a => a.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase) ||
                                a.Name.Equals("xlink:href", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var attr in hrefAttrs)
                {
                    if (!IsExternalOrSpecialUrl(attr.Value))
                    {
                        string resolved = ResolveEpubRelativePath(chapterHref, attr.Value, out _);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            attr.Value = $"ir-book://{bookId}/{resolved}";
                        }
                    }
                }
            }
            else if (tag == "a")
            {
                string? href = el.GetAttribute("href");
                if (!IsExternalOrSpecialUrl(href))
                {
                    string resolved = ResolveEpubRelativePath(chapterHref, href!, out string fragment);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        el.SetAttribute("data-ir-spine-href", resolved);
                        if (!string.IsNullOrEmpty(fragment))
                        {
                            el.SetAttribute("data-ir-fragment", fragment.TrimStart('#'));
                        }
                        el.SetAttribute("href", $"ir-book://{bookId}/{resolved}{fragment}");
                    }
                }
            }
        }
    }

    private static void InjectHeadAssetsAndClasses(IDocument document, EnrichmentOptions options)
    {
        var html = document.DocumentElement;
        if (string.IsNullOrEmpty(html.GetAttribute("xmlns")))
        {
            html.SetAttribute("xmlns", "http://www.w3.org/1999/xhtml");
        }

        // Ensure xmlns:epub and xmlns:xlink are declared if any element uses those prefixes
        bool usesEpubPrefix = document.All.Any(e => e.Attributes.Any(a => a.Name.StartsWith("epub:", StringComparison.OrdinalIgnoreCase)));
        if (usesEpubPrefix && string.IsNullOrEmpty(html.GetAttribute("xmlns:epub")))
        {
            html.SetAttribute("xmlns:epub", "http://www.idpf.org/2007/ops");
        }

        bool usesXlinkPrefix = document.All.Any(e => e.Attributes.Any(a =>
            a.Name.StartsWith("xlink:", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a.NamespaceUri, NamespaceNames.XLinkUri, StringComparison.Ordinal)));
        if (usesXlinkPrefix && string.IsNullOrEmpty(html.GetAttribute("xmlns:xlink")))
        {
            html.SetAttribute("xmlns:xlink", "http://www.w3.org/1999/xlink");
        }

        // Writing mode class
        if (options.WritingMode == "vertical-rl")
        {
            html.ClassList.Add("ir-mode-vertical");
        }
        else if (options.WritingMode == "horizontal-tb")
        {
            html.ClassList.Add("ir-mode-horizontal");
        }

        // Furigana mode class
        string fClass = options.FuriganaMode switch
        {
            FuriganaMode.AuthorOnly => "ir-furigana-author",
            FuriganaMode.UnknownAndLearningOnly => "ir-furigana-unknown",
            FuriganaMode.Hover => "ir-furigana-hover",
            FuriganaMode.Off => "ir-furigana-off",
            _ => "ir-furigana-all"
        };
        html.ClassList.Add(fClass);

        if (options.HighlightVocabulary)
        {
            html.ClassList.Add("ir-highlight-vocab");
        }

        IElement head = (IElement?)document.Head ?? document.CreateElement("head");
        if (document.Head is null)
        {
            html.InsertBefore(head, html.FirstChild);
        }

        // Link stylesheet
        var link = document.CreateElement("link");
        link.SetAttribute("rel", "stylesheet");
        link.SetAttribute("href", "ir-app://assets/ir-reader-injected.css");
        head.AppendChild(link);

        // Script bridge
        var script = document.CreateElement("script");
        script.SetAttribute("src", "ir-app://assets/ir-reader-bridge.js");
        script.SetAttribute("defer", "defer");
        head.AppendChild(script);
    }

    private static List<IElement> DiscoverBlocks(IDocument document)
    {
        var list = new List<IElement>();
        var body = document.Body ?? document.DocumentElement;
        if (body is null) return list;

        void Walk(INode node)
        {
            if (node is IElement el)
            {
                string tag = el.LocalName;
                if (SkippedTagNames.Contains(tag)) return;

                if (BlockTagNames.Contains(tag))
                {
                    list.Add(el);
                    return;
                }

                if (string.Equals(tag, "div", StringComparison.OrdinalIgnoreCase))
                {
                    // If div contains no child blocks, treat it as a leaf block
                    bool hasChildBlock = el.Children.Any(c => BlockTagNames.Contains(c.LocalName) || c.LocalName.Equals("div", StringComparison.OrdinalIgnoreCase));
                    if (!hasChildBlock && el.ChildNodes.Any(c => c is IText || (c is IElement e && e.LocalName == "ruby")))
                    {
                        list.Add(el);
                        return;
                    }
                }
            }

            foreach (var child in node.ChildNodes)
            {
                Walk(child);
            }
        }

        Walk(body);
        return list;
    }

    private sealed class DomTextSlice
    {
        public INode Node { get; set; }
        public bool IsRubyElement { get; }
        public int StartOffset { get; }
        public int EndOffset { get; }
        public string Text { get; }

        public DomTextSlice(INode node, bool isRubyElement, int startOffset, int endOffset, string text)
        {
            Node = node;
            IsRubyElement = isRubyElement;
            StartOffset = startOffset;
            EndOffset = endOffset;
            Text = text;
        }
    }

    private static void CollectBlockSlices(
        IElement blockEl,
        List<DomTextSlice> slices,
        List<SourceRubySpan> authorRubies,
        StringBuilder blockSb)
    {
        void Walk(INode node)
        {
            if (node is IElement el)
            {
                string tag = el.LocalName;
                if (SkippedTagNames.Contains(tag)) return;

                if (string.Equals(tag, "ruby", StringComparison.OrdinalIgnoreCase))
                {
                    // Atomic ruby element
                    string baseText = "";
                    string reading = "";

                    foreach (var child in el.ChildNodes)
                    {
                        if (child is IElement childEl)
                        {
                            if (string.Equals(childEl.LocalName, "rt", StringComparison.OrdinalIgnoreCase))
                            {
                                reading += childEl.TextContent;
                            }
                            else if (!string.Equals(childEl.LocalName, "rp", StringComparison.OrdinalIgnoreCase))
                            {
                                baseText += childEl.TextContent;
                            }
                        }
                        else if (child is IText t)
                        {
                            baseText += t.Data;
                        }
                    }

                    int start = blockSb.Length;
                    blockSb.Append(baseText);
                    int end = blockSb.Length;

                    slices.Add(new DomTextSlice(el, isRubyElement: true, start, end, baseText));
                    authorRubies.Add(new SourceRubySpan(start, end, baseText, reading));
                    return;
                }
            }

            if (node is IText textNode)
            {
                string data = textNode.Data;
                if (!string.IsNullOrEmpty(data))
                {
                    int start = blockSb.Length;
                    blockSb.Append(data);
                    int end = blockSb.Length;
                    slices.Add(new DomTextSlice(textNode, isRubyElement: false, start, end, data));
                }
                return;
            }

            foreach (var child in node.ChildNodes)
            {
                Walk(child);
            }
        }

        foreach (var child in blockEl.ChildNodes)
        {
            Walk(child);
        }
    }

    private static void ApplyTokensToBlock(
        IDocument document,
        IElement blockEl,
        List<DomTextSlice> slices,
        List<EnrichedTokenEntry> tokens)
    {
        // For each text slice (IText node or <ruby> element), replace it with enriched token spans
        foreach (var slice in slices)
        {
            var overlappingTokens = tokens
                .Where(t => Math.Max(slice.StartOffset, t.Token.StartCharOffset) < Math.Min(slice.EndOffset, t.Token.EndCharOffset))
                .ToList();

            if (overlappingTokens.Count == 0) continue;

            if (slice.IsRubyElement && slice.Node is IElement rubyEl)
            {
                // Preserve internal author ruby markup! Add .ir-tok onto the ruby element (or wrap it)
                var topTok = overlappingTokens[0];
                rubyEl.ClassList.Add("ir-tok");
                rubyEl.SetAttribute("data-tid", topTok.TokenId.ToString());
                ApplyStatusClasses(rubyEl, topTok);
                continue;
            }

            if (slice.Node is not IText textNode || textNode.Parent is null) continue;

            var frag = document.CreateDocumentFragment();
            int currentSliceOffset = slice.StartOffset;

            foreach (var tok in overlappingTokens)
            {
                // Plain text before token
                if (tok.Token.StartCharOffset > currentSliceOffset)
                {
                    int preLen = tok.Token.StartCharOffset - currentSliceOffset;
                    string preText = slice.Text.Substring(currentSliceOffset - slice.StartOffset, preLen);
                    frag.AppendChild(document.CreateTextNode(preText));
                    currentSliceOffset = tok.Token.StartCharOffset;
                }

                // Token slice
                int tokSubStart = Math.Max(currentSliceOffset, tok.Token.StartCharOffset);
                int tokSubEnd = Math.Min(slice.EndOffset, tok.Token.EndCharOffset);
                int tokLen = tokSubEnd - tokSubStart;
                if (tokLen <= 0) continue;

                string tokSubText = slice.Text.Substring(tokSubStart - slice.StartOffset, tokLen);
                var span = document.CreateElement("span");
                span.ClassList.Add("ir-tok");
                span.SetAttribute("data-tid", tok.TokenId.ToString());
                ApplyStatusClasses(span, tok);

                // If token spans across multiple nodes, record data-part
                if (tok.Token.StartCharOffset < slice.StartOffset || tok.Token.EndCharOffset > slice.EndOffset)
                {
                    span.SetAttribute("data-part", (tok.Token.StartCharOffset < slice.StartOffset ? "1" : "0"));
                }

                // Render ruby units inside the token span
                RenderRubyUnitsIntoSpan(document, span, tok, tokSubStart - tok.Token.StartCharOffset, tokLen);
                frag.AppendChild(span);

                currentSliceOffset = tokSubEnd;
            }

            // Remaining trailing text in slice
            if (currentSliceOffset < slice.EndOffset)
            {
                string postText = slice.Text.Substring(currentSliceOffset - slice.StartOffset);
                frag.AppendChild(document.CreateTextNode(postText));
            }

            textNode.Parent.ReplaceChild(frag, textNode);
        }
    }

    private static void ApplyStatusClasses(IElement el, EnrichedTokenEntry tok)
    {
        if (!string.IsNullOrEmpty(tok.Token.Lemma))
        {
            el.SetAttribute("data-lemma", tok.Token.Lemma);
        }

        string statusClass = tok.Status switch
        {
            WordStatus.Learning => "ir-st-learning",
            WordStatus.Known => "ir-st-known",
            WordStatus.Ignored => "ir-st-ignored",
            _ => "ir-st-new"
        };
        el.ClassList.Add(statusClass);

        if (tok.InBookDeck)
        {
            el.ClassList.Add("ir-in-deck");
        }
    }

    private static void RenderRubyUnitsIntoSpan(
        IDocument document,
        IElement span,
        EnrichedTokenEntry tok,
        int subOffset,
        int subLen)
    {
        if (tok.RubyUnits.Count == 0)
        {
            span.AppendChild(document.CreateTextNode(tok.Token.Surface.Substring(subOffset, subLen)));
            return;
        }

        foreach (var u in tok.RubyUnits)
        {
            int uStart = Math.Max(subOffset, u.RelativeStart);
            int uEnd = Math.Min(subOffset + subLen, u.RelativeEnd);
            int len = uEnd - uStart;
            if (len <= 0) continue;

            string baseText = u.BaseText;
            if (u.Reading != null)
            {
                var ruby = document.CreateElement("ruby");
                if (u.Source == RubySource.PluginGenerated)
                {
                    ruby.ClassList.Add("ir-ruby-gen");
                }
                ruby.AppendChild(document.CreateTextNode(baseText));

                var rt = document.CreateElement("rt");
                rt.TextContent = u.Reading;
                ruby.AppendChild(rt);

                span.AppendChild(ruby);
            }
            else
            {
                span.AppendChild(document.CreateTextNode(baseText));
            }
        }
    }

    private sealed class PolyglotXhtmlMarkupFormatter : HtmlMarkupFormatter
    {
        public static new readonly PolyglotXhtmlMarkupFormatter Instance = new();

        private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "base", "br", "col", "embed", "hr", "img", "input",
            "link", "meta", "param", "source", "track", "wbr"
        };

        public override string OpenTag(IElement element, bool selfClosing)
        {
            var sb = new StringBuilder();
            sb.Append('<');
            if (!string.IsNullOrEmpty(element.Prefix))
            {
                sb.Append(element.Prefix).Append(':');
            }
            sb.Append(element.LocalName);

            var seenAttrNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var attr in element.Attributes)
            {
                string attrName = GetXmlAttributeName(attr);
                if (!seenAttrNames.Add(attrName)) continue;
                sb.Append(' ').Append(FormatAttribute(attrName, attr.Value));
            }

            if (selfClosing || VoidElements.Contains(element.LocalName))
            {
                sb.Append(" />");
            }
            else
            {
                sb.Append('>');
            }

            return sb.ToString();
        }

        public override string CloseTag(IElement element, bool selfClosing)
        {
            if (selfClosing || VoidElements.Contains(element.LocalName))
            {
                return string.Empty;
            }
            return base.CloseTag(element, selfClosing);
        }

        private static string GetXmlAttributeName(IAttr attr)
        {
            if (string.Equals(attr.NamespaceUri, NamespaceNames.XLinkUri, StringComparison.Ordinal))
            {
                return attr.LocalName.StartsWith("xlink:", StringComparison.Ordinal) ? attr.LocalName : $"xlink:{attr.LocalName}";
            }
            if (string.Equals(attr.NamespaceUri, NamespaceNames.XmlUri, StringComparison.Ordinal))
            {
                return attr.LocalName.StartsWith("xml:", StringComparison.Ordinal) ? attr.LocalName : $"xml:{attr.LocalName}";
            }
            if (string.Equals(attr.NamespaceUri, NamespaceNames.XmlNsUri, StringComparison.Ordinal))
            {
                return attr.LocalName == "xmlns" ? "xmlns" : $"xmlns:{attr.LocalName}";
            }

            string name = attr.Name;
            if (!string.IsNullOrEmpty(attr.Prefix) && !name.Contains(':'))
            {
                name = attr.Prefix == "xmlns" && attr.LocalName == "xmlns"
                    ? "xmlns"
                    : $"{attr.Prefix}:{attr.LocalName}";
            }
            return name;
        }

        private static string FormatAttribute(string name, string? rawVal)
        {
            string val = rawVal ?? string.Empty;
            var sb = new StringBuilder(name.Length + val.Length + 4);
            sb.Append(name).Append("=\"");
            foreach (char c in val)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public override string Text(ICharacterData text)
        {
            string data = text.Data ?? string.Empty;
            string? parentTag = text.ParentElement?.LocalName;
            if (string.Equals(parentTag, "script", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parentTag, "style", StringComparison.OrdinalIgnoreCase))
            {
                return data;
            }

            var sb = new StringBuilder(data.Length);
            foreach (char c in data)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
