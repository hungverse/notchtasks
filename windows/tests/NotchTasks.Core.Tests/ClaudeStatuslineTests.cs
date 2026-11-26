using System.Text;
using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

// The statusline half of `NotchTasksHook.exe --statusline`: the printed summary and the saved snapshot. Temp folders only.
public sealed class ClaudeStatuslineTests : IDisposable
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
        var d = Path.Combine(Path.GetTempPath(), "nt-statusline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _made.Add(d);
        return d;
    }

    private const string BothWindows =
        "{\"model\":{\"id\":\"x\"},\"rate_limits\":{"
        + "\"five_hour\":{\"used_percentage\":12,\"resets_at\":4102444800},"
        + "\"seven_day\":{\"used_percentage\":3,\"resets_at\":4102444800}}}";

    [Fact]
    public void Summary_BothWindows_PrintsFiveHourThenSevenDay()
    {
        Assert.Equal("5h 12% · 7d 3%", ClaudeStatusline.Summary(BothWindows));
    }

    [Fact]
    public void Summary_OnlyFiveHour_PrintsOnlyThatWindow()
    {
        const string json = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":40,\"resets_at\":4102444800}}}";

        Assert.Equal("5h 40%", ClaudeStatusline.Summary(json));
    }

    [Fact]
    public void Summary_OnlySevenDay_PrintsOnlyThatWindow()
    {
        const string json = "{\"rate_limits\":{\"seven_day\":{\"used_percentage\":7,\"resets_at\":4102444800}}}";

        Assert.Equal("7d 7%", ClaudeStatusline.Summary(json));
    }

    [Fact]
    public void Summary_NoRateLimits_PrintsNothing()
    {
        Assert.Equal("", ClaudeStatusline.Summary("{\"model\":{\"id\":\"x\"}}"));
        Assert.Equal("", ClaudeStatusline.Summary("{\"rate_limits\":{}}"));
    }

    [Fact]
    public void Summary_NonJson_PrintsNothing()
    {
        Assert.Equal("", ClaudeStatusline.Summary("not json at all"));
        Assert.Equal("", ClaudeStatusline.Summary(""));
    }

    [Fact]
    public void Summary_FractionalPercentages_TakeTheIntegerPart()
    {
        const string json = "{\"rate_limits\":{"
            + "\"five_hour\":{\"used_percentage\":12.9,\"resets_at\":4102444800},"
            + "\"seven_day\":{\"used_percentage\":0.5,\"resets_at\":4102444800}}}";

        Assert.Equal("5h 12% · 7d 0%", ClaudeStatusline.Summary(json));
    }

    [Fact]
    public void Summary_WindowWithoutNumericPercentage_IsLeftOut()
    {
        const string json = "{\"rate_limits\":{"
            + "\"five_hour\":{\"used_percentage\":\"lots\",\"resets_at\":4102444800},"
            + "\"seven_day\":{\"used_percentage\":3,\"resets_at\":4102444800}}}";

        Assert.Equal("7d 3%", ClaudeStatusline.Summary(json));
    }

    [Fact]
    public void Save_WritesTheJsonAsItIs_UnderTheStatusFile()
    {
        var dir = Path.Combine(Tmp(), "nested", "claude");

        ClaudeStatusline.Save(dir, BothWindows);

        Assert.Equal(BothWindows, File.ReadAllText(Path.Combine(dir, "notchtasks-status.json"), Encoding.UTF8));
    }

    [Fact]
    public void Save_ReplacesTheOldSnapshot_AndLeavesNoTempFile()
    {
        var dir = Tmp();
        File.WriteAllText(Path.Combine(dir, "notchtasks-status.json"), "{\"old\":true}");

        ClaudeStatusline.Save(dir, BothWindows);

        Assert.Equal(BothWindows, File.ReadAllText(Path.Combine(dir, "notchtasks-status.json")));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp*"));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void Save_ThenFromStatusline_ReadsTheSameLimits()
    {
        var dir = Tmp();
        var resets = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var json = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":42,\"resets_at\":" + resets + "}}}";

        ClaudeStatusline.Save(dir, json);
        var limits = ClaudeUsage.FromStatusline(dir, DateTimeOffset.UtcNow);

        Assert.NotNull(limits);
        Assert.Equal("5h", limits!.Windows.Single().Label);
        Assert.Equal(42, limits.Windows.Single().Used);
    }
}
