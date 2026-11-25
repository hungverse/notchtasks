using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NotchTasks.Core.Asks;

namespace NotchTasks.Core.Sync;

/// The PC's side of phone sync: the cloud half of SyncHub in electron/src/core/sync.js (the Mac's Sources/Sync.swift). It holds the
/// group and its pairing, Pro, the snapshot written to Firebase, the answers stream and problem reports. State sits behind _gate and
/// is never touched while a network call is awaited; events are queued under _gate and raised by Flush after it is released.
public sealed partial class SyncHub : IAsyncDisposable
{
    /// The Mac app's Firebase client config (Config/GoogleService-Info.plist): public by design, the rules guard the data.
    public const string ApiKey = "AIzaSyDeHy-Giegt9j4OsBLOeYFqHilTZYQ0bDM";
    public const string Db = "https://notchtasks-default-rtdb.asia-southeast1.firebasedatabase.app";

    private const double ProMs = 120_000;   // the group's Pro is read at most this often
    private const double WriteMs = 60_000;  // non-ask changes reach Firebase at most this often (asks go at once)
    private const double WaitSeconds = AskStore.DefaultWaitSeconds; // an answer older than this is not applied

    private readonly object _gate = new();
    private readonly object _notifyGate = new(); // keeps events in the order they were queued
    private readonly SyncDeps _deps;
    private readonly FirebaseRest _rest;
    private readonly SnapshotBuilder _builder = new();
    private readonly List<Action> _outbox = [];
    private readonly Dictionary<string, string> _phones = new(StringComparer.Ordinal);
    private readonly HashSet<string> _direct = new(StringComparer.Ordinal); // phones proven on this Wi-Fi (the LAN half fills it)

    private SyncGroup? _group;
    private bool _running;
    private bool _lan;
    private bool _onlyAway;
    private bool _pro;
    private double? _proUntil;
    private IReadOnlyList<string> _proHolders = [];
    private ProNoteInfo? _proNote;
    private double _proAtMs = double.NegativeInfinity;
    private string? _problem;
    private int _lanPhones = 0;            // phones on the LAN (the LAN half counts them)
    private SyncInput? _latest;            // the last input from the app
    private SyncSnapshot? _sent;           // the last snapshot written to Firebase
    private bool? _sentAway;
    private bool _sending;
    private double _lastWriteMs = double.NegativeInfinity;
    private CrewSig _sigCrew = new(0, 0, double.NegativeInfinity);
    private long _eventAt;
    private string _eventBox = string.Empty;
    private CancellationTokenSource? _streamCts;
    private System.Threading.Timer? _timer;

    /// The state changed (the Pairing pane redraws from it).
    public event Action<SyncState>? StateChanged;

    /// A phone's answer to apply here: an ask reply, a timer, a notch edit.
    public event Action<PhoneAnswer>? Answered;

    /// A Pro note to show once: the text is already localised.
    public event Action<string>? ProNote;

    /// Loads the group the vault holds (none, or one that cannot be read, is unpaired).
    public SyncHub(SyncDeps deps)
    {
        _deps = deps;
        _rest = new FirebaseRest(deps.Http, deps.ApiKey, deps.Db, deps.Now);
        _lan = deps.Lan;
        _onlyAway = deps.OnlyAway;
        _group = LoadGroup();
    }

    /// What the Pairing pane shows now.
    public SyncState State
    {
        get
        {
            lock (_gate)
            {
                return BuildState();
            }
        }
    }

    /// The group this PC shares with the phones, or null when unpaired.
    public SyncGroup? Group
    {
        get
        {
            lock (_gate)
            {
                return _group;
            }
        }
    }

    /// The LAN listener's port. Null: there is no listener yet (the LAN half of the hub sets it).
    public int? LanPort => null;

    // MARK: lifecycle

