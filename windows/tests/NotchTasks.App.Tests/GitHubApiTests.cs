using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The GitHub HTTPS seam, with a fake handler: no request leaves the test. Only api.github.com is asked, a token goes as a bearer
/// header, and a failed, oversized or unparsable answer is null.
public sealed class GitHubApiTests
{
    /// Answers each request with what the function gives, and records the requests.
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> answer;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => this.answer = answer;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Get_Ok_ReturnsTheParsedJsonWithTheGitHubHeaders()
    {
        var handler = new FakeHandler(_ => Json("{\"workflow_runs\":[]}"));
        var api = new GitHubApi(Path.GetTempPath(), handler);

        using var document = await api.Get("repos/o/r/actions/runs?per_page=10", "tok");

        Assert.NotNull(document);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/o/r/actions/runs?per_page=10", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("tok", request.Headers.Authorization.Parameter);
        Assert.Equal("2022-11-28", request.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.Select(a => a.MediaType));
    }

    [Fact]
    public async Task Get_NoToken_SendsNoAuthorization()
    {
        var handler = new FakeHandler(_ => Json("{}"));
        var api = new GitHubApi(Path.GetTempPath(), handler);

        using var document = await api.Get("repos/o/r/actions/runs", null);

        Assert.NotNull(document);
        Assert.Null(Assert.Single(handler.Requests).Headers.Authorization);
    }

    [Fact]
    public async Task Get_NotOk_IsNull()
    {
        var api = new GitHubApi(Path.GetTempPath(), new FakeHandler(_ => Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound)));

        Assert.Null(await api.Get("repos/o/missing/actions/runs", null));
    }

    [Fact]
    public async Task Get_BadJson_IsNull()
    {
        var api = new GitHubApi(Path.GetTempPath(), new FakeHandler(_ => Json("{not json")));

        Assert.Null(await api.Get("repos/o/r/actions/runs", null));
    }

    [Fact]
    public async Task Get_OverTwoMegabytes_IsNull()
    {
        var big = new string('a', 2_000_001);
        var api = new GitHubApi(Path.GetTempPath(), new FakeHandler(_ => Json("\"" + big + "\"")));

        Assert.Null(await api.Get("repos/o/r/actions/runs", null));
    }

    [Fact]
    public async Task Get_OtherHost_IsNullAndNothingIsSent()
    {
        var handler = new FakeHandler(_ => Json("{}"));
        var api = new GitHubApi(Path.GetTempPath(), handler);

        Assert.Null(await api.Get("https://example.com/repos/o/r", "tok"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Get_PortInTheHost_IsNullAndNothingIsSent()
    {
        var handler = new FakeHandler(_ => Json("{}"));
        var api = new GitHubApi(Path.GetTempPath(), handler);

        Assert.Null(await api.Get("https://api.github.com:8443/repos/o/r", "tok"));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Get_Redirect_IsNullAndNotFollowed()
    {
        var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
        redirect.Headers.Location = new Uri("https://example.com/elsewhere");
        var handler = new FakeHandler(_ => redirect);
        var api = new GitHubApi(Path.GetTempPath(), handler);

        Assert.Null(await api.Get("repos/o/r/actions/runs", null));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Get_StreamedBodyOverTwoMegabytesWithoutLength_IsNull()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EndlessStream(2_000_001)),   // no Content-Length: the cap is counted as it streams
        };
        var api = new GitHubApi(Path.GetTempPath(), new FakeHandler(_ => response));

        Assert.Null(await api.Get("repos/o/r/actions/runs", null));
    }

    [Fact]
    public async Task Get_BodyThatStalls_IsNullWithinTheLimit()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StalledStream()) };
        var api = new GitHubApi(Path.GetTempPath(), new FakeHandler(_ => response), TimeSpan.FromMilliseconds(300));

        var started = DateTime.UtcNow;
        var result = await api.Get("repos/o/r/actions/runs", null);

        Assert.Null(result);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    /// A body of length bytes with no end marker (the length is known only when it has been read).
    private sealed class EndlessStream : Stream
    {
        private readonly long length;
        private long sent;

        public EndlessStream(long length) => this.length = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - sent);
            Array.Fill(buffer, (byte)'a', offset, n);
            sent += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// A body that never sends a byte: a read waits until it is cancelled.
    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
