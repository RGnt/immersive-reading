using System.Text;
using AngleSharp.Dom;
using ImmersiveReading.Core.Plugins;

namespace ImmersiveReading.Enrichment.Dom;

public sealed class DomTextSlice
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

public static class BlockDiscovery
{
    public static readonly HashSet<string> SkippedTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "svg", "math", "head", "rt", "rp"
    };

    public static readonly HashSet<string> BlockTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "li", "blockquote", "dt", "dd", "caption"
    };

    public static List<IElement> DiscoverBlocks(IDocument document)
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

    public static void CollectBlockSlices(
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
}

