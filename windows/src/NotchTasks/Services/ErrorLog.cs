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
        Debug.WriteLine("NotchTasks: unhandled: " + error);
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            File.AppendAllText(
                Path.Combine(AppPaths.DataDir, "windows-errors.log"),
                $"[{stamp}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("NotchTasks: could not write the error log: " + e);
        }
    }
}
