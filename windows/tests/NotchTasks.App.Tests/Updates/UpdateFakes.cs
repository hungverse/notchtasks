using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Updates;
using NotchTasks.Services.Updates;

namespace NotchTasks.AppTests.Updates;

/// The update feed and its installer as a fake HTTP handler. The installer URL answers with the installer bytes (after `hold`, when
/// given); every other URL is the feed and answers with a manifest. Each request's URL is recorded.
internal sealed class FakeUpdateServer : HttpMessageHandler
{
    public const string InstallerUrl = "https://dl.test/setup.exe";

    public static readonly byte[] Installer = Encoding.UTF8.GetBytes("fake installer bytes");

    private readonly Func<string, Task<HttpResponseMessage>> _answer;

    public FakeUpdateServer(Func<string, Task<HttpResponseMessage>> answer) => _answer = answer;

    /// The URLs requested so far, in order.
    public List<string> Requests { get; } = new();

    /// The checksum of bytes, as the feed's sha256 field spells it.
    public static string Sha256Of(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// A feed that offers `build` (its installer's checksum is the real one unless `sha256` says otherwise). `hold` delays the installer.
    public static FakeUpdateServer Offering(int build, string version = "3.0.1", string? sha256 = null, Task<bool>? hold = null)
    {
        var manifest = JsonSerializer.Serialize(new
        {
            version,
            build,
            url = InstallerUrl,
            sha256 = sha256 ?? Sha256Of(Installer),
            minOS = "10.0.0",
            notes = new Dictionary<string, string> { ["en"] = "Faster sync" },
        });
        return new FakeUpdateServer(async url =>
        {
            if (url == InstallerUrl)
            {
                if (hold is not null)
                {
                    await hold;
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Installer) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest, Encoding.UTF8, "application/json") };
        });
    }

    /// Every URL waits for `gate`, then answers 404 (no update). A check stays in progress until the gate opens.
    public static FakeUpdateServer Held(Task<bool> gate) =>
        new(async _ =>
        {
            await gate;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    /// Every URL answers with the same status and no body.
    public static FakeUpdateServer Status(HttpStatusCode code) =>
        new(_ => Task.FromResult(new HttpResponseMessage(code)));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.ToString());
        return _answer(request.RequestUri!.ToString());
    }
}

/// The outputs of one UpdateService under test: what it announced, what it launched and how often it quit the app. Create makes
/// the service over `server`, with its installer folder at `tempDir` and the state file of the current test root.
internal sealed class UpdateProbe
{
    public List<Toast> Announced { get; } = new();

    public List<string> Launched { get; } = new();

    public int Quits { get; private set; }

    /// What the launch returns for an installer path. True by default: the installer started.
    public Func<string, bool> Launch { get; set; } = _ => true;

    public UpdateService Create(FakeUpdateServer server, string tempDir) => new(
        new UpdateClient(server),
        () => AppPaths.StateFile,
        toast => Announced.Add(toast),
        () => Quits++,
        path =>
        {
            Launched.Add(path);
            return Launch(path);
        },
        Dispatcher.CurrentDispatcher,
        tempDir);
}
