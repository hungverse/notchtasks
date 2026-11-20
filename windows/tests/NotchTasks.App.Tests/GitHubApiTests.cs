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
}
