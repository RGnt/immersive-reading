using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ImmersiveReading.Library.StudyPack;

namespace ImmersiveReading.Library;

public sealed class StudyPackLease : IDisposable
{
    private FileStream? _lockStream;
    public string Generation { get; }
    public string LockFilePath { get; }
    public bool IsActive => _lockStream is not null;

    internal StudyPackLease(string generation, string lockFilePath, FileStream lockStream)
    {
        Generation = generation;
        LockFilePath = lockFilePath;
        _lockStream = lockStream;
    }

    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _lockStream, null);
        if (stream is null) return;

        try
        {
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                try
                {
                    stream.Unlock(0, 1);
                }
                catch
                {
                    // Ignore unlock errors on close
                }
            }
        }
        finally
        {
            stream.Dispose();
        }

        try
        {
            if (File.Exists(LockFilePath))
            {
                File.Delete(LockFilePath);
            }
        }
        catch
        {
            // Ignore cleanup race
        }
    }
}

public static class SnapshotLeaseManager
{
    private static readonly Regex SafeHex32Regex = new("^[a-f0-9]{32}$", RegexOptions.Compiled);

    public static StudyPackLease? AcquireSnapshotLease(string outputRoot, string? generation = null)
    {
        string? targetGen = generation;
        if (string.IsNullOrEmpty(targetGen))
        {
            var (activeGen, _, doc) = PublicationReader.TryReadActivePublication(outputRoot);
            doc?.Dispose();
            targetGen = activeGen;
        }

        if (string.IsNullOrEmpty(targetGen) || !SafeHex32Regex.IsMatch(targetGen))
        {
            return null;
        }

        string leaseDir = Path.Combine(outputRoot, ".publication", "leases", targetGen);
        Directory.CreateDirectory(leaseDir);

        string leaseId = Guid.NewGuid().ToString("N");
        string lockFilePath = Path.Combine(leaseDir, $"{leaseId}.lock");

        var fs = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }));
            fs.SetLength(0);
            fs.Write(payload, 0, payload.Length);
            fs.Flush(flushToDisk: true);

            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                fs.Lock(0, 1);
            }

            return new StudyPackLease(targetGen, lockFilePath, fs);
        }
        catch
        {
            fs.Dispose();
            return null;
        }
    }
}
