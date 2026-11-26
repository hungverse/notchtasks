using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Limits;

/// Claude's plan limits: the statusline snapshot, and the reading of `claude -p /usage`
/// (Mac: claudeLimits/claudeNow; electron/src/core/providers.js claudeLimits, parseClaudeUsage, parseResetWhen).
public static partial class ClaudeUsage
{
    private const string BundleId = "com.anthropic.claudefordesktop";
    /// The statusline snapshot's file name in the Claude config folder (written by ClaudeStatusline.Save).
    public const string StatusFile = "notchtasks-status.json";
    private static readonly string[] Months = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

    [GeneratedRegex(@"(\d+(?:\.\d+)?)% used")]
    private static partial Regex UsedRe();

    [GeneratedRegex(@"resets (.+?) \(([^)]+)\)")]
    private static partial Regex ResetsRe();

    [GeneratedRegex(@"^([A-Z]{3}) (\d{1,2})(?: AT (\d{1,2})(?::(\d{2}))?(AM|PM))?$")]
    private static partial Regex WhenRe();

    /// Claude Code only exposes plan limits to the statusline: notch-statusline.sh saves that JSON in the config folder.
    /// The 5h and 7d windows whose reset has not passed; null when there are none or the file is missing.
    /// Updated is the file's write time (now when it cannot be read).
    public static ProviderLimits? FromStatusline(string dir, DateTimeOffset now)
    {
        var file = Path.Combine(dir, StatusFile);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(Providers.ReadAllTextShared(file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        using (doc)
        {
            var rl = Get(doc.RootElement, "rate_limits");
            if (rl.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var nowSeconds = now.ToUnixTimeMilliseconds() / 1000.0;
            var windows = new List<LimitWindow>();
            foreach (var (key, label) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
            {
                var w = Get(rl, key);
                if (w.ValueKind != JsonValueKind.Object) continue;
                var used = Get(w, "used_percentage");
                var resets = Get(w, "resets_at");
                if (used.ValueKind != JsonValueKind.Number || resets.ValueKind != JsonValueKind.Number) continue;
                if (!used.TryGetDouble(out var usedPct) || !resets.TryGetDouble(out var resetSeconds)) continue;
                if (resetSeconds <= nowSeconds) continue;
                windows.Add(new LimitWindow(label, usedPct, DateTimeOffset.FromUnixTimeMilliseconds((long)(resetSeconds * 1000))));
            }

            if (windows.Count == 0)
            {
                return null;
            }

            DateTimeOffset updated;
            try
            {
                updated = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                updated = now;
            }
            return new ProviderLimits("claude", "Claude", BundleId, windows, updated);
        }
    }

    /// The limits the Mac shows for Claude (Model.claudeNow): the live CLI reading when it is younger than 15 min
    /// (windows past their reset count as unused), else the statusline snapshot.
    public static ProviderLimits? Now(IReadOnlyList<LimitWindow>? live, DateTimeOffset? liveAt, ProviderLimits? statusline, DateTimeOffset now)
    {
        if (live is null || liveAt is not { } at || now - at >= TimeSpan.FromMinutes(15))
        {
            return statusline;
        }
        var windows = live.Select(w => w.Resets > now ? w : w with { Used = 0 }).ToList();
        return new ProviderLimits("claude", "Claude", BundleId, windows, at);
    }

    /// "Current session: 34% used · resets Nov 3 at 8:09pm (Asia/Saigon)" gives the 5h window; "Current week (all models)"
    /// gives 7d. A reset that does not parse keeps the default (5 h or 7 d from now). Lines with no percentage give nothing.
    public static IReadOnlyList<LimitWindow> Parse(string cliText, DateTimeOffset now)
    {
        var list = new List<LimitWindow>();
        foreach (var line in cliText.Split('\n'))
        {
            var label = line.StartsWith("Current session", StringComparison.Ordinal) ? "5h"
                : line.StartsWith("Current week (all models)", StringComparison.Ordinal) ? "7d"
                : null;
            if (label is null) continue;
            var m = UsedRe().Match(line);
            if (!m.Success) continue;

            var resets = now + (label == "5h" ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7));
            var r = ResetsRe().Match(line);
            if (r.Success && ParseResetWhen(r.Groups[1].Value, r.Groups[2].Value, now) is { } when)
            {
                resets = when;
            }
            list.Add(new LimitWindow(label, double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture), resets));
        }
        return list;
    }

    /// "Nov 3 at 8:09pm" in the IANA zone zoneName gives the instant. The year is this year in that zone, minutes default
    /// to 0, and a time more than a day in the past means next year. An unknown zone uses this PC's zone. Null for an unknown shape.
    public static DateTimeOffset? ParseResetWhen(string when, string zoneName, DateTimeOffset now)
    {
        var m = WhenRe().Match(when.ToUpperInvariant());
        if (!m.Success) return null;
        var month = Array.IndexOf(Months, m.Groups[1].Value);
        if (month < 0) return null;

        var zone = ValidZone(zoneName);
        var tz = ZoneOf(zone);
        var yearNow = TimeZoneInfo.ConvertTime(now, tz).Year;
        var h = 0;
        if (m.Groups[3].Success)
        {
            h = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) % 12 + (m.Groups[5].Value == "PM" ? 12 : 0);
        }
        var mi = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
        var day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);

        var d = ZonedToDate(yearNow, month + 1, day, h, mi, tz);
        if (d < now - TimeSpan.FromDays(1))
        {
            d = ZonedToDate(yearNow + 1, month + 1, day, h, mi, tz);
        }
        return d;
    }

    /// The offset of the zone from UTC at the instant `at`, in ms (JS tzOffsetMs). An unknown zone uses this PC's zone.
    public static double TzOffsetMs(DateTimeOffset at, string zone) =>
        OffsetMs(at.ToUnixTimeMilliseconds(), ZoneOf(ValidZone(zone)));

    /// A wall-clock time (month 1-12) in the IANA zone as an instant. Day and minute overflow roll over, as JS Date.UTC does.
    /// An unknown zone uses this PC's zone.
    public static DateTimeOffset ZonedToDate(int y, int mo, int d, int h, int mi, string? zone) =>
        ZonedToDate(y, mo, d, h, mi, ZoneOf(ValidZone(zone)));

    /// The zone's name when Windows knows it (IANA names work on .NET 6+ with ICU), else null.
    public static string? ValidZone(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(name);
            return name;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static TimeZoneInfo ZoneOf(string? name) =>
        name is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(name);

    private static double OffsetMs(long ms, TimeZoneInfo tz) =>
        tz.GetUtcOffset(DateTimeOffset.FromUnixTimeMilliseconds(ms)).TotalMilliseconds;

    private static DateTimeOffset ZonedToDate(int y, int mo, int d, int h, int mi, TimeZoneInfo tz)
    {
        var wall = new DateTime(y, 1, 1).AddMonths(mo - 1).AddDays(d - 1).AddHours(h).AddMinutes(mi);
        var guess = new DateTimeOffset(wall, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var t = guess - (long)OffsetMs(guess, tz);
        var off2 = (long)OffsetMs(t, tz);
        if (off2 != (long)OffsetMs(guess, tz))
        {
            t = guess - off2;
        }
        return DateTimeOffset.FromUnixTimeMilliseconds(t);
    }

    private static JsonElement Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}
