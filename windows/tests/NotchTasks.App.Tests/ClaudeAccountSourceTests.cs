using System.IO;
using System.Net;
using System.Net.Http;
using NotchTasks.Core.Limits;
using NotchTasks.Services;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// The account's plan usage fetcher: the request it makes (URL and headers), what it does with each reply, when it asks again,
/// and that the token never reaches the error log. A fake handler stands in for the network; no request leaves the test.
public sealed class ClaudeAccountSourceTests
{
    private const string Token = "SECRET-TOKEN-7731-abc";

    private sealed record Sent(string Method, string Url, string? Authorization, string? Beta, string? UserAgent);

    /// Records each request it gets, and answers with the next reply (the last one repeats).
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _reply;

        public FakeHandler(Func<int, HttpResponseMessage> reply) => _reply = reply;

        public List<Sent> Sent { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(new Sent(
                request.Method.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization is { } a ? a.Scheme + " " + a.Parameter : null,
                request.Headers.TryGetValues("anthropic-beta", out var beta) ? string.Join(",", beta) : null,
                request.Headers.UserAgent.ToString()));
            return Task.FromResult(_reply(Sent.Count));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body) };

    private const string UsageBody = "{\"five_hour\":{\"utilization\":42,\"resets_at\":null}}";

    private static string Creds(long expiresAtMs) =>
        "{\"claudeAiOauth\":{\"accessToken\":\"" + Token + "\",\"refreshToken\":\"r\",\"expiresAt\":" + expiresAtMs + "}}";

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 10, 0, 0, TimeSpan.Zero);

    /// A temp Claude folder with a credentials file (when text is given), and the source over it.
    private static (ClaudeAccountSource Source, string File) Make(TempRoot root, string? text, FakeHandler handler)
    {
        var dir = Path.Combine(root.Folder, "claude");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, ".credentials.json");
        if (text is not null)
        {
            System.IO.File.WriteAllText(file, text);
        }
        return (new ClaudeAccountSource(file, handler), file);
    }

    /// Points the error log at a temp file for the test, and gives back its text after `act`.
    private static string LogOf(TempRoot root, Action act)
    {
        var log = Path.Combine(root.Folder, "logs", "windows-errors.log");
        var saved = ErrorLog.FileOverride;
        ErrorLog.FileOverride = log;
        try
        {
            act();
            return System.IO.File.Exists(log) ? System.IO.File.ReadAllText(log) : string.Empty;
        }
        finally
        {
            ErrorLog.FileOverride = saved;
        }
    }

    [Fact]
    public async Task Fetch_SendsTheExactUrlAndTheThreeHeaders()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, UsageBody));
        var (source, _) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        await source.FetchAsync(Now);

        var sent = Assert.Single(handler.Sent);
        Assert.Equal("GET", sent.Method);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", sent.Url);
        Assert.Equal(ClaudeAccountUsage.Url, sent.Url);
        Assert.Equal("Bearer " + Token, sent.Authorization);
        Assert.Equal("oauth-2025-04-20", sent.Beta);
        Assert.Equal("NotchTasks", sent.UserAgent);
    }

    [Fact]
    public async Task Fetch_200_GivesTheWindowsAsTheAccountReading()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, UsageBody));
        var (source, _) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        var limits = await source.FetchAsync(Now);

        Assert.NotNull(limits);
        Assert.Equal("claude", limits!.Kind);
        Assert.Equal(Now, limits.Updated);
        var w = Assert.Single(limits.Windows);
        Assert.Equal("5h", w.Label);
        Assert.Equal(42, w.Used);
    }

    [Fact]
    public async Task Fetch_ExpiredToken_NoRequest()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, UsageBody));
        var (source, _) = Make(root, Creds(Now.AddMinutes(-1).ToUnixTimeMilliseconds()), handler);

        Assert.Null(await source.FetchAsync(Now));
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Fetch_MissingCredentialsFile_NoRequest()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, UsageBody));
        var (source, _) = Make(root, null, handler);

        Assert.Null(await source.FetchAsync(Now));
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Fetch_401_StopsUntilTheFileChanges()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(n => n == 1 ? Json(HttpStatusCode.Unauthorized, "") : Json(HttpStatusCode.OK, UsageBody));
        var (source, file) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        Assert.True(source.Due(Now));
        Assert.Null(await source.FetchAsync(Now));

        // no ask while the file is the same, however long the wait
        Assert.False(source.Due(Now.AddMinutes(5)));
        Assert.False(source.Due(Now.AddDays(2)));
        Assert.Single(handler.Sent);

        // Claude Code wrote a new token: the file changed, so the app asks again
        System.IO.File.WriteAllText(file, Creds(Now.AddDays(3).ToUnixTimeMilliseconds()) + " ");
        Assert.True(source.Due(Now.AddDays(2)));
        Assert.NotNull(await source.FetchAsync(Now.AddDays(2)));
        Assert.Equal(2, handler.Sent.Count);
    }

    [Fact]
    public async Task Fetch_403_StopsLikeA401()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.Forbidden, ""));
        var (source, _) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        await source.FetchAsync(Now);

        Assert.False(source.Due(Now.AddHours(3)));
    }

    [Fact]
    public async Task Fetch_429_BacksOffTenThenTwentyMinutes()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json((HttpStatusCode)429, ""));
        var (source, _) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        await source.FetchAsync(Now);
        Assert.False(source.Due(Now.AddMinutes(9)));
        Assert.True(source.Due(Now.AddMinutes(10)));

        await source.FetchAsync(Now.AddMinutes(10));
        Assert.False(source.Due(Now.AddMinutes(10 + 19)));
        Assert.True(source.Due(Now.AddMinutes(10 + 20)));
    }

    [Fact]
    public async Task Fetch_ServerError_BacksOffToAnHour()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.InternalServerError, ""));
        var (source, _) = Make(root, Creds(Now.AddDays(1).ToUnixTimeMilliseconds()), handler);

        var at = Now;
        foreach (var wait in new[] { 10, 20, 40, 60, 60 })
        {
            await source.FetchAsync(at);
            Assert.False(source.Due(at.AddMinutes(wait - 1)));
            Assert.True(source.Due(at.AddMinutes(wait)));
            at = at.AddMinutes(wait);
        }
    }

    [Fact]
    public async Task Fetch_Success_AsksAgainAfterFiveMinutes()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, UsageBody));
        var (source, _) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        await source.FetchAsync(Now);

        Assert.False(source.Due(Now.AddMinutes(4)));
        Assert.True(source.Due(Now.AddMinutes(5)));
    }

    [Fact]
    public async Task Fetch_TokenNeverReachesTheErrorLog()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(n => n == 1
            ? throw new HttpRequestException("refused while sending " + Token)
            : Json(HttpStatusCode.InternalServerError, "echo " + Token));
        var (source, _) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        var log = LogOf(root, () =>
        {
            Assert.Null(source.FetchAsync(Now).Result);
            Assert.Null(source.FetchAsync(Now.AddMinutes(10)).Result);
        });

        Assert.Contains("Claude account usage", log);
        Assert.DoesNotContain(Token, log);
        Assert.DoesNotContain("refused", log);
    }

    [Fact]
    public async Task Fetch_UsageReplyWithNoWindows_GivesNull()
    {
        using var root = new TempRoot();
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{\"five_hour\":null}"));
        var (source, _) = Make(root, Creds(Now.AddHours(1).ToUnixTimeMilliseconds()), handler);

        Assert.Null(await source.FetchAsync(Now));
    }
}
