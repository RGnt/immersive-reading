using AngleSharp.Dom;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment.Dom;

public static class ReaderHeadInjector
{
    public static void InjectHeadAssetsAndClasses(IDocument document, EnrichmentOptions options)
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
}

