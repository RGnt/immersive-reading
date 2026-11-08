using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImmersiveReading.Library.StudyPack;

public static class PublicationReader
{
    private static readonly Regex SafeHex32Regex = new("^[a-f0-9]{32}$", RegexOptions.Compiled);

    public static (string? Generation, string? ManifestPath, JsonDocument? ManifestDoc) TryReadActivePublication(string outputRoot)
    {
        string activePath = Path.Combine(outputRoot, ".publication", "active.json");
        if (!File.Exists(activePath))
        {
            return (null, null, null);
        }

        try
        {
            using var activeDoc = JsonDocument.Parse(File.ReadAllText(activePath, Encoding.UTF8));
            var root = activeDoc.RootElement;
            string? gen = root.TryGetProperty("generation", out var gProp) ? gProp.GetString() : null;
            string? relManifest = root.TryGetProperty("manifest", out var mProp) ? mProp.GetString() : null;
            string? expectedSha = root.TryGetProperty("sha256", out var sProp) ? sProp.GetString() : null;

            if (string.IsNullOrEmpty(gen) || !SafeHex32Regex.IsMatch(gen) || string.IsNullOrEmpty(relManifest))
            {
                return (null, null, null);
            }

            string fullManifestPath = Path.GetFullPath(Path.Combine(outputRoot, relManifest.Replace('/', Path.DirectorySeparatorChar)));
            if (!fullManifestPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullManifestPath))
            {
                return (null, null, null);
            }

            if (!string.IsNullOrEmpty(expectedSha))
            {
                byte[] manifestBytes = File.ReadAllBytes(fullManifestPath);
                string actualSha = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    return (null, null, null);
                }
            }

            var manifestDoc = JsonDocument.Parse(File.ReadAllText(fullManifestPath, Encoding.UTF8));
            return (gen, fullManifestPath, manifestDoc);
        }
        catch
        {
            return (null, null, null);
        }
    }

    public static string ResolveBookSubdir(string outputRoot, string? studySnapshotRoot, string category, string bookId)
    {
        if (!string.IsNullOrEmpty(studySnapshotRoot))
        {
            string snapCandidate = Path.Combine(studySnapshotRoot, category, bookId);
            if (Directory.Exists(snapCandidate))
            {
                return snapCandidate;
            }
        }
        return Path.Combine(outputRoot, category, bookId);
    }
}

