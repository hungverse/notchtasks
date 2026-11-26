using System.Globalization;
using System.Text.Json;

namespace NotchTasks.Core.Limits;

/// What `NotchTasksHook.exe --statusline` does with Claude Code's statusline JSON (Mac: mac/notch-statusline.sh; Electron:
/// runStatusline in electron/src/main/main.js). The JSON is saved for ClaudeUsage.FromStatusline, and one line is printed.
public static class ClaudeStatusline
{
    /// The printed limits, "5h 12% · 7d 3%": the integer part of each window's used_percentage, for the windows that are
    /// there (five_hour, then seven_day). Empty when the input is not JSON or has no window.
    public static string Summary(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var (key, label) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
            {
                if (rl.TryGetProperty(key, out var window)
                    && window.ValueKind == JsonValueKind.Object
                    && window.TryGetProperty("used_percentage", out var used)
                    && used.ValueKind == JsonValueKind.Number
                    && used.TryGetDouble(out var pct))
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{label} {Math.Truncate(pct):0}%"));
                }
            }
            return string.Join(" · ", parts);
        }
    }

    /// Saves the statusline JSON as <dir>\notchtasks-status.json, replacing the old one in one step (a temporary file, then a
    /// rename). The folder is created when it is missing. Throws IOException or UnauthorizedAccessException when it cannot write.
    public static void Save(string dir, string json) =>
        AtomicFile.WriteAllText(Path.Combine(dir, ClaudeUsage.StatusFile), json);
}
