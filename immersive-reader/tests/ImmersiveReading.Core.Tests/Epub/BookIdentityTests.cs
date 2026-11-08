using System.Text;
using ImmersiveReading.Core.Epub;
using Xunit;

namespace ImmersiveReading.Core.Tests.Epub;

public sealed class BookIdentityTests
{
    private const string ExpectedFileHex = "89ad234d4d4d72f1d876a377d1789e0c3363c5b999d371b442379d2ac0e9c216";
    private const string ExpectedBookId = "book-c4f09c425b74418b";
    private static readonly byte[] TestBytes = Encoding.UTF8.GetBytes("hello epub test data");

    [Fact]
    public void ComputeFromBytes_MatchesDeterministicExpected()
    {
        var (bookId, fullHex) = BookIdentity.ComputeFromBytes(TestBytes);
        Assert.Equal(ExpectedFileHex, fullHex);
        Assert.Equal(ExpectedBookId, bookId);
    }

    [Fact]
    public void ComputeFromStream_MatchesBytesAndRewindsStream()
    {
        using var ms = new MemoryStream(TestBytes);
        var (bookId, fullHex) = BookIdentity.ComputeFromStream(ms);
        Assert.Equal(ExpectedFileHex, fullHex);
        Assert.Equal(ExpectedBookId, bookId);
        Assert.Equal(0, ms.Position);
    }

    [Fact]
    public async Task ComputeFromFileAsync_MatchesBytesAndExpected()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(tempFile, TestBytes);
            var (bookId, fullHex) = await BookIdentity.ComputeFromFileAsync(tempFile);
            Assert.Equal(ExpectedFileHex, fullHex);
            Assert.Equal(ExpectedBookId, bookId);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}

