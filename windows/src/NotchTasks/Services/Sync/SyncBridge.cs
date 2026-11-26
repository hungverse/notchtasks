using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Microsoft.Win32;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Sync;
using NotchTasks.Pill;

namespace NotchTasks.Services.Sync;

/// The app's state to the phone, and the phone's answers to the app: the Electron main.js sync wiring (syncInput, agentOf, notchOf,
/// applyNotch, onAnswer, onProNote, and announce's record). The hub runs on the thread pool, so its events are marshalled to the UI
/// dispatcher with BeginInvoke and nothing here blocks. Create builds the production hub and owns what it makes; a test builds the
/// hub over fakes and passes it in.
public sealed class SyncBridge : IAsyncDisposable
{
    private const string ProNoteTint = "#FF9F0A";
    private const int TimerCapMinutes = 180;
    private const string LanKey = "syncLAN";
    private const string OnlyAwayKey = "syncOnlyAway";
    private const string NoteKeyPrefix = "lastNotchProNoteAt.";

    /// The module ids, as the notch's own sound keys (Electron SOUND_KEYS): applied from the phone in this order.
    private static readonly string[] SoundKeys =
    [
        "size", "expandMode", "accent", "customAccent", "background", "gradientA", "gradientB", "toastStyle", "motion",
        "sounds", "volume", "systemNotifications", "doneAfter", "limitWarn", "modules", "disabled", "placements",
    ];

    private static readonly string[] PlacementValues = ["left", "right", "auto"];

    private readonly AppState _app;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable[] _owned;
    private SyncState _state;
    private bool _running;
    private bool _disposed;

    /// A bridge over a hub that is already built. The bridge owns nothing: the caller disposes what it made.
    public SyncBridge(AppState app, SyncHub hub)
        : this(app, hub, Array.Empty<IDisposable>())
    {
    }

    private SyncBridge(AppState app, SyncHub hub, IDisposable[] owned)
    {
        _app = app;
        Hub = hub;
        _owned = owned;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _state = hub.State;
        hub.StateChanged += state => Post(() =>
        {
            var becamePaired = state.Paired && !_state.Paired;
            _state = state;
            Changed?.Invoke();
            if (becamePaired)
            {
                Update(); // the phones get the app's state as soon as the PC is paired, not at the next publish
            }
        });
        hub.Answered += answer => Post(() => OnAnswer(answer));
        hub.ProNote += text => Post(() => _app.Announce(new Toast("sparkles", ProNoteTint, text, Event: null), notify: false));
    }

    /// The hub that talks to the phones.
    public SyncHub Hub { get; }

    /// What the Pairing pane shows: the hub's last state, read on the UI thread.
    public SyncState State => _state;

    /// Raised on the UI thread when State changes (the Pairing pane redraws).
    public event Action? Changed;

    /// The production bridge: this PC's OS parts (the away watch, the HTTP client, the group vault, DNS-SD, the clock, the host
    /// name, the Pro note memory in state.json and the two switches) under the hub make builds. The bridge disposes the away watch
    /// and the HTTP client it made.
    internal static SyncBridge Create(AppState app, Func<SyncDeps, SyncHub> makeHub)
    {
        var away = new AwayWatch();
        var http = new SyncHttp();
        var deps = new SyncDeps(
            http,
            new GroupVault(AppPaths.DataDir, text => ErrorLog.Write(new InvalidOperationException("NotchTasks sync vault: " + text))),
            () => new DnsSdAdvertiser(),
            Dns.GetHostName(),
            () => DateTimeOffset.Now,
            away.IsAway,
            GetNoteAt,
            SetNoteAt,
            text => ErrorLog.Write(new InvalidOperationException("NotchTasks sync: " + text)))
        {
            Lan = ReadFlag(LanKey, true),
            OnlyAway = ReadFlag(OnlyAwayKey, true),
        };
        SyncHub hub;
        try
        {
            hub = makeHub(deps);
        }
        catch
        {
            away.Dispose();
            http.Dispose();
            throw;
        }
        return new SyncBridge(app, hub, new IDisposable[] { away, http });
    }

