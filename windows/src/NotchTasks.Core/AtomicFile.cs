using System.Text;

namespace NotchTasks.Core;

public static class AtomicFile
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// Writes a temporary file next to the target, then renames it over the target, so a crash never leaves half a file.
    public static void WriteAllText(string path, string text)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = $"{full}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(tmp, text, Utf8);
            MoveWithRetry(tmp, full);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private const int MoveAttempts = 3;

    /// Windows can refuse the rename for a moment while another process reads the target (a watcher, an antivirus scan).
    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to, overwrite: true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < MoveAttempts)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// The file's text, or null when there is no file.
    public static string? ReadAllTextOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path, Utf8);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
