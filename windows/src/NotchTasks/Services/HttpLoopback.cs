using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using NotchTasks.Core;

namespace NotchTasks.Services;

/// The HTTP of the agents and limits: one shared client, for the loopback addresses only (http://127.0.0.1 and
/// http://localhost). Any other URL gives null without a request. Redirects are not followed, and no proxy is used. Each call
/// has its own timeout; a failure, a non-success status or a timeout gives null (Core treats null as no answer).
public sealed class HttpLoopback : IHttpPost, IHttpGet
{
    /// The one instance the app uses.
    public static readonly HttpLoopback Shared = new();

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,  // each call sets its own timeout
    };

    public async Task<string?> PostAsync(string url, IReadOnlyDictionary<string, string> headers, string body, TimeSpan timeout)
    {
        if (!IsLoopback(url, out var uri))
        {
            return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        try
        {
            foreach (var (name, value) in headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }
        catch (FormatException)
        {
            return null;
        }
        return await SendAsync(request, timeout).ConfigureAwait(false);
    }

    public async Task<string?> GetAsync(string url, TimeSpan timeout)
    {
        if (!IsLoopback(url, out var uri))
        {
            return null;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await SendAsync(request, timeout).ConfigureAwait(false);
    }

    /// The most a reply body may be: a larger reply is no answer (1 MB).
    public const int MaxBodyBytes = 1024 * 1024;

    /// True for an absolute http URL whose host is 127.0.0.1 or localhost. A URL with user information ("user@host", or
    /// "127.0.0.1@evil.example") is refused, because the host it names is the one after the "@" and that is not the one a reader
    /// means. The parsed address is given back when it is true.
    public static bool IsLoopback(string url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }
        if (parsed.UserInfo.Length > 0 || parsed.Host is not ("127.0.0.1" or "localhost"))
        {
            return false;
        }
        uri = parsed;
        return true;
    }

    private static async Task<string?> SendAsync(HttpRequestMessage request, TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBodyBytes)
            {
                return null;
            }
            using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var body = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cts.Token).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxBodyBytes)
                {
                    return null;
                }
                body.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
        }
        catch (Exception)
        {
            // a refused connection, a timeout or a bad reply: no answer
            return null;
        }
    }
}
