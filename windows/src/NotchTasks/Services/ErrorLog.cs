using System.IO;
using System.Diagnostics;
using System.Globalization;
using NotchTasks.Core;

namespace NotchTasks.Services;

/// Appends unhandled exceptions to %APPDATA%\NotchTasks\windows-errors.log. Never throws.
public static class ErrorLog
{
    /// Where the log is written, when not the app's own file. The test assembly points it at a temp file before any test runs, so the
    /// suite never writes the developer's log; the app never sets it.
    public static string? FileOverride { get; set; }

    private static string FilePath => FileOverride ?? Path.Combine(AppPaths.DataDir, "windows-errors.log");

    public static void Write(Exception error)
    {
        try
        {
            Debug.WriteLine("NotchTasks: unhandled: " + error);
            var path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppPaths.DataDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            File.AppendAllText(path, $"[{stamp}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never throw: a failed write is dropped.
        }
    }
}
