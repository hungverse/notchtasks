using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

public class ClaudeUsageTests : IDisposable
{
    private readonly List<string> _made = new();

    public void Dispose()
    {
        foreach (var d in _made)
        {
            try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }

    private string Tmp()
    {
        var d = Path.Combine(Path.GetTempPath(), "nt-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _made.Add(d);
        return d;
    }

    [Fact]
    public void ClaudeUsageText_FiveHourAndWeekWindows_ResetsInThePrintedZone()
    {
        var usage = "You are currently using your subscription\n\n"
            + "Current session: 34% used · resets Nov 3 at 8:09pm (Asia/Saigon)\n"
            + "Current week (all models): 34% used · resets Nov 5 at 1:59pm (Asia/Saigon)\n"
            + "Current week (Sonnet only): 3% used · resets Nov 5 at 2pm (Asia/Saigon)\n";

        var w = ClaudeUsage.Parse(usage, new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, w.Count);
        Assert.Equal("5h", w[0].Label);
        Assert.Equal(34, w[0].Used);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 13, 9, 0, TimeSpan.Zero), w[0].Resets);
        Assert.Equal("7d", w[1].Label);
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 6, 59, 0, TimeSpan.Zero), w[1].Resets);
    }

    [Fact]
    public void ClaudeUsage_ResetWithoutMinutes_IsOnTheHour_ResetPastNewYear_IsNextYear()
    {
        var hourOnly = ClaudeUsage.Parse("Current week (all models): 34% used · resets Nov 5 at 2pm (Asia/Saigon)",
            new DateTimeOffset(2026, 11, 3, 10, 58, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 5, 7, 0, 0, TimeSpan.Zero), hourOnly[0].Resets);

        var newYear = ClaudeUsage.Parse("Current session: 10% used · resets Jan 2 at 9am (UTC)",
            new DateTimeOffset(2026, 12, 31, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2027, 1, 2, 9, 0, 0, TimeSpan.Zero), newYear[0].Resets);
    }

    [Fact]
    public void ClaudeUsage_NoPercentage_OrOtherLine_GivesNothing()
    {
        Assert.Empty(ClaudeUsage.Parse("Current session: resets soon", DateTimeOffset.UtcNow));
        Assert.Empty(ClaudeUsage.Parse("Something else: 5% used", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ParseResetWhen_IanaZoneNames_WorkOnWindows()
    {
        // Los Angeles is on PST (UTC-8) on Nov 3 2026: 8:09pm there is 04:09 UTC on Nov 4
        var la = ClaudeUsage.ParseResetWhen("Nov 3 at 8:09pm", "America/Los_Angeles", new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 4, 9, 0, TimeSpan.Zero), la);

        Assert.Null(ClaudeUsage.ValidZone("Not/AZone"));
        Assert.Equal("America/Los_Angeles", ClaudeUsage.ValidZone("America/Los_Angeles"));
        Assert.Null(ClaudeUsage.ParseResetWhen("Smarch 3", "UTC", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ZonedToDate_ConvertsWallClockInTheZone()
    {
        var at = ClaudeUsage.ZonedToDate(2026, 11, 3, 20, 9, "America/Los_Angeles");
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 4, 9, 0, TimeSpan.Zero), at);
        Assert.Equal(TimeSpan.FromHours(-8).TotalMilliseconds,
            ClaudeUsage.TzOffsetMs(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero), "America/Los_Angeles"));
    }

    [Fact]
    public void ClaudeLimits_ReadsTheStatuslineSnapshot_PastResetsAreDropped()
    {
        var dir = Tmp();
        var now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var future = now.ToUnixTimeSeconds() + 3600;
        var past = now.ToUnixTimeSeconds() - 100;
        File.WriteAllText(Path.Combine(dir, "notchtasks-status.json"),
            "{\"rate_limits\":{"
            + "\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + future + "},"
            + "\"seven_day\":{\"used_percentage\":9,\"resets_at\":" + past + "}}}");

        var p = ClaudeUsage.FromStatusline(dir, now);

        Assert.NotNull(p);
        Assert.Equal("Claude", p!.Name);
        Assert.Equal("claude", p.Kind);
        Assert.Equal(new[] { ("5h", 42.0) }, p.Windows.Select(w => (w.Label, w.Used)));
        Assert.Null(ClaudeUsage.FromStatusline(Tmp(), now));
    }

    [Fact]
    public void ClaudeLimits_StatuslineHeldOpenByItsWriter_IsRead()
    {
        var dir = Tmp();
        var now = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var future = now.ToUnixTimeSeconds() + 3600;
        // Claude keeps its status file open for writing while it works
        using var writer = new FileStream(Path.Combine(dir, "notchtasks-status.json"), FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(System.Text.Encoding.UTF8.GetBytes(
            "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + future + "}}}"));
        writer.Flush();

        var p = ClaudeUsage.FromStatusline(dir, now);

        Assert.NotNull(p);
        Assert.Equal(new[] { ("5h", 42.0) }, p!.Windows.Select(w => (w.Label, w.Used)));
    }

    [Fact]
    public void Now_LiveReadingYoungerThan15Min_WinsAndPastResetsCountZero()
    {
        var at = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var live = new[] { new LimitWindow("5h", 90, at.AddHours(2)), new LimitWindow("7d", 50, at.AddMinutes(5)) };
        var status = new ProviderLimits("claude", "Claude", null, new[] { new LimitWindow("5h", 1, at.AddHours(1)) }, at);

        // 10 min old: the window that reset 5 min after the reading counts as unused
        var fresh = ClaudeUsage.Now(live, at, status, at.AddMinutes(10));
        Assert.NotNull(fresh);
        Assert.Equal(at, fresh!.Updated);
        Assert.Equal(new[] { 90.0, 0.0 }, fresh.Windows.Select(w => w.Used));

        // 16 min old: the statusline answers instead
        Assert.Same(status, ClaudeUsage.Now(live, at, status, at.AddMinutes(16)));
        Assert.Same(status, ClaudeUsage.Now(null, null, status, at));
    }
}
