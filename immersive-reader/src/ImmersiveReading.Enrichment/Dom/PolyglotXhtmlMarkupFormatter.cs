using System.Text;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html;

namespace ImmersiveReading.Enrichment.Dom;

public sealed class PolyglotXhtmlMarkupFormatter : HtmlMarkupFormatter
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

        foreach (var attr in element.Attributes)
        {
            sb.Append(' ').Append(Attribute(attr));
        }

        if (VoidElements.Contains(element.LocalName))
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
        if (VoidElements.Contains(element.LocalName))
        {
            return "";
        }

        var sb = new StringBuilder();
        sb.Append("</");
        if (!string.IsNullOrEmpty(element.Prefix))
        {
            sb.Append(element.Prefix).Append(':');
        }
        sb.Append(element.LocalName).Append('>');
        return sb.ToString();
    }
}