    /// Starts the answers stream, the 1 s publish tick (only while paired) and reads the phones and Pro.
    public async Task StartAsync()
    {
        lock (_gate)
        {
            _running = true;
            UpdateTimer();
            StartStream();
            StartLan();
        }
        Flush();
        await LoadPhonesAsync();
    }

    /// Stops the stream, the tick and the LAN. The group stays paired.
    public Task StopAsync()
    {
        lock (_gate)
        {
            _running = false;
            UpdateTimer();
            StopStream();
            StopLan();
        }
        Flush();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// The 1 s tick: publishes what changed, and returns at once when nothing did. The timer calls it; tests call it directly.
    public void Tick()
    {
        lock (_gate)
        {
            PublishSafely();
        }
        Flush();
    }

    // MARK: pairing

    /// Creates the group when there is none (a fresh anonymous account owns it) and returns its code and URL.
    public async Task<(string Code, string Url)> PairAsync()
    {
        SyncGroup? existing;
        lock (_gate)
        {
            existing = _group;
        }
        var group = existing ?? await CreateGroupAsync();
        if (existing is null)
        {
            var problem = SaveVault(group);
            lock (_gate)
            {
                _group = group;
                _sent = null;
                _phones.Clear();
                ResetPro();
                _problem = problem;
                UpdateTimer();
                StartStream();
                StartLan();
            }
        }
        var code = Box.PairCode(group.G, group.K, _deps.MacName);
        lock (_gate)
        {
            Emit();
        }
        Flush();
        return (code, Box.PairUrl(code));
    }

    /// Reads the phones (the members with role phone) and the Pro state. A failed read keeps what was known.
    public async Task LoadPhonesAsync()
    {
        SyncGroup? g;
        lock (_gate)
        {
            g = _group;
        }
        if (g is null)
        {
            return;
        }
        await LoadPhonesCoreAsync(g);
        Flush();
    }

    /// Removes one phone: its member entry goes, and its Wi-Fi connection with it.
    public async Task UnpairAsync(string uid)
    {
        SyncGroup? g;
        lock (_gate)
        {
            g = _group;
        }
        if (g is null)
        {
            return;
        }
        await _rest.RestAsync("DELETE", $"groups/{g.G}/members/{uid}", g.Refresh, null, CancellationToken.None);
        lock (_gate)
        {
            _phones.Remove(uid);
            OnPhoneRemoved(uid);
            Emit();
        }
        Flush();
    }

    /// Removes every phone, then the PC's own member entry: nobody can read the old group. Pair again for a new code.
    public async Task UnpairAllAsync()
    {
        SyncGroup? g;
        lock (_gate)
        {
            g = _group;
            if (g is not null)
            {
                StopStream();
            }
        }
        if (g is null)
        {
            return;
        }
        await LoadPhonesCoreAsync(g);
        List<string> uids;
        lock (_gate)
        {
            uids = _phones.Keys.ToList();
        }
        foreach (var uid in uids)
        {
            await DeleteQuietlyAsync($"groups/{g.G}/members/{uid}", g.Refresh);
        }
        await DeleteQuietlyAsync($"groups/{g.G}/members/{g.Uid}", g.Refresh);
        var problem = SaveVault(null);
        lock (_gate)
        {
            _group = null;
            _phones.Clear();
            ResetPro();
            _rest.ClearToken();
            _sent = null;
            _problem = problem;
            UpdateTimer();
            StopLan();
            Emit();
        }
        Flush();
    }

    /// Switches the LAN listener on or off. The listener itself comes with the LAN half.
    public Task SetLanAsync(bool on)
    {
        lock (_gate)
        {
            _lan = on;
            StopLan();
            StartLan();
            Emit();
        }
        Flush();
        return Task.CompletedTask;
    }

    /// Whether the push and the notch events wait until the PC is away (the app's "only when away").
    public void SetOnlyAway(bool on)
    {
        lock (_gate)
        {
            _onlyAway = on;
            Emit();
        }
        Flush();
    }

    // MARK: the app's input, events for the phone, reports

    /// The app's snapshot input, whenever the model changes. Rounding, compare and throttle happen in the publish.
    public void Update(SyncInput input)
    {
        lock (_gate)
        {
            _latest = input;
            PublishSafely();
        }
        Flush();
    }

    /// A notice worth knowing on the phone (kind: taskDone, ciPassed, ciFailed, limitWarn, lowBattery, warning).
    public void Record(string kind, string title, string detail, bool toPhone)
    {
        lock (_gate)
        {
            var g = _group;
            if (g is null)
            {
                return;
            }
            var ev = _builder.Record(kind, title, detail, toPhone, AtMs(NowMs()));
            if (ev is null)
            {
                return;
            }
            // the push carries it only when away (limit warnings never wake the phone)
            if ((!_onlyAway || _deps.IsAway()) && kind != "limitWarn")
            {
                _eventAt = ev.At.ToUnixTimeMilliseconds();
                _eventBox = Box.Seal(EventJson(ev), Box.FromB64Url(g.K));
            }
        }
        Flush();
    }

    /// Writes a problem report under the group, or under a fresh anonymous account when unpaired. Returns the report's id.
    public async Task<string> SendReportAsync(string topic, string text, string contact, ReportInfo info)
    {
        SyncGroup? g;
        bool pro;
        double? proUntil;
        int phones;
        lock (_gate)
        {
            g = _group;
            pro = _pro;
            proUntil = _proUntil;
            phones = _phones.Count;
        }
        string uid, refresh, groupId = string.Empty;
        if (g is null)
        {
            var acc = await _rest.SignUpAsync(CancellationToken.None);
            uid = acc.Uid;
            refresh = acc.Refresh;
        }
        else
        {
            uid = g.Uid;
            refresh = g.Refresh;
            groupId = g.G;
        }
        var body = new JsonObject
        {
            ["uid"] = uid,
            ["g"] = Cut(groupId, 100),
            ["role"] = "mac",
            ["kind"] = "problem",
            ["auto"] = false,
            ["text"] = Cut($"{topic}: {text}", 2000),
            ["contact"] = Cut(contact, 200),
            ["app"] = new JsonObject
            {
                ["version"] = Cut(info.Version, 80),
                ["build"] = Cut(info.Build, 80),
                ["os"] = Cut(info.Os, 80),
                ["model"] = "PC",
                ["lang"] = Cut(info.Lang, 40),
            },
            ["pro"] = new JsonObject
            {
                ["own"] = false,
                ["product"] = string.Empty,
                ["tx"] = string.Empty,
                ["until"] = 0,
                ["group"] = pro,
                ["groupUntil"] = proUntil ?? 0,
            },
            ["context"] = new JsonObject { ["paired"] = g is not null, ["phones"] = phones },
            ["at"] = ServerTimestamp(),
        };
        var res = JsonNode.Parse(await _rest.RestAsync("POST", "reports", refresh, body, CancellationToken.None));
        if (res is not JsonObject ro || Str(ro["name"]) is not { } name)
        {
            throw new InvalidOperationException("no report id");
        }
        return name;
    }

    // MARK: pairing helpers

    private async Task<SyncGroup> CreateGroupAsync()
    {
        try
        {
            var acc = await _rest.SignUpAsync(CancellationToken.None);
            var (g, k) = Box.RandomGroup();
            var group = new SyncGroup(g, k, acc.Uid, acc.Refresh);
            await _rest.RestAsync(
                "PUT",
                $"groups/{g}/members/{acc.Uid}",
                acc.Refresh,
                new JsonObject { ["role"] = "mac", ["name"] = _deps.MacName, ["at"] = ServerTimestamp() },
                CancellationToken.None);
            return group;
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                SetProblem(L10n.L("Couldn't create a pairing code: {0}", e.Message));
            }
            Flush();
            throw;
        }
    }

