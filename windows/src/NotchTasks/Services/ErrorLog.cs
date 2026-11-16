using System.IO;
using System.Diagnostics;
using System.Globalization;
using NotchTasks.Core;

namespace NotchTasks.Services;

/// Appends unhandled exceptions to %APPDATA%\NotchTasks\windows-errors.log. Never throws.
public static class ErrorLog
{
    public static void Write(Exception error)
    {
        try
        {
            Debug.WriteLine("NotchTasks: unhandled: " + error);
            Directory.CreateDirectory(AppPaths.DataDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            File.AppendAllText(
                Path.Combine(AppPaths.DataDir, "windows-errors.log"),
                $"[{stamp}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never throw: a failed write is dropped.
        }
    }
}
