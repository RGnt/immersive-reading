using System;
using System.Collections.Generic;
using System.Text;
using AngleSharp.Dom;
using ImmersiveReading.Core.Extensions;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Plugins.PosHighlighter;

public sealed class PosDomEnrichmentStage : IDomEnrichmentStage
{
    public string StageId => "pos-highlighter";
    public int Order => 200;

    public PosHighlighterSettings Settings { get; set; }

    public PosDomEnrichmentStage(PosHighlighterSettings? initialSettings = null)
    {
        Settings = initialSettings ?? new PosHighlighterSettings();
    }

    public void Enrich(IDocument document, DomEnrichmentContext context)
    {
        if (!Settings.Enabled)
        {
            return;
        }

        // Map tokens by TokenId
        var tokenMap = new Dictionary<int, EnrichedTokenEntry>(context.Tokens.Count);
        foreach (var tok in context.Tokens)
        {
            tokenMap[tok.TokenId] = tok;
        }

        var tokenElements = document.QuerySelectorAll(".ir-tok");
        foreach (var el in tokenElements)
        {
            var tidAttr = el.GetAttribute("data-tid");
            if (string.IsNullOrEmpty(tidAttr) || !int.TryParse(tidAttr, out int tid))
            {
                continue;
            }

            if (!tokenMap.TryGetValue(tid, out var tokenEntry))
            {
                continue;
            }

            var category = PosClassifier.Classify(tokenEntry.Token);
            if (category == PosCategory.Unknown)
            {
                continue;
            }

            string cssClass = PosClassifier.ToCssClass(category);
            if (!string.IsNullOrEmpty(cssClass))
            {
                el.ClassList.Add(cssClass);
                el.SetAttribute("data-ir-pos", PosClassifier.ToCode(category));
            }
        }

        InjectStyles(document);
    }

    private void InjectStyles(IDocument document)
    {
        var head = document.Head;
        if (head == null)
        {
            return;
        }

        // Remove existing style tag if present to avoid duplication
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

        void AppendRule(string className, string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor)) return;

            if (string.Equals(style, "underline", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($".{className} {{ text-decoration: underline; text-decoration-color: {hexColor}; text-underline-offset: 3px; }}");
            }
            else if (string.Equals(style, "background", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($".{className} {{ background-color: {hexColor}26; border-radius: 3px; padding: 0 1px; }}");
            }
            else // Default: text-color
            {
                sb.AppendLine($".{className}, .{className} rt {{ color: {hexColor} !important; }}");
            }
        }

        AppendRule("ir-pos-adj-i", s.ColorAdjI);
        AppendRule("ir-pos-adj-na", s.ColorAdjNa);
        AppendRule("ir-pos-verb-u", s.ColorVerbGodan);
        AppendRule("ir-pos-verb-ru", s.ColorVerbIchidan);
        AppendRule("ir-pos-verb-irr", s.ColorVerbIrregular);
        AppendRule("ir-pos-suffix", s.ColorSuffix);
        AppendRule("ir-pos-prt-single", s.ColorParticleSingle);
        AppendRule("ir-pos-prt-multi", s.ColorParticleMulti);
        AppendRule("ir-pos-adverb", s.ColorAdverb);

        return sb.ToString();
    }
}