    private async Task LoadPhonesCoreAsync(SyncGroup g)
    {
        try
        {
            var all = JsonNode.Parse(await _rest.RestAsync("GET", $"groups/{g.G}/members", g.Refresh, null, CancellationToken.None));
            if (all is JsonObject members)
            {
                var found = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (uid, m) in members)
                {
                    if (m is JsonObject mo && Str(mo["role"]) == "phone")
                    {
                        found[uid] = Str(mo["name"]) ?? L10n.L("Phone");
                    }
                }
                lock (_gate)
                {
                    _phones.Clear();
                    foreach (var (uid, name) in found)
                    {
                        _phones[uid] = name;
                    }
                }
            }
        }
        catch (Exception e)
        {
            _deps.Log($"members: {e.Message}");
        }
        await CheckProAsync(g, force: true);
        lock (_gate)
        {
            Emit();
        }
    }

    private async Task DeleteQuietlyAsync(string path, string refresh)
    {
        try
        {
            await _rest.RestAsync("DELETE", path, refresh, null, CancellationToken.None);
        }
        catch (Exception)
        {
            // a failed delete changes nothing the phone reads (sync.js: .catch(() => {}))
        }
    }

    // MARK: Pro (entitlement, grants, notices): read at most every 2 minutes

    private async Task CheckProAsync(SyncGroup g, bool force)
    {
        double nowMs;
        lock (_gate)
        {
            nowMs = NowMs();
            if (!force && nowMs - _proAtMs <= ProMs)
            {
                return;
            }
            _proAtMs = nowMs;
        }

        JsonNode? entitlement;
        try
        {
            entitlement = JsonNode.Parse(await _rest.RestAsync("GET", $"groups/{g.G}/entitlement", g.Refresh, null, CancellationToken.None));
        }
        catch (Exception)
        {
            return; // a failed read keeps what was known
        }
        var pro = SnapshotBuilder.IsPro(entitlement, nowMs);
        var proUntil = pro && entitlement is JsonObject e ? SnapshotBuilder.JsNumber(e["until"]) : (double?)null;

        IReadOnlyList<string> holders;
        try
        {
            var grants = JsonNode.Parse(await _rest.RestAsync("GET", $"groups/{g.G}/grants", g.Refresh, null, CancellationToken.None));
            holders = ValuesOf(grants)
                .OfType<JsonObject>()
                .Where(x => SnapshotBuilder.JsNumber(x["until"]) > nowMs)
                .Select(x => Str(x["uid"]))
                .OfType<string>()
                .ToList();
        }
        catch (Exception)
        {
            holders = [];
        }

        ProNoteInfo? note = null;
        string? noteText = null;
        try
        {
            var n = JsonNode.Parse(await _rest.RestAsync("GET", $"groups/{g.G}/proNote", g.Refresh, null, CancellationToken.None));
            if (n is JsonObject no && Str(no["why"]) is { } why && Num(no["at"]) is { } at)
            {
                note = new ProNoteInfo(why, at);
                var text = SnapshotBuilder.ProNoteText(why);
                if (text is not null && at > _deps.GetNoteAt(g.G))
                {
                    _deps.SetNoteAt(g.G, at);
                    noteText = text;
                }
            }
        }
        catch (Exception)
        {
            note = null;
        }

        lock (_gate)
        {
            _pro = pro;
            _proUntil = proUntil;
            _proHolders = holders;
            _proNote = note;
            if (noteText is not null)
            {
                var text = noteText;
                _outbox.Add(() => Raise(ProNote, text));
            }
            Emit();
        }
    }

    // MARK: Mac -> phone: the snapshot

    /// Writes what changed to Firebase. Called under _gate on every update and each second; one write is in flight at a time.
    private void Publish()
    {
        var g = _group;
        var input = _latest;
        if (g is null || input is null)
        {
            return;
        }
        var t = AtMs(NowMs());
        var s = _builder.Build(input, _deps.MacName, t);

        var handled = _direct.ToList();
        var needsCloud = _phones.Count == 0 ? _lanPhones == 0 : _phones.Keys.Any(u => !_direct.Contains(u));
        var away = !_onlyAway || _deps.IsAway();
        // just left (or came back) while asks wait: they may need a push now
        if (away != _sentAway && s.Asks.Count > 0)
        {
            _sent = null;
        }
        if (!needsCloud || _sending)
        {
            return;
        }
        if (_sent is not null && SameSnapshot(s, _sent))
        {
            return;
        }
        if (_sent is not null)
        {
            var askChanged = !SameAsks(s, _sent);
            // asks go at once; anything else only with Pro, and at most once a minute
            if (!askChanged && (!_pro || !ProChanged(s, _sent) || NowMs() - _lastWriteMs < WriteMs))
            {
                return;
            }
        }
        _sending = true;
        _lastWriteMs = NowMs();
        _ = Task.Run(() => WriteCloudAsync(g, s, handled, away));
    }

    private void PublishSafely()
    {
        try
        {
            Publish();
        }
        catch (Exception e)
        {
            _deps.Log($"publish: {e.Message}");
        }
    }

    private async Task WriteCloudAsync(SyncGroup g, SyncSnapshot s, IReadOnlyList<string> direct, bool away)
    {
        string? error = null;
        try
        {
            await CheckProAsync(g, force: false);
            JsonObject body;
            lock (_gate)
            {
                body = BuildWriteBody(g, s, direct, away);
            }
            await _rest.RestAsync("PUT", $"groups/{g.G}/snapshot", g.Refresh, body, CancellationToken.None);
        }
        catch (Exception e)
        {
            error = L10n.L("Couldn't send to the phone: {0}", e.Message);
        }
        lock (_gate)
        {
            // _sending and the problem change together, so a publish that follows sees both
            _sending = false;
            if (error is not null)
            {
                SetProblem(error);
            }
            else if (ReferenceEquals(_group, g)) // unpaired meanwhile: nothing to record
            {
                _sent = s;
                _sentAway = away;
                if (_problem is not null)
                {
                    SetProblem(null);
                }
            }
        }
        Flush();
    }

    /// The PUT body (sync.js writeCloud): the sealed parts, the push, the counts, and the `sig` the push function compares. Without
    /// Pro the sealed parts stay empty and only what the push needs goes out. Called under _gate.
    private JsonObject BuildWriteBody(SyncGroup g, SyncSnapshot s, IReadOnlyList<string> direct, bool away)
    {
        var key = Box.FromB64Url(g.K);
        var enc = Wire.Snapshot(s);
        var newest = Newest(s.Asks);
        var pro = _pro;
        var newestMs = newest is null ? 0L : MsOf(newest.Ask.At);
        var eventAt = pro ? _eventAt : 0L;
        var jobState = pro && s.Job is not null ? s.Job.State : string.Empty;
        var jobAt = pro ? MsOf(s.Job?.Since) : 0L;
        var crewCount = pro && s.Crew is not null ? s.Crew.Count : 0;
        var crewAt = pro ? MsOf(s.Crew?.Since) : 0L;

        var body = new JsonObject
        {
            ["box"] = pro ? Box.Seal(enc, key) : string.Empty,
            ["at"] = ServerTimestamp(),
            ["asks"] = s.Asks.Count,
            ["newest"] = newestMs,
            ["direct"] = DirectJson(direct),
            ["push"] = newest is null ? string.Empty : Box.Seal(Wire.Ask(Wire.AskForPush(newest)), key),
            ["quiet"] = !away,
            ["eventAt"] = eventAt,
            ["eventBox"] = pro ? _eventBox : string.Empty,
            ["job"] = pro && enc["job"] is { } job ? Box.Seal(job, key) : string.Empty,
            ["jobState"] = jobState,
            ["jobAt"] = jobAt,
            ["crew"] = pro && enc["crew"] is { } crew ? Box.Seal(crew, key) : string.Empty,
            ["crewCount"] = crewCount,
            ["crewAt"] = crewAt,
        };

        // `sig`: the push function runs only when this changes (each run is billed)
        _sigCrew = SnapshotBuilder.CrewSignal(crewCount, crewAt, _sigCrew, NowMs());
        body["sig"] = new JsonObject
        {
            ["crewCount"] = _sigCrew.Count,
            ["crewAt"] = (long)_sigCrew.At,
            ["asks"] = s.Asks.Count,
            ["newest"] = newestMs,
            ["direct"] = DirectJson(direct),
            ["quiet"] = !away,
            ["eventAt"] = eventAt,
            ["jobState"] = jobState,
            ["jobAt"] = jobAt,
        };
        return body;
    }

    // "Same" (sync.js same): the JSON text of the two values as the phone reads them. A value that cannot be written is never the
    // same, so it goes on to be reported.

    private static bool SameSnapshot(SyncSnapshot a, SyncSnapshot b) => SameText(SnapshotText(a), SnapshotText(b));

    private static bool SameAsks(SyncSnapshot a, SyncSnapshot b) => SameText(AsksText(a), AsksText(b));

    /// Port of proChanged in sync.js: the events, the job and crew markers, or the notch changed.
    private static bool ProChanged(SyncSnapshot s, SyncSnapshot prev) =>
        !SameText(EventsText(s), EventsText(prev))
        || !Pick(s).Equals(Pick(prev))
        || NotchText(s) != NotchText(prev);

    private static (string? State, long JobAt, int? Crew, long CrewAt) Pick(SyncSnapshot x) =>
        (x.Job?.State, MsOf(x.Job?.Since), x.Crew?.Count, MsOf(x.Crew?.Since));

    private static bool SameText(string? a, string? b) => a is not null && a == b;

    private static string? SnapshotText(SyncSnapshot s) => Canon(() => Wire.Snapshot(s with { At = DateTimeOffset.UnixEpoch }));

    private static string? AsksText(SyncSnapshot s) =>
        Canon(() => new JsonArray(s.Asks.Select(a => (JsonNode?)Wire.Ask(a)).ToArray()));

    private static string? EventsText(SyncSnapshot s) =>
        Canon(() => Wire.Snapshot(s with { At = DateTimeOffset.UnixEpoch })["events"]!);

    private static string NotchText(SyncSnapshot s) => s.Notch is null ? string.Empty : SyncJson.Write(s.Notch);

    private static string? Canon(Func<JsonNode> make)
    {
        try
        {
            return SyncJson.Write(make());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static SyncAsk? Newest(IReadOnlyList<SyncAsk> asks)
    {
        SyncAsk? best = null;
        foreach (var a in asks)
        {
            if (best is null || a.Ask.At > best.Ask.At)
            {
                best = a;
            }
        }
        return best;
    }

    // MARK: the answers stream (phone -> PC)

    private enum StreamEnd
    {
        Ended,
        Revoked,
    }

    /// Keeps one answers stream open while paired and running. Failures back off 5 s, 10 s, 20 s, up to a minute. Called under _gate.
    private void StartStream()
    {
        StopStream();
        var g = _group;
        if (!_running || g is null)
        {
            return;
        }
        var cts = new CancellationTokenSource();
        _streamCts = cts;
        _ = Task.Run(() => StreamLoopAsync(g, cts.Token));
    }

    private void StopStream()
    {
        if (_streamCts is null)
        {
            return;
        }
        _streamCts.Cancel();
        _streamCts = null;
    }

    private async Task StreamLoopAsync(SyncGroup g, CancellationToken ct)
    {
        var fails = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var stream = await _rest.OpenAnswersAsync(g.Refresh, g.G, ct);
                fails = 0;
                if (await ReadEventsAsync(stream, g, ct) == StreamEnd.Revoked)
                {
                    _rest.ClearToken();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                fails++;
                lock (_gate)
                {
                    SetProblem(L10n.L("Lost connection to the phone, retrying…"));
                }
                Flush();
            }
            var wait = TimeSpan.FromSeconds(Math.Min(60, 5 * Math.Pow(2, Math.Max(fails - 1, 0))));
            try
            {
                await _deps.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// Reads server-sent events (sync.js readEvents): put and patch carry answers; auth_revoked and cancel end the stream as revoked.
    private async Task<StreamEnd> ReadEventsAsync(Stream body, SyncGroup g, CancellationToken ct)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[4096];
        var buf = string.Empty;
        var ev = string.Empty;
        var data = new List<string>();
        while (true)
        {
            var n = await body.ReadAsync(bytes, ct);
            if (n == 0)
            {
                return StreamEnd.Ended;
            }
            var chars = new char[decoder.GetCharCount(bytes, 0, n, false)];
            var count = decoder.GetChars(bytes, 0, n, chars, 0, false);
            buf += new string(chars, 0, count);
            int i;
            while ((i = buf.IndexOf('\n')) >= 0)
            {
                var line = buf[..i];
                buf = buf[(i + 1)..];
                if (line.EndsWith('\r'))
                {
                    line = line[..^1];
                }
                if (line.Length == 0)
                {
                    if ((ev is "put" or "patch") && data.Count > 0)
                    {
                        await HandleAnswersAsync(string.Join("\n", data), g);
                    }
                    ev = string.Empty;
                    data.Clear();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    ev = line[6..].Trim();
                    if (ev is "auth_revoked" or "cancel")
                    {
                        return StreamEnd.Revoked;
                    }
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Add(line[5..].TrimStart());
                }
            }
        }
    }

    /// "/" carries every answer (or null); "/<id>" one answer. Each is applied if fresh, then deleted (as the Mac does).
    private async Task HandleAnswersAsync(string text, SyncGroup g)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return;
        }
        if (root is not JsonObject o || Str(o["path"]) is not { } path)
        {
            return;
        }
        var entries = new List<KeyValuePair<string, JsonNode?>>();
        if (path == "/")
        {
            if (o["data"] is JsonObject all)
            {
                entries.AddRange(all);
            }
        }
        else
        {
            entries.Add(new KeyValuePair<string, JsonNode?>(path.Length > 0 ? path[1..] : string.Empty, o["data"]));
        }

        foreach (var (id, v) in entries)
        {
            if (v is not JsonObject vo || Str(vo["box"]) is not { } boxText)
            {
                continue;
            }
            if (Num(vo["at"]) is { } at && (NowMs() - at) / 1000 < WaitSeconds)
            {
                try
                {
                    var answer = Wire.ReadAnswer(Box.Open(boxText, Box.FromB64Url(g.K)));
                    lock (_gate)
                    {
                        ApplyAnswer(answer);
                    }
                }
                catch (Exception e)
                {
                    _deps.Log($"answer: {e.Message}");
                }
            }
            await DeleteQuietlyAsync($"groups/{g.G}/answers/{id}", g.Refresh);
        }
        lock (_gate)
        {
            if (_problem is not null)
            {
                SetProblem(null);
            }
        }
        Flush();
    }

    /// Refresh: the phone opened, so the next publish writes everything. The rest is raised for the app, which replies, edits or times.
    /// Called under _gate.
    private void ApplyAnswer(PhoneAnswer a)
    {
        if (a.Refresh)
        {
            _sent = null;
            PublishSafely();
            return;
        }
        if (a.Ask.Length == 0 && a.Timer is null && a.Notch is null)
        {
            return; // the hello: nothing to apply
        }
        var answer = a;
        _outbox.Add(() => Raise(Answered, answer));
        PublishSafely();
    }

    // MARK: state, events, timer

    /// The state as the Pairing pane reads it. Called under _gate.
    private SyncState BuildState() => new(
        Paired: _group is not null,
        Phones: new Dictionary<string, string>(_phones, StringComparer.Ordinal),
        LanPhones: _lanPhones,
        Problem: _problem,
        Pro: _pro,
        ProUntil: _proUntil,
        ProHolders: _proHolders.ToList(),
        ProNote: _proNote,
        Lan: _lan,
        OnlyAway: _onlyAway);

    /// Queues a StateChanged with the state as it is now. Called under _gate.
    private void Emit()
    {
        var state = BuildState();
        _outbox.Add(() => Raise(StateChanged, state));
    }

    /// Sets the problem and queues the state. Called under _gate.
    private void SetProblem(string? problem)
    {
        _problem = problem;
        Emit();
    }

    private void ResetPro()
    {
        _pro = false;
        _proUntil = null;
        _proHolders = [];
        _proNote = null;
        _proAtMs = double.NegativeInfinity;
    }

    /// Runs the 1 s timer only while paired and running: nothing runs for an unpaired PC. Called under _gate.
    private void UpdateTimer()
    {
        var want = _running && _group is not null;
        if (want && _timer is null)
        {
            _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
        }
        else if (!want && _timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }
    }

    /// Raises the handlers one by one; a handler that throws is logged and the rest still run. Never called under _gate.
    private void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T>)handler)(value);
            }
            catch (Exception e)
            {
                _deps.Log($"event: {e.Message}");
            }
        }
    }

    /// Raises every queued event, outside _gate and in the order they were queued. Never call it under _gate.
    private void Flush()
    {
        lock (_notifyGate)
        {
            Action[] items;
            lock (_gate)
            {
                items = _outbox.ToArray();
                _outbox.Clear();
            }
            foreach (var item in items)
            {
                item();
            }
        }
    }

    // MARK: helpers

    private SyncGroup? LoadGroup()
    {
        try
        {
            return _deps.Vault.Load();
        }
        catch (Exception e)
        {
            _deps.Log($"vault: {e.Message}");
            return null;
        }
    }

    private string? SaveVault(SyncGroup? group)
    {
        try
        {
            return _deps.Vault.Save(group);
        }
        catch (Exception e)
        {
            _deps.Log($"vault: {e.Message}");
            return null;
        }
    }

    private double NowMs() => _deps.Now().ToUnixTimeMilliseconds();

    private static DateTimeOffset AtMs(double ms) => DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

    private static long MsOf(DateTimeOffset? d) => d is null ? 0 : d.Value.ToUnixTimeMilliseconds();

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static double? Num(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<double>(out var d) ? d : null;

    private static IEnumerable<JsonNode?> ValuesOf(JsonNode? node)
    {
        if (node is JsonObject o)
        {
            return o.Select(p => p.Value).ToList();
        }
        if (node is JsonArray a)
        {
            return a.ToList();
        }
        return Array.Empty<JsonNode?>();
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n];

    private static JsonObject ServerTimestamp() => new() { [".sv"] = "timestamp" };

    private static JsonObject DirectJson(IEnumerable<string> direct)
    {
        var o = new JsonObject();
        foreach (var u in direct)
        {
            o[u] = true;
        }
        return o;
    }

    private static JsonObject EventJson(SyncEvent ev) => new()
    {
        ["id"] = ev.Id,
        ["kind"] = ev.Kind,
        ["title"] = ev.Title,
        ["detail"] = ev.Detail,
        ["at"] = SwiftDate.From(ev.At),
    };

    // LAN hooks: the LAN half (SyncHub.Lan.cs) supplies these. Each is called under _gate.
    partial void StartLan();
    partial void StopLan();
    partial void OnPhoneRemoved(string uid);
}
