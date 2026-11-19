using System.Globalization;

namespace NotchTasks.Core.Limits;

/// One usage window of a provider: Used is 0-100 and Resets is when it starts over (Mac: LimitWindow).
public sealed record LimitWindow(string Label, double Used, DateTimeOffset Resets);

/// A provider's usage limits (Mac: ProviderLimits). Kind is "claude", "codex" or "process"; Windows are shortest first.
public sealed record ProviderLimits(string Kind, string Name, string? BundleID, IReadOnlyList<LimitWindow> Windows, DateTimeOffset Updated);

/// One limit badge on an agent card: the window, its label (null when narrow), and its detail text (null when calm).
public sealed record Badge(LimitWindow Window, string? Label, string? Detail);

/// The limit helpers of the Mac's Providers.swift and Views.swift (LimitCard.badges).
public static class Limit
{
    /// "5h" under a day, "7d" after (Mac: windowLabel).
    public static string WindowLabel(int minutes) =>
        minutes < 1440 ? $"{minutes / 60}h" : $"{minutes / 1440}d";

    /// When to warn: 5 % steps (84 → 80), then 99 (last call) and 100 (out: you wait for the reset). Never shown.
    public static int Step(double used) =>
        used >= 100 ? 100 : used >= 99 ? 99 : (int)Math.Floor(used / 5) * 5;

    /// When it resets, as short as it stays clear: the time today, the weekday and time within a week, else the date.
    /// In the zone tz, with the culture's patterns (Mac: LimitWindow.resetText).
    public static string ResetText(LimitWindow w, DateTimeOffset now, CultureInfo c, TimeZoneInfo tz)
    {
        var at = TimeZoneInfo.ConvertTime(w.Resets, tz);
        var today = TimeZoneInfo.ConvertTime(now, tz);
        var dt = c.DateTimeFormat;
        if (at.Date == today.Date)
        {
            return at.ToString(dt.ShortTimePattern, c);
        }
        if (w.Resets - now < TimeSpan.FromDays(6))
        {
            return at.ToString("ddd " + dt.ShortTimePattern, c);
        }
        return at.ToString(dt.MonthDayPattern.Replace("MMMM", "MMM"), c);
    }

    /// The badges for a card (Mac: LimitCard.badges). The window at 90 % or more that resets last is the one that
    /// "presses"; it is always shown. Narrow 0 is the full card, 1 drops the labels of calm windows, 2 shows only
    /// windows at 70 % or more. Shown is in window order; Hidden are the rest. Reset texts use the current culture and zone.
    public static (IReadOnlyList<Badge> Shown, IReadOnlyList<LimitWindow> Hidden) Badges(
        IReadOnlyList<LimitWindow> windows, int narrow, DateTimeOffset now)
    {
        int? pressing = null;
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Used < 90) continue;
            if (pressing is not int p || windows[i].Used > windows[p].Used
                || (windows[i].Used == windows[p].Used && windows[i].Resets > windows[p].Resets))
            {
                pressing = i;
            }
        }

        var candidates = Enumerable.Range(0, windows.Count).Where(i => narrow < 2 || windows[i].Used >= 70).ToList();
        var selected = candidates.Take(3).ToList();
        if (pressing is int pi && selected.Count == 3 && !selected.Contains(pi))
        {
            selected[2] = pi;
        }

        var shown = selected.OrderBy(i => i).Select(index =>
        {
            var window = windows[index];
            var urgent = index == pressing;
            var reset = ResetText(window, now, CultureInfo.CurrentCulture, TimeZoneInfo.Local);
            var rounded = (int)Math.Round(window.Used, MidpointRounding.AwayFromZero);
            var detail = window.Used >= 100 && urgent ? "→ " + reset
                : window.Used >= 90 && urgent ? $"{rounded}% · {reset}"
                : window.Used >= 70 ? $"{rounded}%"
                : null;
            var label = narrow >= 1 && window.Used < 70 ? null : window.Label;
            return new Badge(window, label, detail);
        }).ToList();

        var hidden = Enumerable.Range(0, windows.Count).Where(i => !selected.Contains(i)).Select(i => windows[i]).ToList();
        return (shown, hidden);
    }
}
