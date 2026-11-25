using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using NotchTasks.Core.Sync;
using NotchTasks.Services.Sync;

namespace NotchTasks.AppTests;

/// The Firebase stand-in of the Pairing pane and the report dialog: the anonymous sign-up can be held, the phones are the members
/// JSON given, the report write can fail. Every call is recorded as "METHOD url". Nothing reaches the network.
internal sealed class PairingHttp : ISyncHttp
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();

    /// The members the group's member path answers with (the phones with role phone).
    public string MembersJson { get; set; } = "{}";

    /// The Pro grants the group's grants path answers with: each grant names the uid that holds Pro and its end (ms since 1970).
    public string GrantsJson { get; set; } = "null";

    /// When set, the anonymous sign-up waits for this task before it answers (the code is being created).
    public TaskCompletionSource? HoldSignUp { get; set; }

    /// When true, the report write answers with an error.
    public bool FailReports { get; set; }

    /// The id the report write answers with (the report code is its last six characters).
    public string ReportId { get; set; } = "-NrReportAbcdef123456";

    /// Every call so far as "METHOD url", oldest first.
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    /// How many calls contain the text.
    public int Count(string contains)
    {
        lock (_gate)
        {
            return _calls.Count(call => call.Contains(contains, StringComparison.Ordinal));
        }
    }

    public async Task<SyncHttpResult> SendAsync(string method, string url, string? body, CancellationToken ct, string contentType = "application/json")
    {
        lock (_gate)
        {
            _calls.Add($"{method} {url}");
        }
        if (url.Contains("accounts:signUp", StringComparison.Ordinal))
        {
            if (HoldSignUp is { } hold)
            {
                await hold.Task;
            }
            return new SyncHttpResult(200, "{\"idToken\":\"tok-1\",\"localId\":\"uid-new\",\"refreshToken\":\"ref-new\"}");
        }
        if (url.Contains("securetoken", StringComparison.Ordinal))
        {
            return new SyncHttpResult(200, "{\"id_token\":\"tok-1\"}");
        }
        if (method == "POST" && url.Contains("/reports", StringComparison.Ordinal))
        {
            return FailReports
                ? new SyncHttpResult(500, "{\"error\":\"fake\"}")
                : new SyncHttpResult(200, $"{{\"name\":\"{ReportId}\"}}");
        }
        if (method == "GET" && url.Contains("/members", StringComparison.Ordinal))
        {
            return new SyncHttpResult(200, MembersJson);
        }
        if (method == "GET" && url.Contains("/grants", StringComparison.Ordinal))
        {
            return new SyncHttpResult(200, GrantsJson);
        }
        return new SyncHttpResult(200, "null");
    }

    public Task<Stream> OpenEventStreamAsync(string url, CancellationToken ct)
    {
        var pipe = new Pipe();
        _ = ct.Register(() => pipe.Writer.Complete());
        return Task.FromResult(pipe.Reader.AsStream());
    }
}

/// A vault that keeps the group in memory.
internal sealed class PairingVault : IGroupVault
{
    public SyncGroup? Stored { get; set; }

    public SyncGroup? Load() => Stored;

    public string? Save(SyncGroup? group)
    {
        Stored = group;
        return null;
    }
}

/// An mDNS advertiser that does nothing.
internal sealed class PairingAdvertiser : IServiceAdvertiser
{
    public void Publish(string instanceName, int port)
    {
    }

    public void Unpublish()
    {
    }

    public void Dispose()
    {
    }
}

/// Builds a sync hub over the fakes: paired (a group in the vault) or not. The LAN is off and binds loopback.
internal static class PairingRig
{
    public static SyncHub MakeHub(PairingHttp http, bool paired)
    {
        var vault = new PairingVault();
        if (paired)
        {
            var (g, k) = Box.RandomGroup();
            vault.Stored = new SyncGroup(g, k, "uid-mac", "refresh-1");
        }
        var notes = new Dictionary<string, double>(StringComparer.Ordinal);
        var deps = new SyncDeps(
            http,
            vault,
            () => new PairingAdvertiser(),
            "Test PC",
            () => DateTimeOffset.UtcNow,
            () => false,
            g => notes.TryGetValue(g, out var at) ? at : 0,
            (g, at) => notes[g] = at,
            _ => { })
        {
            LanBindAddress = IPAddress.Loopback,
            Lan = false,
            OnlyAway = false,
        };
        return new SyncHub(deps);
    }
}
