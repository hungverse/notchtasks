using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using NotchTasks.Core.Sync;

namespace NotchTasks.Services.Sync;

/// The sync's HTTP (ISyncHttp) on one HttpClient. Requests give up after 20 s; the answers event stream has no timeout.
/// A non-2xx answer to a request comes back as a result; only the event stream throws on one.
public sealed class SyncHttp : ISyncHttp, IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _client;

    /// The handler is the test's stub; the app passes none and gets a pooled socket handler that renews its connections every 5 minutes.
    public SyncHttp(HttpMessageHandler? handler = null)
    {
        _client = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            // Each call sets its own limit: a request gets RequestTimeout, the event stream none at all.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<SyncHttpResult> SendAsync(string method, string url, string? body, CancellationToken ct, string contentType = "application/json")
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RequestTimeout);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, limit.Token);
        var text = await response.Content.ReadAsStringAsync(limit.Token);
        return new SyncHttpResult((int)response.StatusCode, text);
    }

    public async Task<Stream> OpenEventStreamAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/event-stream");
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var status = response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"The answers stream answered {(int)status}.", null, status);
        }
        return await response.Content.ReadAsStreamAsync(ct);
    }

    public void Dispose() => _client.Dispose();
}
