using System.IO.Pipelines;
using System.Net;
using System.Text;
using NotchTasks.Core.Sync;

namespace NotchTasks.Core.Tests.Sync;

/// A Firebase stand-in, the fakeFirebase of electron/test/sync.test.js: it records every call, answers the anonymous sign-up, the token
/// renewal and the group's paths from its settings, and serves the answers stream from a pipe the test writes server-sent events into.
internal sealed class FakeFirebase : ISyncHttp
{
    private readonly object _gate = new();
    private readonly List<Call> _calls = [];
    private readonly List<(string Method, string Contains, int Status)> _failures = [];
    private PipeWriter? _events;
    private int _streamFailures;

    public sealed record Call(string Method, string Url, string? Body, string ContentType);

    /// The entitlement JSON the group's path answers with ("null" for none).
    public string EntitlementJson { get; set; } = "null";

    public string GrantsJson { get; set; } = "null";

    public string ProNoteJson { get; set; } = "null";

    /// The members JSON the group's members path answers with.
    public string MembersJson { get; set; } = "{}";

    /// When set, the snapshot PUT waits for this task before it answers.
    public TaskCompletionSource? HoldSnapshot { get; set; }

    /// The writer of the answers stream that was opened last. Null until a stream is open.
    public PipeWriter? Events
    {
        get
        {
            lock (_gate)
            {
                return _events;
            }
        }
    }

    /// Every call so far, oldest first.
    public IReadOnlyList<Call> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    /// The next call that matches the method and a part of the URL answers with the status, once.
    public void FailNext(string method, string urlContains, int status)
    {
        lock (_gate)
        {
            _failures.Add((method, urlContains, status));
        }
    }

    /// Makes the next n event-stream opens fail (a lost connection).
    public void FailStreamOpens(int n)
    {
        lock (_gate)
        {
            _streamFailures = n;
        }
    }

    public async Task<SyncHttpResult> SendAsync(string method, string url, string? body, CancellationToken ct, string contentType = "application/json")
    {
        lock (_gate)
        {
            _calls.Add(new Call(method, url, body, contentType));
        }
        var status = TakeFailure(method, url);
        if (status is not null)
        {
            return new SyncHttpResult(status.Value, "{\"error\":\"fake\"}");
        }
        if (method == "PUT" && url.Contains("/snapshot.json", StringComparison.Ordinal) && HoldSnapshot is { } hold)
        {
            await hold.Task;
        }
        return Answer(method, url);
    }

    public Task<Stream> OpenEventStreamAsync(string url, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(new Call("GET", url, null, "text/event-stream"));
            if (_streamFailures > 0)
            {
                _streamFailures--;
                return Task.FromException<Stream>(new HttpRequestException("stream failed", null, HttpStatusCode.ServiceUnavailable));
            }
        }
        var pipe = new Pipe();
        lock (_gate)
        {
            _events = pipe.Writer;
        }
        _ = ct.Register(() => pipe.Writer.Complete());
        return Task.FromResult(pipe.Reader.AsStream());
    }

    /// Writes server-sent event text into the open answers stream.
    public async Task SendEventAsync(string text)
    {
        var writer = Events ?? throw new InvalidOperationException("no answers stream is open");
        await writer.WriteAsync(Encoding.UTF8.GetBytes(text));
    }

    private int? TakeFailure(string method, string url)
    {
        lock (_gate)
        {
            var i = _failures.FindIndex(f => f.Method == method && url.Contains(f.Contains, StringComparison.Ordinal));
            if (i < 0)
            {
                return null;
            }
            var status = _failures[i].Status;
            _failures.RemoveAt(i);
            return status;
        }
    }

    private SyncHttpResult Answer(string method, string url)
    {
        if (url.Contains("accounts:signUp", StringComparison.Ordinal))
        {
            return Ok("{\"idToken\":\"tok-1\",\"localId\":\"uid-mac\",\"refreshToken\":\"ref-1\"}");
        }
        if (url.Contains("securetoken", StringComparison.Ordinal))
        {
            return Ok("{\"id_token\":\"tok-2\"}");
        }
        if (url.Contains("/entitlement.json", StringComparison.Ordinal))
        {
            return Ok(EntitlementJson);
        }
        if (url.Contains("/grants.json", StringComparison.Ordinal))
        {
            return Ok(GrantsJson);
        }
        if (url.Contains("/proNote.json", StringComparison.Ordinal))
        {
            return Ok(ProNoteJson);
        }
        if (method == "GET" && url.Contains("/members.json", StringComparison.Ordinal))
        {
            return Ok(MembersJson);
        }
        if (method == "POST" && url.Contains("/reports.json", StringComparison.Ordinal))
        {
            return Ok("{\"name\":\"-NRep1\"}");
        }
        return Ok("null");
    }

    private static SyncHttpResult Ok(string body) => new(200, body);
}

/// A vault that keeps the group in memory and records every save (null is a forget).
internal sealed class FakeVault : IGroupVault
{
    public SyncGroup? Stored { get; set; }

    public List<SyncGroup?> Saves { get; } = [];

    public SyncGroup? Load() => Stored;

    public string? Save(SyncGroup? group)
    {
        Saves.Add(group);
        Stored = group;
        return null;
    }
}

/// An mDNS advertiser that does nothing (the LAN half is not under test here).
internal sealed class NullAdvertiser : IServiceAdvertiser
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
