using System.Text;

namespace ImmersiveReading.App.Hosting;

/// <summary>
/// Handles <c>ir-app://</c> custom scheme requests for static web UI assets, covers, and study-pack outputs.
/// </summary>
public sealed class AppSchemeHandler
{
    private readonly string _wwwrootDir;
    private readonly string _coversDir;
    private readonly string? _extractorOutputDir;

    public AppSchemeHandler(string wwwrootDir, string coversDir, string? extractorOutputDir = null)
    {
        _wwwrootDir = Path.GetFullPath(wwwrootDir);
        _coversDir = Path.GetFullPath(coversDir);
        _extractorOutputDir = !string.IsNullOrEmpty(extractorOutputDir) ? Path.GetFullPath(extractorOutputDir) : null;
    }

    public Stream Handle(string url, out string contentType)
    {
        return HandleRequest(url, _wwwrootDir, _coversDir, _extractorOutputDir, out contentType);
    }

    public static Stream HandleRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        out string contentType)
        => HandleRequest(url, wwwrootDir, coversDir, extractorOutputDir: null, out contentType);

    public static Stream HandleRequest(
        string url,
        string wwwrootDir,
        string coversDir,
        string? extractorOutputDir,
        out string contentType)
    {
        contentType = "text/plain; charset=utf-8";
        try
        {
            var uri = new Uri(url);
            string host = uri.Host.ToLowerInvariant();
            string relativePath = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));

            // Prevent path traversal
            if (relativePath.Contains("..", StringComparison.Ordinal) || host.Contains("..", StringComparison.Ordinal))
            {
                return new MemoryStream([]);
            }

            string fullWwwroot = Path.GetFullPath(wwwrootDir);
            string fullCovers = Path.GetFullPath(coversDir);
            string? fullExtractor = !string.IsNullOrEmpty(extractorOutputDir) ? Path.GetFullPath(extractorOutputDir) : null;

            if (host == "covers" || relativePath.StartsWith("covers/", StringComparison.OrdinalIgnoreCase))
            {
                string fileName = host == "covers" ? Path.GetFileName(relativePath) : Path.GetFileName(relativePath["covers/".Length..]);
                string fullCoverPath = Path.GetFullPath(Path.Combine(fullCovers, fileName));
                if (IsUnderRoot(fullCoverPath, fullCovers) && File.Exists(fullCoverPath))
                {
                    contentType = GetMimeType(fullCoverPath);
                    return new FileStream(fullCoverPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return new MemoryStream([]);
            }

            if (fullExtractor != null &&
                (host == "studypack" || relativePath.StartsWith("studypack/", StringComparison.OrdinalIgnoreCase)))
            {
                string subPath = host == "studypack" ? relativePath : relativePath["studypack/".Length..];
                string fullStudyPath = Path.GetFullPath(Path.Combine(fullExtractor, subPath.Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnderRoot(fullStudyPath, fullExtractor) && File.Exists(fullStudyPath))
                {
                    contentType = GetMimeType(fullStudyPath);
                    return new FileStream(fullStudyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                return new MemoryStream([]);
            }

            // When URL is e.g. "ir-app://assets/ir-reader-injected.css", uri.Host is "assets" and uri.AbsolutePath is "/ir-reader-injected.css".
            if (!string.IsNullOrEmpty(host) && host != "app" && host != "localhost")
            {
                string hostCombined = Path.GetFullPath(Path.Combine(fullWwwroot, host, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (IsUnderRoot(hostCombined, fullWwwroot) && File.Exists(hostCombined))
                {
                    contentType = GetMimeType(hostCombined);
                    return new FileStream(hostCombined, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
            }

            string targetFile = Path.GetFullPath(Path.Combine(fullWwwroot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsUnderRoot(targetFile, fullWwwroot) && File.Exists(targetFile))
            {
                contentType = GetMimeType(targetFile);
                return new FileStream(targetFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            string distTarget = Path.GetFullPath(Path.Combine(fullWwwroot, "dist", relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsUnderRoot(distTarget, fullWwwroot) && File.Exists(distTarget))
            {
                contentType = GetMimeType(distTarget);
                return new FileStream(distTarget, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }
        catch
        {
            // Fall through
        }

        return new MemoryStream(Encoding.UTF8.GetBytes("Not found"));
    }

    private static bool IsUnderRoot(string fullPath, string rootPath)
    {
        string normalizedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetMimeType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }
}

