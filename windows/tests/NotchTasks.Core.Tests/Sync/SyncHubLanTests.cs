using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Sync;
using Xunit;

namespace NotchTasks.Core.Tests.Sync;

/// Port of the three LAN tests of electron/test/sync.test.js (a sealed hello gets the snapshot as frames, a connection with no hello
/// is dropped, a bad frame drops it), plus the LAN half's own rules: the connection cap, the phone count, answers over the wire, the
/// phones on the Wi-Fi left out of the cloud write, the stop. Loopback TCP on port 0; the rig disposes every client it opened.
public sealed class SyncHubLanTests
{
    private const string GroupId = "group123456789012";
    private const long T0 = 1_800_000_000_000; // a fixed clock (ms)
    private const string TwoPhones = "{\"uid-1\":{\"role\":\"phone\",\"name\":\"Pixel\"},\"uid-2\":{\"role\":\"phone\",\"name\":\"Galaxy\"}}";
    private static readonly string KeyText = Crosscheck.Read("key.txt");
    private static readonly byte[] Key = Box.FromB64Url(KeyText);

    /// The clock the tests move by hand.
    private sealed class ManualClock
    {
        public double Ms { get; set; } = T0;

        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeMilliseconds((long)Ms);
    }

    /// The mDNS seam: it records what the hub told it.
    private sealed class RecordingAdvertiser : IServiceAdvertiser
    {
        private readonly object _gate = new();
        private readonly List<(string Name, int Port)> _published = [];
        private int _unpublished;
        private int _disposed;

        public (string Name, int Port)[] Published
        {
            get
            {
                lock (_gate)
                {
                    return _published.ToArray();
                }
            }
        }

        public int Unpublished
        {
            get
            {
                lock (_gate)
                {
                    return _unpublished;
                }
            }
        }

        public int Disposed
        {
            get
            {
                lock (_gate)
                {
                    return _disposed;
                }
            }
        }

        public void Publish(string instanceName, int port)
        {
            lock (_gate)
            {
                _published.Add((instanceName, port));
            }
        }

        public void Unpublish()
        {
            lock (_gate)
            {
                _unpublished++;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed++;
            }
        }
    }

    /// A hub wired to the fakes, with its states and answers collected in order. Every client a test opens is disposed with it.
    private sealed class Rig : IAsyncDisposable
    {
        private readonly ConcurrentQueue<SyncState> _states = new();
        private readonly ConcurrentQueue<PhoneAnswer> _answers = new();
        private readonly ConcurrentQueue<TcpClient> _clients = new();

        public Rig(string members = "{}", TimeSpan? helloTimeout = null)
        {
            Vault.Stored = new SyncGroup(GroupId, KeyText, "uid-mac", "ref-1");
            Fb.MembersJson = members;
            var deps = new SyncDeps(
                Fb,
                Vault,
                () => Adv,
                "Test PC",
                () => Clock.Now,
                () => false,
                _ => 0,
                (_, _) => { },
                _ => { })
            {
                HelloTimeout = helloTimeout ?? TimeSpan.FromSeconds(5),
                LanBindAddress = IPAddress.Loopback,
            };
            Hub = new SyncHub(deps);
            Hub.StateChanged += s => _states.Enqueue(s);
            Hub.Answered += a => _answers.Enqueue(a);
        }

        public FakeFirebase Fb { get; } = new();

        public FakeVault Vault { get; } = new();

        public RecordingAdvertiser Adv { get; } = new();

        public ManualClock Clock { get; } = new();

        public SyncHub Hub { get; }

        public IReadOnlyList<SyncState> States => _states.ToArray();

        public IReadOnlyList<PhoneAnswer> Answers => _answers.ToArray();

        /// The listener's port; the test fails if the hub is not listening.
        public int Port => Hub.LanPort ?? throw new InvalidOperationException("the hub is not listening");

        /// The snapshot PUTs so far, each parsed.
        public List<JsonNode> Snaps() => Fb.Calls
            .Where(c => c.Method == "PUT" && c.Url.Contains("/snapshot.json", StringComparison.Ordinal))
            .Select(c => JsonNode.Parse(c.Body!)!)
            .ToList();

        /// Opens a connection to the listener on loopback.
        public async Task<TcpClient> ConnectAsync()
        {
            var client = new TcpClient { NoDelay = true };
            _clients.Enqueue(client);
            await client.ConnectAsync(IPAddress.Loopback, Port);
            return client;
        }

        /// Connects and says hello as the phone uid; returns once the hub counts the phone on the Wi-Fi.
        public async Task<TcpClient> HelloAsync(string uid)
        {
            var before = Hub.State.LanPhones;
            var client = await ConnectAsync();
            await client.GetStream().WriteAsync(Frame(Answer(HelloJson(uid, SwiftDate.From(Clock.Now)))));
            await WaitFor(() => Hub.State.LanPhones == before + 1);
            return client;
        }

