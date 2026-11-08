using System.Text;

namespace ImmersiveReading.Library.Internal;

internal static class AtomicFile
{
    public static void WriteAllText(string destinationPath, string content)
    {
        string dir = Path.GetDirectoryName(destinationPath)!;
        Directory.CreateDirectory(dir);
        string tempPath = Path.Combine(dir, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}

