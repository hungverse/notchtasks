using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;

namespace NotchTasks.Services.Modules;

/// The signed-in Claude account's plan usage (ClaudeAccountUsage). The sign-in token is read from the credentials file Claude keeps
/// in its config folder. It is never refreshed and the file is never written: a refresh would rotate Claude's own tokens.
/// - No request when the file is missing, has no usable token, or the token is expired (the file is looked at again in 5 min).
/// - Success: the next ask is 5 min later.
/// - 429 and 5xx (and any other failure): back off 10, 20, 40, then 60 min.
/// - 401 and 403: no ask until the credentials file changes (its write time or size).
/// Only the HTTP status or the exception's type reaches the error log, never the token or a message that could carry it.
public sealed class ClaudeAccountSource
{
    /// The one handler the app uses: no redirects (the token must not follow one), the system proxy as usual.
    public static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler { AllowAutoRedirect = false };

    /// The file name of the credentials in the Claude config folder.
    public const string CredentialsName = ".credentials.json";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BackoffFirst = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BackoffMax = TimeSpan.FromMinutes(60);

    private readonly string _file;
    private readonly HttpClient _client;
    private DateTimeOffset _nextAt = DateTimeOffset.MinValue;
    private int _failures;
    private (DateTime Written, long Size)? _stoppedAt;

    /// file is the credentials file; handler answers the usage endpoint (a test passes a fake).
    public ClaudeAccountSource(string file, HttpMessageHandler handler)
    {
        _file = file;
        _client = new HttpClient(handler, disposeHandler: false) { Timeout = RequestTimeout };
    }

    /// True when an ask may be made now. After a 401 or 403 it stays false until the credentials file changes.
    public bool Due(DateTimeOffset now)
    {
        if (_stoppedAt is { } stopped)
        {
            if (Stamp() == stopped)
            {
                return false;
            }
            _stoppedAt = null;
            _nextAt = DateTimeOffset.MinValue;
        }
        return now >= _nextAt;
    }

    /// Asks the usage endpoint once, with the token of the credentials file. The reading is null when there is no answer, or
    /// the answer has no windows. Never throws.
    public async Task<ProviderLimits?> FetchAsync(DateTimeOffset now)
    {
        var stamp = Stamp();
        string json;
        try
        {
            json = Providers.ReadAllTextShared(_file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _nextAt = now + Every;
            return null;
        }

        var token = ClaudeAccountUsage.ReadToken(json, now);
        if (token is null)
        {
            _nextAt = now + Every;
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ClaudeAccountUsage.Url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.UserAgent.ParseAdd("NotchTasks");
            using var response = await _client.SendAsync(request).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _failures = 0;
                _nextAt = now + Every;
                var windows = ClaudeAccountUsage.Parse(body, now);
                return windows.Count == 0
                    ? null
                    : new ProviderLimits("claude", "Claude", ClaudeUsage.BundleId, windows, now);
            }
            if (status is 401 or 403)
            {
                _stoppedAt = stamp;
                Log("HTTP " + status);
                return null;
            }
            Fail(now, "HTTP " + status);
            return null;
        }
        catch (Exception e)
        {
            Fail(now, e.GetType().Name);
            return null;
        }
    }

    private void Fail(DateTimeOffset now, string what)
    {
        _failures++;
        var wait = TimeSpan.FromMinutes(Math.Min(BackoffMax.TotalMinutes, BackoffFirst.TotalMinutes * (1 << Math.Min(_failures - 1, 3))));
        _nextAt = now + wait;
        Log(what);
    }

    private static void Log(string what) =>
        ErrorLog.Write(new InvalidOperationException("Claude account usage: " + what));

    /// The credentials file's write time and size; a missing file has its own stamp.
    private (DateTime Written, long Size) Stamp()
    {
        try
        {
            var info = new FileInfo(_file);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (DateTime.MinValue, -1L);
        }
    }
}
