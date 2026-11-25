namespace NotchTasks.Core.Sync;

/// What a Firebase call answered: the HTTP status and the body text.
public sealed record SyncHttpResult(int Status, string Body);

/// The network, behind a seam: the app sends the calls with HttpClient, the tests with a fake. A body is JSON unless
/// contentType says otherwise (the token exchange sends a form). Throws on a transport failure.
public interface ISyncHttp
{
    Task<SyncHttpResult> SendAsync(string method, string url, string? body, CancellationToken ct, string contentType = "application/json");

    /// Opens the answers event stream (Accept: text/event-stream). Throws HttpRequestException (with StatusCode) on a non-2xx answer.
    Task<Stream> OpenEventStreamAsync(string url, CancellationToken ct);
}

/// Advertises the LAN listener over mDNS (the app's DnsServiceRegister). Publish and Unpublish may throw; the hub logs and goes on.
public interface IServiceAdvertiser : IDisposable
{
    void Publish(string instanceName, int port);
    void Unpublish();
}

/// Keeps the group this PC shares with the phones (the app's DPAPI file). Load returns null when there is none or it cannot be read.
/// Save(null) forgets the group. Save returns a problem text to show, or null.
public interface IGroupVault
{
    SyncGroup? Load();
    string? Save(SyncGroup? group);
}

/// Everything SyncHub needs from outside: the network, the vault, the clock, away, the Pro-note memory, the log, and the
/// Firebase config (ApiKey and Db default to the Mac's). HelloTimeout, Lan and OnlyAway are the initial switches.
public sealed record SyncDeps(
    ISyncHttp Http,
    IGroupVault Vault,
    Func<IServiceAdvertiser> Advertiser,
    string MacName,
    Func<DateTimeOffset> Now,
    Func<bool> IsAway,
    Func<string, double> GetNoteAt,
    Action<string, double> SetNoteAt,
    Action<string> Log)
{
    public string ApiKey { get; init; } = SyncHub.ApiKey;
    public string Db { get; init; } = SyncHub.Db;
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public bool Lan { get; init; } = true;
    public bool OnlyAway { get; init; } = true;

    /// The address the LAN listener binds. The app keeps Any (phones on the Wi-Fi reach it); the tests bind loopback only.
    public System.Net.IPAddress LanBindAddress { get; init; } = System.Net.IPAddress.Any;

    /// The backoff wait of the answers stream and of the LAN accept loop. Tests replace it; the app keeps Task.Delay.
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// Accepts the next phone connection on the LAN listener. Tests replace it to fail accepts; the app keeps AcceptTcpClientAsync.
    internal Func<System.Net.Sockets.TcpListener, Task<System.Net.Sockets.TcpClient>> AcceptAsync { get; init; } =
        listener => listener.AcceptTcpClientAsync();

    /// How long the answers stream may stay silent before it counts as lost (Firebase sends a keep-alive every 30 s). A dead
    /// connection (sleep, a Wi-Fi change) then ends the read and the stream reconnects. Tests lower it.
    internal TimeSpan StreamIdle { get; init; } = TimeSpan.FromSeconds(90);
}
