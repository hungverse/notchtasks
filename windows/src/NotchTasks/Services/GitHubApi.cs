using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using NotchTasks.Core.Ci;

namespace NotchTasks.Services;

/// The GitHub REST seam over HTTPS (Phase 3c R3). Get asks api.github.com only (the host is fixed: a route that names another
/// host is never sent), with a bearer token when one is given. Anything but a 200, an answer over 2 MB or bad JSON is null, and a
/// network error or timeout is null too. Token is gh's token from `gh auth token` (5 s), or null when gh is missing or not logged
/// in. The token lives only in memory. The one shared client does not follow redirects and has a 10 s timeout.
public sealed class GitHubApi : IGitHubApi
{
    private const int MaxBytes = 2_000_000;
    private static readonly Uri ApiBase = new("https://api.github.com/");
    private static readonly TimeSpan RequestLimit = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TokenLimit = TimeSpan.FromSeconds(5);
    private static readonly HttpClient Shared = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = RequestLimit,
    };

    private readonly HttpClient _http;
    private readonly string _home;
    private readonly TimeSpan _limit;

    /// home is the user's profile folder (where gh is looked for). handler replaces the network, for tests; null uses the shared
    /// client. limit is the whole-request limit (the request and its body), 10 s when null.
    public GitHubApi(string home, HttpMessageHandler? handler = null, TimeSpan? limit = null)
    {
        _home = home;
        _limit = limit ?? RequestLimit;
        _http = handler is null ? Shared : new HttpClient(handler) { Timeout = _limit };
    }

    public async Task<JsonDocument?> Get(string route, string? token)
    {
        // the authority comparison refuses a port too: only https://api.github.com is ever asked
        if (!Uri.TryCreate(ApiBase, route, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Authority != ApiBase.Authority)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.TryAddWithoutValidation("User-Agent", "NotchTasks");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // one limit for the whole request: the answer's headers and its body
        using var limit = new CancellationTokenSource(_limit);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
            var body = await ReadCapped(stream, limit.Token);
            return body is null ? null : JsonDocument.Parse(body);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// gh's token: `gh auth token` with constant arguments, run for at most 5 s. Null when gh is not found, fails or prints nothing.
    public Task<string?> Token() => Task.Run<string?>(() =>
    {
        var gh = CliLauncher.ResolveDirect("gh", _home);
        if (gh is null)
        {
            return null;
        }
        var output = CliLauncher.Run(gh, new[] { "auth", "token" }, TokenLimit, CancellationToken.None, requireZeroExit: true);
        var token = output?.Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    });

    /// The body of a response, or null when it is over MaxBytes.
    private static async Task<byte[]?> ReadCapped(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(), ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