    /// Starts the hub (the answers stream, the 1 s publish tick while paired, the LAN listener) and sends the state. A failure is
    /// logged; the hub keeps what it knows.
    public async Task StartAsync()
    {
        if (_disposed || _running)
        {
            return;
        }
        _running = true;
        // a resume from sleep or a network change restarts the answers stream at once (a dead connection is not noticed for long)
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        try
        {
            await Hub.StartAsync();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        Update();
    }

    /// Sends the app's state to the hub: called at the end of every publish. Building the input is cheap; the hub ignores an
    /// unchanged state. Unpaired, nothing goes to a phone, so the input is not built.
    public void Update()
    {
        if (_disposed || !_running || Hub.Group is null)
        {
            return;
        }
        var input = InputOf(_app.Snapshot, _app.Asks, _app.Thermal, _app.Settings);
        LastInput = input;
        Hub.Update(input);
    }

    /// The input the bridge last sent the hub (null until the first update). Tests read it to see what the phone is given.
    internal SyncInput? LastInput { get; private set; }

    /// A notice worth knowing on the phone: the toast's event, title and subtitle. A toast with no event is not for the phone. A done
    /// toast of an agent or a build is sent to the phone (its ToPhone, Electron toPhone); the other kinds reach it only as the hub decides.
    public void Record(Toast t)
    {
        if (_disposed || t.Event is not { } kind)
        {
            return;
        }
        Hub.Record(kind, t.Title, t.Subtitle, t.ToPhone);
    }

    /// Saves the LAN switch (state.json syncLAN) and applies it to the hub.
    public Task SetLan(bool on)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }
        WriteFlag(LanKey, on);
        return Hub.SetLanAsync(on);
    }

    /// Saves the "only when away" switch (state.json syncOnlyAway) and applies it to the hub.
    public void SetOnlyAway(bool on)
    {
        if (_disposed)
        {
            return;
        }
        WriteFlag(OnlyAwayKey, on);
        Hub.SetOnlyAway(on);
    }

    /// Stops the hub and releases what the bridge made. Nothing is raised after this.
    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    /// Stops the hub and releases what the bridge made, synchronously (the hub's stop completes at once).
    internal void Close()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _running = false;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        // StopAsync completes at once today. It is not awaited here (the UI thread must not block on it), so a fault it reports
        // later is observed and logged instead of being dropped; a synchronous throw is logged here.
        try
        {
            _ = Hub.StopAsync().ContinueWith(t => ErrorLog.Write(t.Exception!), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        foreach (var item in _owned)
        {
            item.Dispose();
        }
    }

    /// The phone's answer, on the UI thread (Electron onAnswer): a timer (1 to 180 minutes, 0 stops it), a notch edit, or an ask's
    /// reply. An answer for an ask that is no longer waiting is ignored.
    internal void OnAnswer(PhoneAnswer a)
    {
        if (a.Timer is { } minutes)
        {
            if (minutes > 0)
            {
                _app.Timer.Start(Math.Min(minutes, TimerCapMinutes), DateTimeOffset.Now);
            }
            else
            {
                _app.Timer.Stop();
            }
            _app.Publish();
            return;
        }
        if (a.Notch is { } notch)
        {
            ApplyNotch(_app.Settings, notch);
            _app.Publish();
            return;
        }
        if (a.Ask.Length > 0 && _app.Asks.Any(ask => ask.Id == a.Ask))
        {
            _app.Answer(a.Ask, a.Reply);
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            ReconnectHub();
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => ReconnectHub();

    /// Restarts the answers stream now (the hub does nothing when it is not paired). Called from the system's event threads.
    private void ReconnectHub()
    {
        if (!_disposed && _running)
        {
            Hub.Reconnect();
        }
    }

    /// Runs a hub event on the UI thread, later and without waiting. Events after the bridge is disposed are dropped.
    private void Post(Action action) =>
        _dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_disposed)
            {
                action();
            }
        }));

    /// The sync input: the runs and builds on screen, the asks, the limits, the health and the notch (Electron syncInput). Runs
    /// keep the agent they belong to; a build row takes its tool's icon when the tool has one.
    internal static SyncInput InputOf(NotchSnapshot s, IReadOnlyList<AgentAsk> asks, ThermalReading? thermal, SettingsStore settings)
    {
        var toolIcons = settings.ToolIcons;
        var runs = new List<SyncRun>();
        foreach (var state in s.Running)
        {
            foreach (var job in state.Jobs.Where(j => j.Working))
            {
                runs.Add(new SyncRun(job.Id, AgentOf(state.Item), job.Title, job.Detail, DateTimeOffset.UnixEpoch, ItemIcon.BadgeOf(state.Item)));
            }
        }
        var builds = new List<SyncRun>();
        foreach (var state in s.BuildRunning)
        {
            foreach (var job in state.Jobs.Where(j => j.Working))
            {
                var icon = ToolIcon(job.Tool, toolIcons) ?? ItemIcon.BadgeOf(state.Item);
                builds.Add(new SyncRun(job.Id, null, job.Title, job.Detail, DateTimeOffset.UnixEpoch, icon));
            }
        }
        var limits = s.Limits
            .SelectMany(p => p.Windows.Select(w => new SyncLimitInput(p.Name, w.Label, w.Used, w.Resets)))
            .ToList();
        var health = new SyncHealthInput(
            s.Battery?.Percent,
            s.Battery?.Charging ?? false,
            thermal?.Celsius,
            s.Load?.Cpu,
            s.Load?.Memory);
        return new SyncInput(
            runs,
            builds,
            asks.Select(a => new SyncAsk(a, null)).ToList(),
            limits,
            health,
            NotchOf(settings));
    }

    /// The agent a watch item runs: Claude and Codex by their kind, the others by their CLI name (Electron agentOf and AGENT_BY_CLI).
    /// Null when the item is not one of the agents the phone knows.
    internal static string? AgentOf(WatchItem item)
    {
        var agent = item.Kind switch
        {
            WatchKind.Claude => "claude",
            WatchKind.Codex => "codex",
            _ => item.Cli switch
            {
                "agy" => "antigravity",
                "gemini" => "gemini",
                "cursor-agent" => "cursor",
                "opencode" => "opencode",
                _ => null,
            },
        };
        return agent is not null && Wire.AgentKinds.Contains(agent) ? agent : null;
    }

    /// The notch's look as the phone takes it (Electron notchOf, P4-R9): the lock fields are off and empty, and the seen tools are the
    /// first twelve.
    internal static JsonObject NotchOf(SettingsStore s)
    {
        var placements = new JsonObject();
        foreach (var (module, where) in s.Placements)
        {
            placements[module] = where;
        }
        var toolIcons = new JsonObject();
        foreach (var (tool, badge) in s.ToolIcons)
        {
            toolIcons[tool] = badge.DeepClone();
        }
        return new JsonObject
        {
            ["size"] = s.Size,
            ["expandMode"] = s.ExpandMode,
            ["accent"] = s.Accent,
            ["customAccent"] = s.CustomAccent,
            ["background"] = s.Background,
            ["gradientA"] = s.GradientA,
            ["gradientB"] = s.GradientB,
            ["toastStyle"] = s.ToastStyle,
            ["motion"] = s.Motion,
            ["modules"] = StringArray(ModuleCatalog.Order(s.Modules)),  // the full order the Mac sends, so the phone shows the same rows
            ["disabled"] = StringArray(s.Disabled),
            ["placements"] = placements,
            ["haptics"] = false,
            ["sounds"] = s.Sounds,
            ["volume"] = s.Volume,
            ["systemNotifications"] = s.SystemNotifications,
            ["doneAfter"] = s.DoneAfter,
            ["limitWarn"] = s.LimitWarn,
            ["lockScreen"] = false,
            ["lockShows"] = new JsonArray(),
            ["badges"] = s.Badges,
            ["lockOwnBadges"] = false,
            ["lockBadges"] = new JsonArray(),
            ["toolIcons"] = toolIcons,
            ["seenTools"] = StringArray(s.SeenTools.Take(12)),
        };
    }

    /// The phone's edit of the notch (Electron applyNotch, P4-R9): each sound key the phone sends is checked and written, a value
    /// this PC does not take stays as it is. Badges and tool icons keep only the pictures this PC already holds.
    internal static void ApplyNotch(SettingsStore s, JsonObject n)
    {
        foreach (var key in SoundKeys)
        {
            if (n[key] is not { } value || Checked(key, value) is not { } clean)
            {
                continue;
            }
            TrySet(s, key, clean);
        }

        // Badges go through the settings' badge check (SetBadges drops an invalid one); tool icons through SetToolIcon, one by one.
        if (n["badges"] is JsonArray badges)
        {
            var current = s.Badges.OfType<JsonObject>().ToList();
            var kept = new List<JsonObject>();
            foreach (var node in badges)
            {
                if (node is JsonObject badge && KeepPicture(badge, current))
                {
                    kept.Add((JsonObject)badge.DeepClone());
                }
            }
            try
            {
                s.SetBadges(kept);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(new IOException("NotchTasks: could not save the phone's badges", e));
            }
        }

        if (n["toolIcons"] is JsonObject tools)
        {
            var seen = s.SeenTools;
            var current = s.ToolIcons.Values.ToList();
            var kept = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var (tool, node) in tools)
            {
                if (node is JsonObject badge && seen.Contains(tool) && KeepPicture(badge, current))
                {
                    kept[tool] = (JsonObject)badge.DeepClone();
                }
            }
            // The map is replaced as the phone sent it: a tool the phone no longer names loses its icon.
            foreach (var tool in s.ToolIcons.Keys.Where(t => !kept.ContainsKey(t)).ToList())
            {
                TrySetToolIcon(s, tool, null);
            }
            foreach (var (tool, badge) in kept)
            {
                TrySetToolIcon(s, tool, badge);  // a badge the check refuses is not saved, and the old one stays
            }
        }
    }

    /// A badge with a picture is kept only when a current badge holds the same picture: the same image (a missing image never
    /// matches a missing image) and the same frames. Electron's keepPicture matched on the image alone; here a badge that has
    /// frames but no image has no picture this PC can match, so it is dropped.
    private static bool KeepPicture(JsonObject badge, IEnumerable<JsonObject> current)
    {
        if (badge["image"] is null && badge["frames"] is null)
        {
            return true;
        }
        var image = TextOf(badge["image"]);
        if (image is null)
        {
            return false;
        }
        return current.Any(x => TextOf(x["image"]) == image && JsonNode.DeepEquals(x["frames"], badge["frames"]));
    }

    /// Saves one tool's icon through the settings (SetToolIcon). A file that cannot be written is logged and the icon stays as it was.
    private static void TrySetToolIcon(SettingsStore s, string tool, JsonObject? badge)
    {
        try
        {
            _ = s.SetToolIcon(tool, badge);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException($"NotchTasks: could not save the phone's tool icon for {tool}", e));
        }
    }

    /// The value the notch takes for a key, clamped or filtered as the settings take it; null when this PC does not take it.
    private static JsonNode? Checked(string key, JsonNode value) => key switch
    {
        "size" => Choice(value, Values(Choices.Sizes)),
        "expandMode" => Choice(value, Values(Choices.ExpandModes)),
        "accent" => Choice(value, Appearance.AccentNames),
        "background" => Choice(value, Values(Choices.Backgrounds)),
        "toastStyle" => Choice(value, Values(Choices.ToastStyles)),
        "motion" => Choice(value, Values(Choices.Motions)),
        "customAccent" or "gradientA" or "gradientB" => Hex(value),
        "sounds" or "systemNotifications" => Flag(value),
        "volume" => Number(value, 0, 1),
        "doneAfter" => Number(value, 5, 600),
        "limitWarn" => Number(value, 50, 100),
        "modules" or "disabled" => ModuleList(value),
        "placements" => Placements(value),
        _ => null,
    };

    private static IEnumerable<string> Values(IEnumerable<Choices.Option> options) => options.Select(o => o.Value);

    private static JsonNode? Choice(JsonNode value, IEnumerable<string> allowed) =>
        TextOf(value) is { } text && allowed.Contains(text) ? JsonValue.Create(text) : null;

    private static JsonNode? Hex(JsonNode value) =>
        TextOf(value) is { } text && text.Length == 7 && text[0] == '#' && text.Skip(1).All(Uri.IsHexDigit)
            ? JsonValue.Create(text)
            : null;

    private static JsonNode? Flag(JsonNode value) =>
        value is JsonValue v && v.TryGetValue(out bool flag) ? JsonValue.Create(flag) : null;

    private static JsonNode? Number(JsonNode value, double lo, double hi) =>
        value is JsonValue v && v.TryGetValue(out double number) && !double.IsNaN(number)
            ? JsonValue.Create(Math.Clamp(number, lo, hi))
            : null;

    private static JsonNode? ModuleList(JsonNode value) =>
        value is JsonArray list
            ? StringArray(list.Select(TextOf).OfType<string>().Where(id => ModuleCatalog.All.Contains(id)))
            : null;

    private static JsonNode? Placements(JsonNode value)
    {
        if (value is not JsonObject saved)
        {
            return null;
        }
        var clean = new JsonObject();
        foreach (var (module, where) in saved)
        {
            if (ModuleCatalog.All.Contains(module) && TextOf(where) is { } text && PlacementValues.Contains(text))
            {
                clean[module] = text;
            }
        }
        return clean;
    }

    private static string? TextOf(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    private static JsonObject? ToolIcon(string? tool, IReadOnlyDictionary<string, JsonObject> icons) =>
        !string.IsNullOrEmpty(tool) && icons.TryGetValue(tool, out var badge) ? badge : null;

    private static JsonArray StringArray(IEnumerable<string> items) =>
        new(items.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray());

    /// Writes one setting. A write the file refuses (it is locked or read-only) is logged and the value stays as it was.
    private static void TrySet(SettingsStore s, string key, JsonNode value)
    {
        try
        {
            s.Set(key, value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException($"NotchTasks: could not save the phone's {key}", e));
        }
    }

    private static bool ReadFlag(string key, bool fallback)
    {
        try
        {
            return StateJson.Get(AppPaths.StateFile, key) is JsonValue v && v.TryGetValue(out bool flag) ? flag : fallback;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException($"NotchTasks: could not read {key}", e));
            return fallback;
        }
    }

    private static void WriteFlag(string key, bool on)
    {
        try
        {
            StateJson.Set(AppPaths.StateFile, key, JsonValue.Create(on));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException($"NotchTasks: could not save {key}", e));
        }
    }

    /// The time of the last Pro note this group showed, in ms since 1970 (state.json lastNotchProNoteAt.{g}). When it cannot be read,
    /// the note is taken as seen: a failing file must not repeat it every two minutes.
    private static double GetNoteAt(string group)
    {
        try
        {
            return StateJson.Get(AppPaths.StateFile, NoteKeyPrefix + group) is JsonValue v && v.TryGetValue(out double at) ? at : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException("NotchTasks: could not read the Pro note time", e));
            return double.PositiveInfinity;
        }
    }

    private static void SetNoteAt(string group, double at)
    {
        try
        {
            StateJson.Set(AppPaths.StateFile, NoteKeyPrefix + group, JsonValue.Create(at));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException("NotchTasks: could not save the Pro note time", e));
        }
    }
}
