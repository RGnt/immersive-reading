using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AngleSharp.Dom;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;
using ImmersiveReading.Enrichment.Dom;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class PosDomEnrichmentStage : IDomEnrichmentStage
{
    public string StageId => "pos-highlighter";
    public int Order => 200;

    public PosHighlighterSettings Settings { get; set; }

    private readonly ChapterPosCache? _cache;
    private readonly PythonChapterPosTagger _tagger;

    public PosDomEnrichmentStage(
        PosHighlighterSettings? initialSettings = null,
        ChapterPosCache? cache = null,
        PythonChapterPosTagger? tagger = null)
    {
        Settings = initialSettings ?? new PosHighlighterSettings();
        _cache = cache ?? new ChapterPosCache(maxCacheSizeMb: Settings.MaxCacheSizeMb);
        _tagger = tagger ?? new PythonChapterPosTagger();
    }

    public void Enrich(IDocument document, DomEnrichmentContext context)
    {
        if (!Settings.Enabled)
        {
            return;
        }

        // Sync cache limit with settings
        if (_cache != null && Settings.MaxCacheSizeMb > 0)
        {
            _cache.MaxCacheSizeMb = Settings.MaxCacheSizeMb;
        }

        // 1. Discover block elements
        var blockElements = BlockDiscovery.DiscoverBlocks(document);
        var blockInfos = new List<(int Pid, IElement BlockEl, List<DomTextSlice> Slices, List<SourceRubySpan> AuthorRubies, string Text)>();
        var hashSb = new StringBuilder();

        for (int pIdx = 0; pIdx < blockElements.Count; pIdx++)
        {
            var blockEl = blockElements[pIdx];
            var slices = new List<DomTextSlice>();
            var authorRubies = new List<SourceRubySpan>();
            var blockSb = new StringBuilder();

            BlockDiscovery.CollectBlockSlices(blockEl, slices, authorRubies, blockSb);
            string blockText = blockSb.ToString();
            blockInfos.Add((pIdx, blockEl, slices, authorRubies, blockText));
            hashSb.Append(pIdx).Append(':').Append(blockText).Append(';');
        }

        string contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashSb.ToString()))).ToLowerInvariant();

        // 2. Fetch or compute Sudachi morphemes for paragraphs
        IReadOnlyDictionary<int, SudachiParagraph>? paragraphsMap = null;

        // 2a. Check cache
        if (_cache != null)
        {
            string? cachedJson = _cache.Get(context.BookId, context.ChapterHref, contentHash);
            if (!string.IsNullOrEmpty(cachedJson))
            {
                paragraphsMap = PythonChapterPosTagger.ParseParagraphsJson(cachedJson);
            }
        }

        // 2b. If cache miss and Python enabled
        if ((paragraphsMap == null || paragraphsMap.Count == 0) && blockInfos.Count > 0)
        {
            var pList = blockInfos.Select(b => (b.Pid, b.Text)).ToList();
            if (Settings.UsePythonNlp)
            {
                var (pyMap, rawJson) = _tagger.TagParagraphs(
                    context.BookId,
                    context.ChapterHref,
                    pList,
                    Settings.PythonPath);

                if (pyMap != null && pyMap.Count > 0 && !string.IsNullOrEmpty(rawJson))
                {
                    paragraphsMap = pyMap;
                    _cache?.Set(context.BookId, context.ChapterHref, contentHash, rawJson);
                }
                else if (pyMap != null && pyMap.Count > 0)
                {
                    paragraphsMap = pyMap;
                }
            }

            // 2c. Seamless in-process fallback
            if (paragraphsMap == null || paragraphsMap.Count == 0)
            {
                paragraphsMap = PythonChapterPosTagger.FallbackTagParagraphs(pList);
            }
        }

        // 3. Surround block text with spans based on Sudachi parsing
        int synthTid = 10000;
        if (paragraphsMap != null && paragraphsMap.Count > 0)
        {
            foreach (var (pIdx, blockEl, slices, authorRubies, blockText) in blockInfos)
            {
                if (string.IsNullOrWhiteSpace(blockText)) continue;
                if (!paragraphsMap.TryGetValue(pIdx, out var sudachiPara) || sudachiPara.Morphemes.Count == 0)
                {
                    continue;
                }

                // If block already has .ir-tok spans, annotate them directly
                var existingToks = blockEl.QuerySelectorAll(".ir-tok");
                if (existingToks.Length > 0)
                {
                    // Existing spans: align morphemes to spans by text content / position
                    int morphemeIdx = 0;
                    foreach (var tokEl in existingToks)
                    {
                        string tokText = tokEl.TextContent;
                        if (string.IsNullOrWhiteSpace(tokText)) continue;

                        while (morphemeIdx < sudachiPara.Morphemes.Count &&
                               sudachiPara.Morphemes[morphemeIdx].Surface != tokText &&
                               !tokText.Contains(sudachiPara.Morphemes[morphemeIdx].Surface))
                        {
                            morphemeIdx++;
                        }

                        if (morphemeIdx < sudachiPara.Morphemes.Count)
                        {
                            var m = sudachiPara.Morphemes[morphemeIdx];
                            ApplyMorphemeClasses(tokEl, m);
                            morphemeIdx++;
                        }
                    }
                }
                else
                {
                    // Block text is not yet surrounded with spans: surround each morpheme with spans
                    SurroundBlockTextWithMorphemeSpans(document, blockEl, slices, sudachiPara.Morphemes, ref synthTid);
                }
            }
        }

        // 4. Also classify any remaining .ir-tok spans that had token metadata
        if (context.Tokens != null && context.Tokens.Count > 0)
        {
            var fallbackClassifications = new Dictionary<int, PosClassification>(context.Tokens.Count);
            foreach (var tok in context.Tokens)
            {
                fallbackClassifications[tok.TokenId] = PosClassifier.ClassifyToken(tok.TokenId, tok.Token);
            }

            var tokenElements = document.QuerySelectorAll(".ir-tok");
            foreach (var el in tokenElements)
            {
                var tidAttr = el.GetAttribute("data-tid");
                if (string.IsNullOrEmpty(tidAttr) || !int.TryParse(tidAttr, out int tid))
                {
                    continue;
                }

                if (!fallbackClassifications.TryGetValue(tid, out var classification))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(classification.WordType) && classification.WordType != "unknown")
                {
                    if (!el.ClassList.Contains(classification.WordType))
                    {
                        el.ClassList.Add(classification.WordType);
                    }
                    el.SetAttribute("data-word-type", classification.WordType);
                }

                if (!string.IsNullOrEmpty(classification.SubType) &&
                    classification.SubType != "unknown" &&
                    classification.SubType != classification.WordType &&
                    !el.ClassList.Contains(classification.SubType))
                {
                    el.ClassList.Add(classification.SubType);
                }

                foreach (var c in classification.Classes)
                {
                    if (!string.IsNullOrEmpty(c) && !el.ClassList.Contains(c))
                    {
                        el.ClassList.Add(c);
                    }
                }

                if (!string.IsNullOrEmpty(classification.Code) && classification.Code != "unk")
                {
                    el.SetAttribute("data-ir-pos", classification.Code);
                }
            }
        }

        // 5. Inject styles
        InjectStyles(document);
    }

    private static void SurroundBlockTextWithMorphemeSpans(
        IDocument document,
        IElement blockEl,
        List<DomTextSlice> slices,
        IReadOnlyList<SudachiMorpheme> morphemes,
        ref int nextTid)
    {
        foreach (var slice in slices)
        {
            var overlappingMorphemes = morphemes
                .Where(m => Math.Max(slice.StartOffset, m.Start) < Math.Min(slice.EndOffset, m.End))
                .ToList();

            if (overlappingMorphemes.Count == 0) continue;

            if (slice.IsRubyElement && slice.Node is IElement rubyEl)
            {
                var topM = overlappingMorphemes[0];
                rubyEl.ClassList.Add("ir-tok");
                rubyEl.SetAttribute("data-tid", (nextTid++).ToString());
                ApplyMorphemeClasses(rubyEl, topM);
                continue;
            }

            if (slice.Node is not IText textNode || textNode.Parent is null) continue;

            var frag = document.CreateDocumentFragment();
            int currentOffset = slice.StartOffset;

            foreach (var m in overlappingMorphemes)
            {
                if (m.Start > currentOffset)
                {
                    int preLen = m.Start - currentOffset;
                    string preText = slice.Text.Substring(currentOffset - slice.StartOffset, preLen);
                    frag.AppendChild(document.CreateTextNode(preText));
                    currentOffset = m.Start;
                }

                int subStart = Math.Max(currentOffset, m.Start);
                int subEnd = Math.Min(slice.EndOffset, m.End);
                int len = subEnd - subStart;
                if (len <= 0) continue;

                string subText = slice.Text.Substring(subStart - slice.StartOffset, len);
                var span = document.CreateElement("span");
                span.ClassList.Add("ir-tok");
                span.SetAttribute("data-tid", (nextTid++).ToString());
                span.AppendChild(document.CreateTextNode(subText));

                ApplyMorphemeClasses(span, m);
                frag.AppendChild(span);

                currentOffset = subEnd;
            }

            if (currentOffset < slice.EndOffset)
            {
                string postText = slice.Text.Substring(currentOffset - slice.StartOffset);
                frag.AppendChild(document.CreateTextNode(postText));
            }

            textNode.Parent.ReplaceChild(frag, textNode);
        }
    }

    private static void ApplyMorphemeClasses(IElement el, SudachiMorpheme m)
    {
        if (!string.IsNullOrEmpty(m.WordType) && m.WordType != "unknown")
        {
            if (!el.ClassList.Contains(m.WordType))
            {
                el.ClassList.Add(m.WordType);
            }
            el.SetAttribute("data-word-type", m.WordType);
        }

        if (!string.IsNullOrEmpty(m.SubType) &&
            m.SubType != "unknown" &&
            m.SubType != m.WordType &&
            !el.ClassList.Contains(m.SubType))
        {
            el.ClassList.Add(m.SubType);
        }

        foreach (var c in m.Classes)
        {
            if (!string.IsNullOrEmpty(c) && !el.ClassList.Contains(c))
            {
                el.ClassList.Add(c);
            }
        }

        if (!string.IsNullOrEmpty(m.Code) && m.Code != "unk")
        {
            el.SetAttribute("data-ir-pos", m.Code);
        }
    }

    private void InjectStyles(IDocument document)
    {
        var head = document.Head;
        if (head == null)
        {
            return;
        }

        var existing = head.QuerySelector("#ir-pos-highlighter-style");
        if (existing != null)
        {
            existing.Remove();
        }

        var styleEl = document.CreateElement("style");
        styleEl.Id = "ir-pos-highlighter-style";
        styleEl.TextContent = GenerateCss(Settings);
        head.AppendChild(styleEl);
    }

    public static string GenerateCss(PosHighlighterSettings s)
    {
        var sb = new StringBuilder();
        string style = s.HighlightStyle ?? "text-color";

        // Furigana/ruby rt elements must preserve reader ruby color and not be colored by POS highlights
        sb.AppendLine("rt { color: var(--ruby-color, #e2e8f0) !important; -webkit-text-fill-color: var(--ruby-color, #e2e8f0) !important; text-decoration: none !important; }");

        void AppendRule(string selector, string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor)) return;

            var parts = selector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var rtSelectors = string.Join(", ", parts.Select(p => $"{p} rt, {p} ruby > rt"));

            if (string.Equals(style, "underline", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{selector} {{ text-decoration: underline; text-decoration-color: {hexColor}; text-underline-offset: 3px; }}");
                sb.AppendLine($"{rtSelectors} {{ text-decoration: none !important; }}");
            }
            else if (string.Equals(style, "background", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{selector} {{ background-color: {hexColor}26; border-radius: 3px; padding: 0 1px; }}");
            }
            else // Default: text-color
            {
                sb.AppendLine($"{selector} {{ color: {hexColor} !important; }}");
                sb.AppendLine($"{rtSelectors} {{ color: var(--ruby-color, #e2e8f0) !important; -webkit-text-fill-color: var(--ruby-color, #e2e8f0) !important; }}");
            }
        }

        AppendRule(".ir-tok.adjective-i, .adjective-i, .ir-tok.ir-pos-adj-i, .ir-pos-adj-i, .ir-tok.adjective:not(.adjective-na)", s.ColorAdjI);
        AppendRule(".ir-tok.adjective-na, .adjective-na, .ir-tok.ir-pos-adj-na, .ir-pos-adj-na", s.ColorAdjNa);
        AppendRule(".ir-tok.verb-u, .verb-u, .ir-tok.ir-pos-verb-u, .ir-pos-verb-u, .ir-tok.verb:not(.verb-ru):not(.verb-irr)", s.ColorVerbGodan);
        AppendRule(".ir-tok.verb-ru, .verb-ru, .ir-tok.ir-pos-verb-ru, .ir-pos-verb-ru", s.ColorVerbIchidan);
        AppendRule(".ir-tok.verb-irr, .verb-irr, .ir-tok.ir-pos-verb-irr, .ir-pos-verb-irr", s.ColorVerbIrregular);
        AppendRule(".ir-tok.suffix, .suffix, .ir-tok.ir-pos-suffix, .ir-pos-suffix", s.ColorSuffix);
        AppendRule(".ir-tok.particle-single, .particle-single, .ir-tok.ir-pos-prt-single, .ir-pos-prt-single, .ir-tok.particle:not(.particle-multi)", s.ColorParticleSingle);
        AppendRule(".ir-tok.particle-multi, .particle-multi, .ir-tok.ir-pos-prt-multi, .ir-pos-prt-multi", s.ColorParticleMulti);
        AppendRule(".ir-tok.adverb, .adverb, .ir-tok.ir-pos-adverb, .ir-pos-adverb", s.ColorAdverb);
        AppendRule(".ir-tok.noun, .noun, .ir-tok.ir-pos-noun, .ir-pos-noun", s.ColorNoun);
        AppendRule(".ir-tok.conjunction, .conjunction, .ir-tok.ir-pos-conj, .ir-pos-conj", s.ColorConjunction);
        AppendRule(".ir-tok.auxiliary, .auxiliary, .ir-tok.ir-pos-aux, .ir-pos-aux", s.ColorAuxiliary);
        AppendRule(".ir-tok.interjection, .interjection, .ir-tok.ir-pos-intj, .ir-pos-intj", s.ColorInterjection);

        return sb.ToString();
    }
}