        /// Sends a sealed answer over the connection.
        public static Task SendAsync(TcpClient client, byte[] frame) => client.GetStream().WriteAsync(frame).AsTask();

        /// A sealed swift-allow answer from the Mac fixture, stamped at the given moment.
        public JsonNode AllowAnswer(DateTimeOffset at)
        {
            var answer = Box.Open(Crosscheck.Read("swift-allow.box"), Key);
            ((JsonObject)answer)["at"] = JsonValue.Create(SwiftDate.From(at));
            return answer;
        }

        /// Reads every frame the hub writes to the client, decrypted and parsed, until the client closes.
        public ConcurrentQueue<JsonNode> ReadFrames(TcpClient client)
        {
            var frames = new ConcurrentQueue<JsonNode>();
            _ = Task.Run(async () =>
            {
                var reader = new FrameReader();
                var buf = new byte[4096];
                try
                {
                    while (true)
                    {
                        var n = await client.GetStream().ReadAsync(buf, 0, buf.Length);
                        if (n == 0)
                        {
                            return;
                        }
                        foreach (var f in reader.Push(buf.AsSpan(0, n)))
                        {
                            frames.Enqueue(JsonNode.Parse(Encoding.UTF8.GetString(Box.OpenBytes(f, Key)))!);
                        }
                    }
                }
                catch (Exception)
                {
                    // the client was disposed, or the stream ended
                }
            });
            return frames;
        }

        public async ValueTask DisposeAsync()
        {
            await Hub.DisposeAsync();
            while (_clients.TryDequeue(out var client))
            {
                client.Dispose();
            }
        }
    }

    private static SyncRun Run(string id) => new(id, "claude", "api", "Running", DateTimeOffset.FromUnixTimeMilliseconds(T0), null);

    private static SyncInput Input(IReadOnlyList<SyncRun>? runs = null) => new(runs ?? [], [], [], [], null, null);

    private static string Str(JsonNode node, string key) => node[key]!.GetValue<string>();

    private static byte[] Frame(JsonNode answer) => Box.Frame(Box.SealBytes(Encoding.UTF8.GetBytes(SyncJson.Write(answer)), Key));

    private static JsonObject Answer(string json) => (JsonObject)JsonNode.Parse(json)!;

    /// The sealed hello a phone sends first: an empty ask, a pass reply, the phone's uid.
    private static string HelloJson(string uid, double at) =>
        $"{{\"ask\":\"\",\"reply\":{{\"pass\":{{}}}},\"at\":{SyncJson.Write(JsonValue.Create(at)!)},\"phone\":\"{uid}\"}}";

