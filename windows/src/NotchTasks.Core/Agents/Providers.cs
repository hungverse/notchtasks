using System.Globalization;
using System.Text;

namespace NotchTasks.Core.Agents;

/// File and text helpers the agent scanners share (Electron: providers.js lastComponent, parseIso, readTail, readRange).
public static class Providers
{
    /// The bytes of a rollout that a scan reads from its end: 1 MB, as providers.js readTail.
    public const int TailBytes = 1_000_000;

    /// The bytes of a rollout that identify it (its head line: cwd, session id, parent thread).
    public const int HeadBytes = 16_384;

    /// The last path component for both \ and /; trailing separators are ignored. Empty when there is none.
    public static string LastComponent(string? p)
    {
        var trimmed = (p ?? string.Empty).TrimEnd('\\', '/');
        var cut = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
    }

    /// An ISO 8601 time, or null when it does not parse.
    public static DateTimeOffset? ParseIso(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

    /// The first `bytes` of a file as text; null when it cannot be read.
    public static string? ReadHead(string file, int bytes = HeadBytes)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var len = (int)Math.Min(bytes, fs.Length);
            return Encoding.UTF8.GetString(ReadRange(fs, 0, len));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// The last `bytes` of a file as complete lines (see CompleteLines). Empty when it cannot be read.
    public static string ReadTail(string file, int bytes = TailBytes)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var size = fs.Length;
            var from = size > bytes ? size - bytes : 0;
            var buf = ReadRange(fs, from, (int)(size - from));
            return CompleteLines(buf, startsInsideLine: from > 0).Text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// Reads up to `len` bytes at `pos`; fewer when the file ends first.
    public static byte[] ReadRange(FileStream fs, long pos, int len)
    {
        var buf = new byte[len];
        fs.Position = pos;
        var got = 0;
        while (got < len)
        {
            var n = fs.Read(buf, got, len - got);
            if (n == 0)
            {
                break;
            }
            got += n;
        }
        if (got < len)
        {
            Array.Resize(ref buf, got);
        }
        return buf;
    }

    /// The complete lines in a read. When the read began inside a line, that partial first line is dropped. A last line
    /// with no newline yet is a write in progress and is left out. Used is the number of bytes up to the last newline,
    /// so a caller resumes there next time.
    public static (string Text, int Used) CompleteLines(byte[] buf, bool startsInsideLine)
    {
        var start = 0;
        if (startsInsideLine)
        {
            var first = Array.IndexOf(buf, (byte)'\n');
            start = first < 0 ? buf.Length : first + 1;
        }
        var end = Array.LastIndexOf(buf, (byte)'\n') + 1;
        var text = end > start ? Encoding.UTF8.GetString(buf, start, end - start) : string.Empty;
        return (text, end);
    }
}
