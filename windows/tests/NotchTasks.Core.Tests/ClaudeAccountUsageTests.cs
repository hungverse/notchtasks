using NotchTasks.Core.Limits;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The Claude account's plan usage: the token read from the credentials file, and the usage reply.
public class ClaudeAccountUsageTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);
    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private static string Creds(string token, long expiresAt) =>
        "{\"claudeAiOauth\":{\"accessToken\":\"" + token + "\",\"refreshToken\":\"r\",\"expiresAt\":" + expiresAt
        + ",\"scopes\":[\"user:inference\"],\"subscriptionType\":\"max\"}}";

    [Fact]
    public void Url_IsTheOAuthUsageEndpoint()
    {
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", ClaudeAccountUsage.Url);
    }

    [Fact]
    public void ReadToken_ValidAndNotExpired_GivesTheAccessToken()
    {
        var json = Creds("tok-valid", Ms(Now.AddHours(1)));

        Assert.Equal("tok-valid", ClaudeAccountUsage.ReadToken(json, Now));
    }

    [Fact]
    public void ReadToken_Expired_GivesNull()
    {
        // expiresAt equal to now counts as expired, and so does an earlier one
        Assert.Null(ClaudeAccountUsage.ReadToken(Creds("tok", Ms(Now)), Now));
        Assert.Null(ClaudeAccountUsage.ReadToken(Creds("tok", Ms(Now.AddMinutes(-1))), Now));
    }

    [Fact]
    public void ReadToken_MissingClaudeAiOauth_GivesNull()
    {
        Assert.Null(ClaudeAccountUsage.ReadToken("{\"other\":{\"accessToken\":\"tok\"}}", Now));
        Assert.Null(ClaudeAccountUsage.ReadToken("{}", Now));
    }

    [Fact]
    public void ReadToken_EmptyToken_GivesNull()
    {
        Assert.Null(ClaudeAccountUsage.ReadToken(Creds("", Ms(Now.AddHours(1))), Now));
    }

    [Fact]
    public void ReadToken_MissingExpiry_GivesNull()
    {
        var json = "{\"claudeAiOauth\":{\"accessToken\":\"tok\"}}";

        Assert.Null(ClaudeAccountUsage.ReadToken(json, Now));
    }

    [Fact]
    public void ReadToken_NotJson_GivesNull()
    {
        Assert.Null(ClaudeAccountUsage.ReadToken("not json {", Now));
        Assert.Null(ClaudeAccountUsage.ReadToken("", Now));
    }

    [Fact]
    public void Parse_BothWindows_GivesFiveHourAndSevenDay()
    {
        var json = "{\"five_hour\":{\"utilization\":42.5,\"resets_at\":\"2026-11-27T12:00:00.000000+00:00\"},"
            + "\"seven_day\":{\"utilization\":7,\"resets_at\":\"2026-12-02T08:00:00Z\"}}";

        var w = ClaudeAccountUsage.Parse(json, Now);

        Assert.Equal(2, w.Count);
        Assert.Equal("5h", w[0].Label);
        Assert.Equal(42.5, w[0].Used);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero), w[0].Resets);
        Assert.Equal("7d", w[1].Label);
        Assert.Equal(7, w[1].Used);
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 8, 0, 0, TimeSpan.Zero), w[1].Resets);
    }

    [Fact]
    public void Parse_OneWindowNull_GivesTheOther()
    {
        var w = ClaudeAccountUsage.Parse("{\"five_hour\":null,\"seven_day\":{\"utilization\":10,\"resets_at\":null}}", Now);

        var only = Assert.Single(w);
        Assert.Equal("7d", only.Label);
        Assert.Equal(10, only.Used);
    }

    [Fact]
    public void Parse_UtilizationNotANumber_SkipsThatWindow()
    {
        var w = ClaudeAccountUsage.Parse(
            "{\"five_hour\":{\"utilization\":\"42\",\"resets_at\":null},\"seven_day\":{\"utilization\":null,\"resets_at\":null}}",
            Now);

        Assert.Empty(w);
    }

    [Fact]
    public void Parse_ResetsAtNull_UsesFiveHoursOrSevenDaysFromNow()
    {
        var w = ClaudeAccountUsage.Parse("{\"five_hour\":{\"utilization\":1,\"resets_at\":null},"
            + "\"seven_day\":{\"utilization\":2}}", Now);

        Assert.Equal(Now.AddHours(5), w[0].Resets);
        Assert.Equal(Now.AddDays(7), w[1].Resets);
    }

    [Fact]
    public void Parse_UnparsableResetsAt_UsesTheDefault()
    {
        var w = ClaudeAccountUsage.Parse("{\"five_hour\":{\"utilization\":1,\"resets_at\":\"soon\"}}", Now);

        Assert.Equal(Now.AddHours(5), Assert.Single(w).Resets);
    }

    [Fact]
    public void Parse_ExtraKeysIgnored()
    {
        var w = ClaudeAccountUsage.Parse("{\"seven_day_opus\":{\"utilization\":99,\"resets_at\":null},"
            + "\"extra_usage\":{\"is_enabled\":true},\"five_hour\":{\"utilization\":3,\"resets_at\":null,\"x\":1}}", Now);

        var only = Assert.Single(w);
        Assert.Equal("5h", only.Label);
        Assert.Equal(3, only.Used);
    }

    [Fact]
    public void Parse_NotJson_GivesNone()
    {
        Assert.Empty(ClaudeAccountUsage.Parse("<html>nope</html>", Now));
        Assert.Empty(ClaudeAccountUsage.Parse("", Now));
    }
}
