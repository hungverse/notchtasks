using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotchTasks.Core.Sync;

/// The LAN half of SyncHub (electron/src/core/sync.js startLAN, stopLAN, accept and the LAN part of publish): a TCP listener on
/// port 0 that phones on the same Wi-Fi reach, one read loop per connection, sealed frames both ways, the phones proven on the
/// Wi-Fi (they count as LanPhones and are left out of the cloud write), and the mDNS advertisement through the seam.
///
/// Locking: the state is read and written under _gate only. Socket work never runs under _gate: it is queued on _outbox (under
/// _gate) and runs in Flush, or runs on the connection's own tasks. Each connection writes one frame at a time (its SemaphoreSlim),
/// and frames leave in the order they were queued (each write waits for the one before it).
public sealed partial class SyncHub
{
    private const int MaxLanConnections = 8;
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<int, LanConn> _conns = new();
    private LanSession? _lanSession;
    private int? _lanPort;
    private int _connSeq;
    private string? _sentLocal; // the last snapshot text sent to the phones on the Wi-Fi (sync.js sentLocal)

    /// One listening session: the listener, the group's key, and the advertisement. A stop marks it Stopped.
    private sealed class LanSession(TcpListener listener, int port, byte[] key)
    {
        public TcpListener Listener { get; } = listener;

        public int Port { get; } = port;

        public byte[] Key { get; } = key;

        public IServiceAdvertiser? Advertiser { get; set; }

        public volatile bool Stopped;
    }

    /// One phone's connection. Ok once its first frame (the sealed hello) opened; Phone is the uid that frame named.
    private sealed class LanConn(int id, TcpClient client)
    {
        public int Id { get; } = id;

        public TcpClient Client { get; } = client;

        public NetworkStream Stream { get; } = client.GetStream();

        /// One write at a time on this connection.
        public SemaphoreSlim Gate { get; } = new(1, 1);

        /// The last queued write: the next frame waits for it, so frames leave in order. Touched only in Flush.
        public Task Tail { get; set; } = Task.CompletedTask;

        /// Fires when the hello is late. Null once the connection is Ok or closed.
        public CancellationTokenSource? Hello { get; set; }

        public bool Ok { get; set; }

        public string? Phone { get; set; }

        public volatile bool Closed;
    }

    // MARK: listening (sync.js startLAN / stopLAN)

    /// Listens on port 0 (all addresses) and queues the mDNS advertisement. Stops any listener first. Called under _gate.
    partial void StartLan()
    {
        StopLan();
        var g = _group;
        if (!_running || !_lan || g is null)
        {
            return;
        }
        TcpListener? listener = null;
        try
        {
            var key = Box.FromB64Url(g.K);
            listener = new TcpListener(_deps.LanBindAddress, 0);
            listener.Start();
            var session = new LanSession(listener, ((IPEndPoint)listener.LocalEndpoint).Port, key);
            var name = Box.ServiceName(g.G);
            _lanSession = session;
            _lanPort = session.Port;
            _outbox.Add(() => Advertise(session, name));
            _ = Task.Run(() => AcceptLoopAsync(session));
        }
        catch (Exception e)
        {
            listener?.Stop();
            _lanSession = null;
            _lanPort = null;
            _deps.Log($"lan: {e.Message}");
        }
    }

    /// Closes the listener and every connection, and unpublishes the advertisement. Called under _gate.
    partial void StopLan()
    {
        var session = _lanSession;
        var conns = _conns.Values.ToArray();
        _lanSession = null;
        _lanPort = null;
        _conns.Clear();
        foreach (var c in conns)
        {
            c.Closed = true;
            c.Hello?.Dispose();
            c.Hello = null;
        }
        RecountLan();
        _sentLocal = null;
        if (session is not null)
        {
            session.Stopped = true;
            _outbox.Add(() =>
            {
                try
                {
                    session.Listener.Stop();
                }
                catch (Exception e)
                {
                    _deps.Log($"lan: {e.Message}");
                }
                Unadvertise(session);
            });
        }
        if (conns.Length > 0)
        {
            _outbox.Add(() =>
            {
                foreach (var c in conns)
                {
                    c.Client.Dispose();
                }
            });
        }
    }

    /// Unpublishes the phone's connection when the phone is removed (sync.js unpair: its socket is destroyed, sent is cleared).
    /// Called under _gate.
    partial void OnPhoneRemoved(string uid)
    {
        foreach (var c in _conns.Values.Where(c => c.Phone == uid).ToArray())
        {
            var wasOk = c.Ok;
            Detach(c);
            if (wasOk)
            {
                _sent = null;
            }
            _outbox.Add(() => c.Client.Dispose());
        }
    }

