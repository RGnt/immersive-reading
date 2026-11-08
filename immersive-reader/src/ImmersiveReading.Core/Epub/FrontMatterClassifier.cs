using System.IO.Compression;
using System.Text;
using AngleSharp.Dom;

namespace ImmersiveReading.Core.Epub;

internal static class FrontMatterClassifier
{
    public static readonly HashSet<string> FrontMatterTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "表紙", "扉", "とびら", "口絵", "カラー口絵", "カラーイラスト", "ピンナップ", "イラスト",
        "目次", "もくじ", "凡例", "はじめに", "まえがき", "前書き", "注意", "利用上の注意", "ご利用上の注意", "規約", "利用規約",
        "cover", "titlepage", "title page", "illustrations", "color insert", "contents",
        "table of contents", "toc", "preface", "introduction", "foreword", "caution", "notice"
    };

    public static readonly HashSet<string> BackMatterTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "奥付", "おくづけ", "あとがき", "後書き", "解説", "著者紹介", "著作権", "クレジット", "広告", "裏表紙",
        "colophon", "copyright", "about the author", "profile", "advertisement", "afterword", "acknowledgments"
    };

    public static string NormalizeTitleKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string nfkc = text.Normalize(NormalizationForm.FormKC).Trim();
        return System.Text.RegularExpressions.Regex.Replace(nfkc, @"[\s\u3000\-_・:：〈〉<>（）\(\)\[\]「」『』]+", "").ToLowerInvariant();
    }

    public static bool IsFrontMatterTitle(string title)
    {
        string norm = NormalizeTitleKeyword(title);
        if (string.IsNullOrEmpty(norm)) return true;
        if (FrontMatterTitles.Contains(norm)) return true;
        foreach (var kw in FrontMatterTitles)
        {
            if (norm.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsBackMatterTitle(string title)
    {
        string norm = NormalizeTitleKeyword(title);
        if (string.IsNullOrEmpty(norm)) return false;
        if (BackMatterTitles.Contains(norm)) return true;
        foreach (var kw in BackMatterTitles)
        {
            if (norm.StartsWith(kw, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool IsFrontOrBackMatter(string title, string href)
    {
        if (IsFrontMatterTitle(title) || IsBackMatterTitle(title)) return true;
        string fileName = Path.GetFileNameWithoutExtension(href).ToLowerInvariant();
        return fileName is "cover" or "toc" or "nav" or "colophon" or "copyright" or "caution";
    }

    public static string IdentifyFrontMatterTitle(ZipArchive archive, string spineHref, int spineIndex)
    {
        string fn = Path.GetFileNameWithoutExtension(spineHref).ToLowerInvariant();
        if (fn.Contains("cover")) return "表紙 (Cover)";
        if (fn.Contains("toc") || fn.Contains("nav") || fn.Contains("contents") || fn.Contains("moku")) return "目次 (Contents)";
        if (fn.Contains("caution") || fn.Contains("notice")) return "注意 (Notice)";
        if (fn.Contains("color") || fn.Contains("insert") || fn.Contains("illust") || fn.Contains("kuchie")) return "口絵 (Illustrations)";

        var entry = EpubPaths.GetEntryNormalized(archive, spineHref);
        if (entry is not null)
        {
            try
            {
                var doc = EpubArchiveReader.ParseXmlOrHtml(EpubArchiveReader.ReadEntryString(entry));

                int charCount = 0;
                var body = doc.Body ?? doc.DocumentElement;
                if (body is not null)
                {
                    CountTextNodeCharsRecursive(body, ref charCount);
                }

                bool hasImg = doc.QuerySelector("img, image, svg") is not null;
                if (hasImg && charCount <= 50)
                {
                    if (spineIndex == 0 || fn.Contains("titlepage") || fn.Contains("cover"))
                    {
                        return "表紙 (Cover)";
                    }
                    return "口絵 (Illustrations)";
                }

                var h1 = body?.QuerySelector("h1, h2, h3");
                if (h1 is not null)
                {
                    string hText = h1.TextContent.Trim();
                    if (!string.IsNullOrWhiteSpace(hText) && hText.Length <= 40)
                    {
                        return hText;
                    }
                }
            }
            catch
            {
                // Ignore
            }
        }

        if (fn.Contains("title")) return "扉 (Title Page)";
        return spineIndex == 0 ? "表紙 (Cover)" : $"Introduction (Page {spineIndex + 1})";
    }

    public static bool IsIllustrationSpineItem(ZipArchive archive, string spineHref)
    {
        string fn = Path.GetFileNameWithoutExtension(spineHref).ToLowerInvariant();
        if (fn.Contains("color") || fn.Contains("insert") || fn.Contains("illust") || fn.Contains("image"))
        {
            return true;
        }

        var entry = EpubPaths.GetEntryNormalized(archive, spineHref);
        if (entry is null) return false;

        try
        {
            var doc = EpubArchiveReader.ParseXmlOrHtml(EpubArchiveReader.ReadEntryString(entry));
            bool hasImg = doc.QuerySelector("img, image, svg") is not null;
            if (!hasImg) return false;

            int charCount = 0;
            var body = doc.Body ?? doc.DocumentElement;
            if (body is not null)
            {
                CountTextNodeCharsRecursive(body, ref charCount);
            }
            return charCount <= 50;
        }
        catch
        {
            return false;
        }
    }

    public static int FindFirstStorySpineIndex(ZipArchive archive, List<string> spineHrefs)
    {
        for (int i = 0; i < spineHrefs.Count; i++)
        {
            string href = spineHrefs[i];
            string fn = Path.GetFileNameWithoutExtension(href).ToLowerInvariant();
            if (fn.Contains("cover") || fn.Contains("toc") || fn.Contains("nav") || fn.Contains("caution") || fn.Contains("titlepage"))
            {
                continue;
            }

            var entry = EpubPaths.GetEntryNormalized(archive, href);
            if (entry is null) continue;

            try
            {
                var doc = EpubArchiveReader.ParseXmlOrHtml(EpubArchiveReader.ReadEntryString(entry));
                int charCount = 0;
                var body = doc.Body ?? doc.DocumentElement;
                if (body is not null)
                {
                    CountTextNodeCharsRecursive(body, ref charCount);
                }

                if (charCount > 80)
                {
                    return i;
                }
            }
            catch
            {
                // Ignore
            }
        }
        return 0;
    }

    public static int CountNormalizedChapterCharacters(ZipArchiveEntry entry)
    {
        var doc = EpubArchiveReader.ParseXmlOrHtml(EpubArchiveReader.ReadEntryString(entry));
        var body = doc.Body ?? doc.DocumentElement;
        if (body is null) return 0;

        int count = 0;
        CountTextNodeCharsRecursive(body, ref count);
        return count;
    }

    public static void CountTextNodeCharsRecursive(INode node, ref int count)
    {
        if (node is IElement el)
        {
            string local = el.LocalName;
            if (string.Equals(local, "rt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "rp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "script", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "style", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(local, "head", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        if (node is IText textNode)
        {
            foreach (char c in textNode.Data)
            {
                if (!char.IsWhiteSpace(c))
                {
                    count++;
                }
            }
            return;
        }

        foreach (var child in node.ChildNodes)
        {
            CountTextNodeCharsRecursive(child, ref count);
        }
    }
}

