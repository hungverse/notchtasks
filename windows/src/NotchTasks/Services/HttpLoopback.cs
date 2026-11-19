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

    /// True for an absolute http URL whose host is 127.0.0.1 or localhost. The parsed address is given back.
    internal static bool IsLoopback(string url, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }
        if (parsed.Host is not ("127.0.0.1" or "localhost"))
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
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // a refused connection, a timeout or a bad reply: no answer
            return null;
        }
    }
}
