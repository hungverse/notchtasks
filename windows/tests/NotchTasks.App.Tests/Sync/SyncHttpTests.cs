using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using System.Net.Http;
using System.Text;
using NotchTasks.Services.Sync;
using Xunit;

namespace NotchTasks.AppTests;

/// SyncHttp against a stub handler: no socket is opened.
public sealed class SyncHttpTests
{
    [Fact]
    public async Task SyncHttp_SendsMethodUrlBody_ReturnsStatusAndBody()
    {
        HttpRequestMessage? seen = null;
        string? seenBody = null;
        var handler = new StubHandler(async (request, ct) =>
        {
            seen = request;
            seenBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("made") };
        });
        using var http = new SyncHttp(handler);

        var result = await http.SendAsync("POST", "https://example.test/a.json?auth=t", "{\"a\":1}", CancellationToken.None);

        Assert.Equal(201, result.Status);
        Assert.Equal("made", result.Body);
        Assert.NotNull(seen);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("https://example.test/a.json?auth=t", seen.RequestUri!.ToString());
        Assert.Equal("{\"a\":1}", seenBody);
        Assert.Equal("application/json", seen.Content!.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task SyncHttp_SendAsync_Non2xxIsAResultNotAnException()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("denied"),
        }));
        using var http = new SyncHttp(handler);

        var result = await http.SendAsync("GET", "https://example.test/x.json", null, CancellationToken.None);

        Assert.Equal(401, result.Status);
        Assert.Equal("denied", result.Body);
    }

    [Fact]
    public async Task SyncHttp_SendAsync_FormBodyKeepsItsContentType()
    {
        string? mediaType = null;
        var handler = new StubHandler(async (request, ct) =>
        {
            mediaType = request.Content!.Headers.ContentType!.MediaType;
            _ = await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });
        using var http = new SyncHttp(handler);

        await http.SendAsync("POST", "https://example.test/token", "grant_type=refresh_token", CancellationToken.None,
            "application/x-www-form-urlencoded");

        Assert.Equal("application/x-www-form-urlencoded", mediaType);
    }

    [Fact]
    public async Task SyncHttp_EventStream_Non2xx_Throws()
    {
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var http = new SyncHttp(handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            http.OpenEventStreamAsync("https://example.test/answers.json?auth=t", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task SyncHttp_EventStream_AsksForEventStreamAndReadsTheBody()
    {
        string? accept = null;
        var handler = new StubHandler((request, _) =>
        {
            accept = string.Join(",", request.Headers.Accept.Select(a => a.MediaType));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("event: put\ndata: {}\n\n", Encoding.UTF8, "text/event-stream"),
            };
            return Task.FromResult(response);
        });
        using var http = new SyncHttp(handler);

        using var stream = await http.OpenEventStreamAsync("https://example.test/answers.json", CancellationToken.None);
        using var reader = new StreamReader(stream);

        Assert.Equal("text/event-stream", accept);
        Assert.StartsWith("event: put", await reader.ReadToEndAsync());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }
}
