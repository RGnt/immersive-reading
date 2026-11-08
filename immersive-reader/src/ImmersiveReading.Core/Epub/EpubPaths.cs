using System.IO.Compression;

namespace ImmersiveReading.Core.Epub;

public static class EpubPaths
{
    public static string NormalizePosixPath(string path)
    {
        string p = path.Replace('\\', '/').TrimStart('/');
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(parts.Length);
        foreach (string part in parts)
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            }
            else
            {
                stack.Add(part);
            }
        }
        return string.Join('/', stack);
    }

    public static string GetPosixDirectory(string posixPath)
    {
        string norm = NormalizePosixPath(posixPath);
        int idx = norm.LastIndexOf('/');
        return idx < 0 ? "" : norm[..idx];
    }

    public static string ResolvePosixPath(string baseDir, string relativeHref)
    {
        if (string.IsNullOrEmpty(baseDir))
        {
            return NormalizePosixPath(relativeHref);
        }
        return NormalizePosixPath($"{baseDir}/{relativeHref}");
    }

    public static ZipArchiveEntry? GetEntryNormalized(ZipArchive archive, string posixPath)
    {
        string target = NormalizePosixPath(posixPath);
        foreach (var entry in archive.Entries)
        {
            if (string.Equals(NormalizePosixPath(entry.FullName), target, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }
}