    private static async Task WaitFor(Func<bool> condition, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("timed out waiting");
            }
            await Task.Delay(5);
        }
    }

    /// True when the peer closes the connection (a read ends with no bytes, or with a reset) within the time.
    private static async Task<bool> WaitClosedAsync(TcpClient client, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        var buf = new byte[1024];
        try
        {
            while (DateTime.UtcNow < end)
            {
                var read = client.GetStream().ReadAsync(buf, 0, buf.Length);
                var left = end - DateTime.UtcNow;
                var first = await Task.WhenAny(read, Task.Delay(left > TimeSpan.Zero ? left : TimeSpan.Zero));
                if (!ReferenceEquals(first, read))
                {
                    return false;
                }
                if (await read == 0)
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return true;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        return false;
    }

    [Fact]
    public async Task Lan_Hello_SendsTheSnapshotAsAFrame_CountsOnePhone_AndWritesNothingToFirebase()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();
        rig.Hub.Update(Input([Run("r1")]));
        await WaitFor(() => rig.Snaps().Count == 1);

        Assert.True(rig.Hub.LanPort > 0);
        Assert.Equal(new[] { (Box.ServiceName(GroupId), rig.Port) }, rig.Adv.Published);

        var client = await rig.HelloAsync("uid-1");
        var frames = rig.ReadFrames(client);
        await WaitFor(() => frames.Count >= 1);
        Assert.True(frames.TryPeek(out var snapshot));
        Assert.Equal("Test PC", Str(snapshot!, "mac"));
        Assert.Equal("api", snapshot!["runs"]![0]!["title"]!.GetValue<string>());
        Assert.Equal(1, rig.Hub.State.LanPhones);

        await Task.Delay(50);
        Assert.Single(rig.Snaps());
    }

    [Fact]
    public async Task Lan_NoHello_IsDroppedAfterTheHelloTimeout()
    {
        await using var rig = new Rig(helloTimeout: TimeSpan.FromMilliseconds(100));
        await rig.Hub.StartAsync();

        var client = await rig.ConnectAsync();

        Assert.True(await WaitClosedAsync(client));
        Assert.Equal(0, rig.Hub.State.LanPhones);
    }

    [Fact]
    public async Task Lan_BadFrame_DropsTheConnection()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();

        var client = await rig.ConnectAsync();
        await client.GetStream().WriteAsync(Box.Frame(Enumerable.Repeat((byte)1, 40).ToArray()));

        Assert.True(await WaitClosedAsync(client));
    }

    [Fact]
    public async Task Lan_ZeroLengthOrTooBig_IsDropped()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();

        var zero = await rig.ConnectAsync();
        await zero.GetStream().WriteAsync(new byte[] { 0, 0, 0, 0 });

        var big = await rig.ConnectAsync();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)Box.MaxFrame + 1);
        await big.GetStream().WriteAsync(header);

        Assert.True(await WaitClosedAsync(zero));
        Assert.True(await WaitClosedAsync(big));
    }

    [Fact]
    public async Task Lan_NinthConnection_IsRefusedAtOnce()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();
        for (var i = 1; i <= 8; i++)
        {
            await rig.HelloAsync($"uid-{i}");
        }
        Assert.Equal(8, rig.Hub.State.LanPhones);

        var ninth = await rig.ConnectAsync();

        Assert.True(await WaitClosedAsync(ninth));
        Assert.Equal(8, rig.Hub.State.LanPhones);
    }

    [Fact]
    public async Task Lan_Close_DecrementsLanPhones_AndRaisesStateChanged()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();
        var client = await rig.HelloAsync("uid-1");

        client.Dispose();

        await WaitFor(() => rig.Hub.State.LanPhones == 0);
        await WaitFor(() => rig.States.LastOrDefault()?.LanPhones == 0);
    }

    [Fact]
    public async Task Lan_AnswerOverTheWire_IsRaised_WhenFresh()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();
        var client = await rig.HelloAsync("uid-1");

        await Rig.SendAsync(client, Frame(rig.AllowAnswer(rig.Clock.Now)));

        await WaitFor(() => rig.Answers.Count == 1);
        Assert.Equal("a1", rig.Answers[0].Ask);
        Assert.Equal(ReplyKinds.Allow, rig.Answers[0].Reply.Kind);
        await Task.Delay(50);
        Assert.Single(rig.Answers);
    }

    [Fact]
    public async Task Lan_AnswerOlderThan590Seconds_IsNotRaised()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();
        var client = await rig.HelloAsync("uid-1");

        await Rig.SendAsync(client, Frame(rig.AllowAnswer(rig.Clock.Now.AddMilliseconds(-600_000))));

        await Task.Delay(100);
        Assert.Empty(rig.Answers);
        Assert.Equal(1, rig.Hub.State.LanPhones);
    }

    [Fact]
    public async Task Lan_PhoneOnTheWifi_IsLeftOutOfDirect_WhenTheOtherPhoneIsAway()
    {
        await using var rig = new Rig(members: TwoPhones);
        await rig.Hub.StartAsync();
        Assert.Equal(2, rig.Hub.State.Phones.Count);
        await rig.HelloAsync("uid-1");

        rig.Hub.Update(Input([Run("r1")]));

        await WaitFor(() => rig.Snaps().Count == 1);
        var direct = rig.Snaps()[0]["direct"]!.AsObject();
        Assert.Single(direct);
        Assert.True(direct["uid-1"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Lan_UnpairingAPhone_ClosesItsConnection()
    {
        await using var rig = new Rig(members: "{\"uid-1\":{\"role\":\"phone\",\"name\":\"Pixel\"}}");
        await rig.Hub.StartAsync();
        var client = await rig.HelloAsync("uid-1");

        await rig.Hub.UnpairAsync("uid-1");

        Assert.True(await WaitClosedAsync(client));
        await WaitFor(() => rig.Hub.State.LanPhones == 0);
    }

    [Fact]
    public async Task SetLan_False_StopsTheListenerAndUnpublishes()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();
        var port = rig.Port;
        Assert.Equal(new[] { (Box.ServiceName(GroupId), port) }, rig.Adv.Published);

        await rig.Hub.SetLanAsync(false);

        Assert.Null(rig.Hub.LanPort);
        Assert.False(rig.Hub.State.Lan);
        Assert.Equal(1, rig.Adv.Unpublished);
        Assert.Equal(1, rig.Adv.Disposed);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
        });
    }

    [Fact]
    public async Task Stop_ClosesTheListenerAndEveryConnection()
    {
        await using var rig = new Rig();
        await rig.Hub.StartAsync();
        var client = await rig.HelloAsync("uid-1");
        var port = rig.Port;

        await rig.Hub.StopAsync();

        Assert.True(await WaitClosedAsync(client));
        Assert.Null(rig.Hub.LanPort);
        Assert.Equal(0, rig.Hub.State.LanPhones);
        await WaitFor(() => rig.States.LastOrDefault()?.LanPhones == 0);
        Assert.Equal(1, rig.Adv.Unpublished);
        Assert.Equal(1, rig.Adv.Disposed);
        await Assert.ThrowsAnyAsync<SocketException>(async () =>
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
        });
    }
}