    // MARK: accepting (sync.js accept)

    private async Task AcceptLoopAsync(LanSession s)
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await s.Listener.AcceptTcpClientAsync();
            }
            catch (Exception e)
            {
                // the listener is stopped on a stop; any other failure is logged and the loop goes on
                if (s.Stopped)
                {
                    return;
                }
                _deps.Log($"lan: accept failed ({e.GetType().Name})");
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                continue;
            }
            try
            {
                ConfigureSocket(client);
                if (!Admit(s, client))
                {
                    client.Dispose();
                }
            }
            catch (Exception e)
            {
                // this connection only: its socket is closed, the next one is still accepted
                client.Dispose();
                _deps.Log($"lan: connection setup failed ({e.GetType().Name})");
            }
        }
    }

    /// Admits the connection (up to 8), starts its hello timer and its read loop. Returns false when it is refused.
    private bool Admit(LanSession s, TcpClient client)
    {
        var full = false;
        LanConn? conn = null;
        lock (_gate)
        {
            if (s.Stopped)
            {
                return false;
            }
            full = _conns.Count >= MaxLanConnections;
            if (!full)
            {
                conn = new LanConn(++_connSeq, client)
                {
                    Hello = new CancellationTokenSource(_deps.HelloTimeout),
                };
                _conns[conn.Id] = conn;
            }
        }
        if (conn is not { } admitted)
        {
            if (full)
            {
                _deps.Log("lan: too many connections, refused");
            }
            return false;
        }
        try
        {
            // a connection that never says hello is not a phone
            admitted.Hello?.Token.Register(() => CloseConn(admitted, "lan: no hello in time", onlyIfNotOk: true));
        }
        catch (ObjectDisposedException)
        {
            // stopped in the meantime: the close already ran
        }
        _ = Task.Run(() => ReadLoopAsync(s, admitted));
        return true;
    }

    /// No Nagle delay, and TCP keep-alive after 3 s idle (sync.js setNoDelay, setKeepAlive(true, 3000)).
    private static void ConfigureSocket(TcpClient client)
    {
        client.NoDelay = true;
        var keepAlive = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(keepAlive.AsSpan(0), 1); // on
        BinaryPrimitives.WriteUInt32LittleEndian(keepAlive.AsSpan(4), 3000); // first probe after 3 s idle
        BinaryPrimitives.WriteUInt32LittleEndian(keepAlive.AsSpan(8), 3000); // then every 3 s
        client.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
    }

    /// Reads frames from one phone until it ends. A bad frame or a socket error closes only this connection.
    private async Task ReadLoopAsync(LanSession s, LanConn c)
    {
        var reader = new FrameReader();
        var buf = new byte[16 * 1024];
        string? reason = null;
        try
        {
            while (true)
            {
                var n = await c.Stream.ReadAsync(buf);
                if (n == 0)
                {
                    break;
                }
                foreach (var frame in reader.Push(buf.AsSpan(0, n)))
                {
                    OnPhoneFrame(c, ReadPhoneFrame(frame, s.Key));
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or CryptographicException or FormatException or JsonException)
        {
            reason = "lan: bad frame, connection closed";
        }
        catch (Exception)
        {
            // the socket ended or was closed: nothing to say
        }
        CloseConn(c, reason, onlyIfNotOk: false);
    }

    /// A frame is a sealed Answer (the same JSON the cloud answer holds), opened with the group key.
    private static PhoneAnswer ReadPhoneFrame(byte[] frame, byte[] key) =>
        Wire.ReadAnswer(JsonNode.Parse(Encoding.UTF8.GetString(Box.OpenBytes(frame, key))));

    /// The first frame that opens marks the connection Ok (its phone is proven on the Wi-Fi) and sends everything at once. A fresh
    /// answer is applied as on the cloud path. Called from the connection's read loop, never under _gate.
    private void OnPhoneFrame(LanConn c, PhoneAnswer a)
    {
        lock (_gate)
        {
            if (c.Closed)
            {
                return;
            }
            var phoneChanged = a.Phone is not null && a.Phone != c.Phone;
            if (a.Phone is not null)
            {
                c.Phone = a.Phone;
            }
            if (!c.Ok)
            {
                c.Ok = true;
                c.Hello?.Dispose();
                c.Hello = null;
                RecountLan();
                _sentLocal = null;
                Emit();
                PublishSafely();
            }
            else if (phoneChanged)
            {
                RecountLan(); // the proven-phone set follows the uid this connection now names
            }
            if ((NowMs() - a.At.ToUnixTimeMilliseconds()) / 1000 < WaitSeconds)
            {
                ApplyAnswer(a);
            }
        }
        Flush();
    }

    // MARK: sending (the LAN part of publish)

    /// Sends the snapshot to every proven phone when it changed since the last LAN send (sync.js publish: sentLocal). Called under
    /// _gate; the frames are queued for Flush.
    private void PublishLan(SyncGroup g, SyncSnapshot s)
    {
        if (_lanPhones == 0)
        {
            return;
        }
        var text = SnapshotText(s);
        if (text is null || text == _sentLocal)
        {
            return;
        }
        var frame = Box.Frame(Box.SealBytes(Encoding.UTF8.GetBytes(SyncJson.Write(Wire.Snapshot(s))), Box.FromB64Url(g.K)));
        _sentLocal = text;
        var targets = _conns.Values.Where(c => c.Ok).ToArray();
        _outbox.Add(() =>
        {
            foreach (var c in targets)
            {
                SendFrame(c, frame);
            }
        });
    }

    /// Queues one frame on a connection, behind the frames already queued. Called from Flush, outside _gate.
    private void SendFrame(LanConn c, byte[] frame)
    {
        var previous = c.Tail;
        c.Tail = WriteInOrderAsync(c, previous, frame);
    }

    /// Writes one frame once the one before it has finished. A write that fails or stalls closes only this connection.
    private async Task WriteInOrderAsync(LanConn c, Task previous, byte[] frame)
    {
        await Task.Yield(); // never inline in Flush: a failure closes the connection on its own task
        try
        {
            await previous;
        }
        catch (Exception)
        {
            // the write before this one failed (it closed the connection); this one still runs its own checks
        }
        if (c.Closed)
        {
            return;
        }
        await c.Gate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(WriteTimeout);
            await c.Stream.WriteAsync(frame, timeout.Token);
        }
        catch (Exception)
        {
            CloseConn(c, "lan: send failed, connection closed", onlyIfNotOk: false);
        }
        finally
        {
            c.Gate.Release();
        }
    }

    // MARK: closing

    /// Closes one connection and forgets it: its LanPhones count goes down, and the cloud sent marker is cleared when it was Ok.
    /// Called from the connection's own tasks, never under _gate and never from inside Flush.
    private void CloseConn(LanConn c, string? reason, bool onlyIfNotOk)
    {
        lock (_gate)
        {
            if (c.Closed || (onlyIfNotOk && c.Ok))
            {
                return;
            }
            var wasOk = c.Ok;
            Detach(c);
            if (wasOk)
            {
                _sent = null;
                Emit();
            }
        }
        if (reason is not null)
        {
            _deps.Log(reason);
        }
        c.Client.Dispose();
        Flush();
    }

    /// Takes a connection out of the table. Called under _gate.
    private void Detach(LanConn c)
    {
        c.Closed = true;
        _conns.Remove(c.Id);
        c.Hello?.Dispose();
        c.Hello = null;
        RecountLan();
    }

    /// LanPhones and the proven-phone set, from the open connections that said hello. Called under _gate.
    private void RecountLan()
    {
        _lanPhones = 0;
        _direct.Clear();
        foreach (var c in _conns.Values)
        {
            if (c.Ok)
            {
                _lanPhones++;
                if (c.Phone is not null)
                {
                    _direct.Add(c.Phone);
                }
            }
        }
    }

    // MARK: mDNS (through the advertiser seam; failures are logged, never thrown)

    private void Advertise(LanSession s, string name)
    {
        try
        {
            var advertiser = _deps.Advertiser();
            s.Advertiser = advertiser;
            advertiser.Publish(name, s.Port);
        }
        catch (Exception e)
        {
            _deps.Log($"mdns: {e.Message}");
        }
    }

    private void Unadvertise(LanSession s)
    {
        var advertiser = s.Advertiser;
        s.Advertiser = null;
        if (advertiser is null)
        {
            return;
        }
        try
        {
            advertiser.Unpublish();
        }
        catch (Exception e)
        {
            _deps.Log($"mdns: {e.Message}");
        }
        try
        {
            advertiser.Dispose();
        }
        catch (Exception e)
        {
            _deps.Log($"mdns: {e.Message}");
        }
    }
}
