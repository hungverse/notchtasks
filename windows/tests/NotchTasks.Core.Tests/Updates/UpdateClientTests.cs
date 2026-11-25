using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core.Updates;
using Xunit;

namespace NotchTasks.Core.Tests.Updates;

/// Port of electron/test/updater.test.js (the feed, the installer download and its checksum) over a fake HTTP handler.
public sealed class UpdateClientTests : IDisposable
{
    private const string Feed = "https://feed.test/version-win.json";
    private const string Installer = "https://dl.test/setup.exe";
    private static readonly Version Windows11 = new(10, 0, 22631);

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "nt-upd-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }
    }

    [Fact]
    public async Task CheckAsync_NotFound_IsNoUpdate()
    {
        var client = new UpdateClient(new FakeHandler(_ => Status(HttpStatusCode.NotFound)));

        Assert.Null(await client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_NewerBuild_IsOffered()
    {
        var client = new UpdateClient(new FakeHandler(_ => Bytes(Manifest(build: 5))));

        var m = await client.CheckAsync(Feed, 4, Windows11, CancellationToken.None);

        Assert.NotNull(m);
        Assert.Equal(5, m!.Build);
        Assert.Equal("2.3.0", m.Version);
    }

    [Fact]
    public async Task CheckAsync_SameOrOlderBuild_IsNoUpdate()
    {
        var client = new UpdateClient(new FakeHandler(_ => Bytes(Manifest(build: 4))));

        Assert.Null(await client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));
        Assert.Null(await client.CheckAsync(Feed, 5, Windows11, CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_MinOSAboveThisWindows_IsNoUpdate()
    {
        var client = new UpdateClient(new FakeHandler(_ => Bytes(Manifest(build: 5, minOS: "11.0.0"))));

        Assert.Null(await client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_BodyThatIsNotJson_IsNoUpdate()
    {
        var client = new UpdateClient(new FakeHandler(_ => Bytes(Encoding.UTF8.GetBytes("{nope"))));

        Assert.Null(await client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_BodyOver64KiB_IsNoUpdate()
    {
        // A valid entry that is only too long: without the cap it would be offered.
        var client = new UpdateClient(new FakeHandler(_ => Bytes(Manifest(build: 5, notes: new string('x', 70_000)))));

        Assert.Null(await client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_ServerError_IsADownloadFailure()
    {
        var client = new UpdateClient(new FakeHandler(_ => Status(HttpStatusCode.InternalServerError)));

        var ex = await Assert.ThrowsAsync<UpdateFailure>(() => client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));

        Assert.Equal(UpdateFailureKind.Download, ex.Kind);
    }

    [Fact]
    public async Task CheckAsync_NetworkError_IsADownloadFailure()
    {
        var client = new UpdateClient(new FakeHandler(_ => throw new HttpRequestException("unreachable")));

        var ex = await Assert.ThrowsAsync<UpdateFailure>(() => client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));

        Assert.Equal(UpdateFailureKind.Download, ex.Kind);
    }

    [Fact]
    public async Task CheckAsync_RedirectToPlainHttp_IsADownloadFailure()
    {
        var client = new UpdateClient(new FakeHandler(_ => Redirect("http://example.com/version-win.json")));

        var ex = await Assert.ThrowsAsync<UpdateFailure>(() => client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));

        Assert.Equal(UpdateFailureKind.Download, ex.Kind);
    }

    [Fact]
    public async Task CheckAsync_RedirectLoop_GivesUpAfterFiveRedirects()
    {
        var handler = new FakeHandler(_ => Redirect(Feed));
        var client = new UpdateClient(handler);

        var ex = await Assert.ThrowsAsync<UpdateFailure>(() => client.CheckAsync(Feed, 4, Windows11, CancellationToken.None));

        Assert.Equal(UpdateFailureKind.Download, ex.Kind);
        Assert.Equal(6, handler.Urls.Count); // the first request and five redirects
    }

    [Fact]
    public async Task CheckAsync_FollowsARedirectToAnAllowedFeed()
    {
        var client = new UpdateClient(new FakeHandler(r => r.RequestUri!.ToString() == Feed
            ? Bytes(Manifest(build: 5))
            : Redirect(Feed)));

        var m = await client.CheckAsync("https://feed.test/old.json", 4, Windows11, CancellationToken.None);

        Assert.Equal(5, m?.Build);
    }

    [Fact]
    public async Task CheckAsync_SendsCacheControlNoCache()
    {
        var handler = new FakeHandler(_ => Status(HttpStatusCode.NotFound));
        await new UpdateClient(handler).CheckAsync(Feed, 4, Windows11, CancellationToken.None);

        Assert.Single(handler.NoCache);
        Assert.True(handler.NoCache[0]);
    }

    [Fact]
    public async Task CheckAsync_FeedNotAllowed_FallsBackToTheDefaultFeed()
    {
        var handler = new FakeHandler(_ => Status(HttpStatusCode.NotFound));
        await new UpdateClient(handler).CheckAsync("http://example.com/version-win.json", 4, Windows11, CancellationToken.None);

        Assert.Equal(UpdateClient.DefaultFeed, handler.Urls[0]);
    }

    [Fact]
    public async Task DownloadAsync_WritesTheInstallerAndReportsProgress()
    {
        var installer = Encoding.UTF8.GetBytes("signed bytes");
        var client = new UpdateClient(new FakeHandler(_ => Bytes(installer)));
        var progress = new List<double>();

        var path = await client.DownloadAsync(ManifestFor(installer, build: 9), _tmp, new Recorder(progress), CancellationToken.None);

        Assert.Equal(Path.Combine(_tmp, "NotchTasks-update-9.exe"), path);
        Assert.Equal(installer, File.ReadAllBytes(path));
        Assert.Equal(1.0, progress[^1]);
    }

    [Fact]
    public async Task DownloadAsync_WrongChecksum_IsAHashFailureAndNoFile()
    {
        var client = new UpdateClient(new FakeHandler(_ => Bytes(Encoding.UTF8.GetBytes("tampered"))));
        var manifest = new UpdateManifest("2.3.0", 9, Installer, new string('c', 64), "10.0", new Dictionary<string, string>());

        var ex = await Assert.ThrowsAsync<UpdateFailure>(() => client.DownloadAsync(manifest, _tmp, null, CancellationToken.None));

        Assert.Equal(UpdateFailureKind.Hash, ex.Kind);
        Assert.False(File.Exists(Path.Combine(_tmp, "NotchTasks-update-9.exe")));
    }

    [Fact]
    public async Task DownloadAsync_RedirectToPlainHttp_IsADownloadFailureAndNoFile()
    {
        var client = new UpdateClient(new FakeHandler(_ => Redirect("http://example.com/setup.exe")));

        var ex = await Assert.ThrowsAsync<UpdateFailure>(() =>
            client.DownloadAsync(ManifestFor(Encoding.UTF8.GetBytes("x"), build: 9), _tmp, null, CancellationToken.None));

        Assert.Equal(UpdateFailureKind.Download, ex.Kind);
        Assert.False(File.Exists(Path.Combine(_tmp, "NotchTasks-update-9.exe")));
    }

    [Fact]
    public async Task DownloadAsync_FollowsARedirectToAnAllowedHost()
    {
        var installer = Encoding.UTF8.GetBytes("cdn bytes");
        var client = new UpdateClient(new FakeHandler(r => r.RequestUri!.Host == "dl.test"
            ? Redirect("https://cdn.test/setup.exe")
            : Bytes(installer)));

        var path = await client.DownloadAsync(ManifestFor(installer, build: 9), _tmp, null, CancellationToken.None);

        Assert.Equal(installer, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task DownloadAsync_ServerError_IsADownloadFailure()
    {
        var client = new UpdateClient(new FakeHandler(_ => Status(HttpStatusCode.ServiceUnavailable)));

        var ex = await Assert.ThrowsAsync<UpdateFailure>(() =>
            client.DownloadAsync(ManifestFor(Encoding.UTF8.GetBytes("x"), build: 9), _tmp, null, CancellationToken.None));

        Assert.Equal(UpdateFailureKind.Download, ex.Kind);
    }

    [Fact]
    public void RemoveLeftovers_DeletesOnlyTheInstallers()
    {
        Directory.CreateDirectory(_tmp);
        var installer = Path.Combine(_tmp, "NotchTasks-update-3.exe");
        var other = Path.Combine(_tmp, "unrelated.txt");
        File.WriteAllText(installer, "old");
        File.WriteAllText(other, "keep");

        UpdateClient.RemoveLeftovers(_tmp);

        Assert.False(File.Exists(installer));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void RemoveLeftovers_MissingFolder_DoesNothing()
    {
        UpdateClient.RemoveLeftovers(_tmp);

        Assert.False(Directory.Exists(_tmp));
    }

    private static UpdateManifest ManifestFor(byte[] installer, int build) =>
        new("2.3.0", build, Installer, Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant(), "10.0",
            new Dictionary<string, string>());

    private static byte[] Manifest(int build, string minOS = "10.0.19045", string notes = "English")
    {
        var o = new JsonObject
        {
            ["version"] = "2.3.0",
            ["build"] = build,
            ["url"] = Installer,
            ["sha256"] = new string('a', 64),
            ["minOS"] = minOS,
            ["notes"] = new JsonObject { ["en"] = notes },
        };
        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    private static HttpResponseMessage Bytes(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static HttpResponseMessage Redirect(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Redirect);
        response.Headers.Location = new Uri(to);
        return response;
    }

    // Records each request's URL and whether it asked for no-cache; answers with respond.
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public List<bool> NoCache { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            NoCache.Add(request.Headers.CacheControl?.NoCache == true);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Recorder(List<double> values) : IProgress<double>
    {
        public void Report(double value) => values.Add(value);
    }
}
