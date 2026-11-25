using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Sync;

/// A Firebase call that answered with a non-2xx status. The message names the status only.
internal sealed class FirebaseException(int status) : Exception($"Firebase {status}")
{
    public int Status { get; } = status;
}

/// The Firebase side of sync.js: the anonymous sign-up, the ID token (renewed every 50 minutes), the Realtime Database paths
/// under the group, and the answers stream. Holds the token in memory only. Port of signUp, idToken, rest and post.
internal sealed class FirebaseRest
{
    private const double TokenMs = 3000 * 1000; // the ID token is renewed after 50 minutes (Firebase's lasts an hour)

    private readonly ISyncHttp _http;
    private readonly string _apiKey;
    private readonly string _db;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private string? _idToken;
    private double _tokenUntilMs;

    public FirebaseRest(ISyncHttp http, string apiKey, string db, Func<DateTimeOffset> now)
    {
        _http = http;
        _apiKey = apiKey;
        _db = db;
        _now = now;
    }

    /// Creates an anonymous account and keeps its ID token. Returns its uid and refresh token.
    public async Task<(string Uid, string Refresh)> SignUpAsync(CancellationToken ct)
    {
        var text = await PostAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={_apiKey}",
            SyncJson.Write(new JsonObject { ["returnSecureToken"] = true }),
            "application/json",
            ct);
        var r = JsonNode.Parse(text) as JsonObject;
        var idToken = Text(r?["idToken"]);
        var uid = Text(r?["localId"]);
        var refresh = Text(r?["refreshToken"]);
        if (string.IsNullOrEmpty(idToken) || string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(refresh))
        {
            throw new InvalidOperationException("no anonymous account");
        }
        SetToken(idToken);
        return (uid, refresh);
    }

    /// A valid ID token for the account with this refresh token: the kept one while it is fresh, else a new one.
    public async Task<string> IdTokenAsync(string refresh, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_idToken is not null && _tokenUntilMs > NowMs())
            {
                return _idToken;
            }
        }
        var form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refresh)}";
        var text = await PostAsync(
            $"https://securetoken.googleapis.com/v1/token?key={_apiKey}", form, "application/x-www-form-urlencoded", ct);
        var idToken = Text((JsonNode.Parse(text) as JsonObject)?["id_token"]);
        if (string.IsNullOrEmpty(idToken))
        {
            throw new InvalidOperationException("no ID token");
        }
        SetToken(idToken);
        return idToken;
    }

    /// A Realtime Database call under the group's path (`{db}/{path}.json`). Returns the response text; a non-2xx answer throws
    /// FirebaseException, and a 401 also forgets the token so the next call renews it.
    public async Task<string> RestAsync(string method, string path, string refresh, JsonNode? body, CancellationToken ct)
    {
        var token = await IdTokenAsync(refresh, ct);
        var res = await _http.SendAsync(
            method,
            $"{_db}/{path}.json?auth={token}",
            body is null ? null : SyncJson.Write(body),
            ct);
        if (res.Status is < 200 or > 299)
        {
            if (res.Status == 401)
            {
                ClearToken();
            }
            throw new FirebaseException(res.Status);
        }
        return res.Body;
    }

    /// Opens the answers stream of the group: `{db}/groups/{g}/answers.json`, with a fresh ID token. A 401 or 403 from the stream
    /// forgets the token like a REST 401, so the reopen renews it.
    public async Task<Stream> OpenAnswersAsync(string refresh, string groupId, CancellationToken ct)
    {
        var token = await IdTokenAsync(refresh, ct);
        try
        {
            return await _http.OpenEventStreamAsync($"{_db}/groups/{groupId}/answers.json?auth={token}", ct);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            ClearToken();
            throw;
        }
    }

    /// Forgets the ID token (after a 401, or when the stream says the session was revoked).
    public void ClearToken()
    {
        lock (_gate)
        {
            _idToken = null;
        }
    }

    private async Task<string> PostAsync(string url, string body, string contentType, CancellationToken ct)
    {
        var res = await _http.SendAsync("POST", url, body, ct, contentType);
        if (res.Status is < 200 or > 299)
        {
            throw new FirebaseException(res.Status);
        }
        return res.Body;
    }

    private void SetToken(string idToken)
    {
        lock (_gate)
        {
            _idToken = idToken;
            _tokenUntilMs = NowMs() + TokenMs;
        }
    }

    private double NowMs() => _now().ToUnixTimeMilliseconds();

    private static string? Text(JsonNode? n) => n is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
        ? v.GetValue<string>()
        : null;
}
