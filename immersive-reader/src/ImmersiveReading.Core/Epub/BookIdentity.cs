using System.Security.Cryptography;
using System.Text;

namespace ImmersiveReading.Core.Epub;

/// <summary>
/// Computes deterministic book identifiers 100% identical to enrichement-extractor:
///   file_sha256 = hashlib.sha256(epub_bytes).hexdigest()
///   book_id = "book-" + hashlib.sha256(file_sha256.encode()).hexdigest()[:16]
/// </summary>
public static class BookIdentity
{
    public static (string BookId, string FullSha256Hex) ComputeFromBytes(ReadOnlySpan<byte> epubBytes)
    {
        Span<byte> fileHash = stackalloc byte[32];
        SHA256.HashData(epubBytes, fileHash);
        string fullHex = Convert.ToHexStringLower(fileHash);
        return (ComputeUidFromHex("book", fullHex), fullHex);
    }

    public static (string BookId, string FullSha256Hex) ComputeFromStream(Stream stream)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        Span<byte> fileHash = stackalloc byte[32];
        SHA256.HashData(stream, fileHash);

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        string fullHex = Convert.ToHexStringLower(fileHash);
        return (ComputeUidFromHex("book", fullHex), fullHex);
    }

    public static async ValueTask<(string BookId, string FullSha256Hex)> ComputeFromFileAsync(
        string filePath,
        CancellationToken ct = default)
    {
        await using var fs = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65536,
            useAsync: true);

        byte[] fileHash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        string fullHex = Convert.ToHexStringLower(fileHash);
        return (ComputeUidFromHex("book", fullHex), fullHex);
    }

    public static string ComputeUidFromHex(string prefix, string value)
    {
        Span<byte> utf8Bytes = stackalloc byte[Encoding.UTF8.GetByteCount(value)];
        Encoding.UTF8.GetBytes(value, utf8Bytes);
        Span<byte> uidHash = stackalloc byte[32];
        SHA256.HashData(utf8Bytes, uidHash);
        return $"{prefix}-{Convert.ToHexStringLower(uidHash[..8])}";
    }
}
