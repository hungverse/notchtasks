using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Levels;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Media;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Core.Shelf;
using NotchTasks.Core.Sync;
using NotchTasks.Core.Updates;
using NotchTasks.Services;
using NotchTasks.Services.Appointments;
using NotchTasks.Services.Devices;
using NotchTasks.Services.Levels;
using NotchTasks.Services.Media;
using NotchTasks.Services.Modules;
using NotchTasks.Services.Notifications;
using NotchTasks.Services.Sync;
using NotchTasks.Services.Updates;

namespace NotchTasks;

/// The app's state. It lives on the UI thread: the watcher and the settings callbacks run on other threads and are
/// marshalled to the dispatcher captured when this object was made.
public sealed class AppState : INotifyPropertyChanged, IDisposable, IAnnouncer
{
    /// The tint of an ask's toast and card (AskStyles AskOrange).
    private const string AskTint = "#FF9F0A";

    private readonly Dispatcher _dispatcher;
    private readonly AskStore _store;
    private readonly AskTracker _tracker = new();
    private readonly ModuleHost _host;
    private readonly SharedClock _clock;
    private readonly BatterySource _batterySource;
    private readonly HealthSource _healthSource;
    private readonly SensorsSource _sensorsSource;
    private readonly ThermalSource _thermalSource;
    private readonly AgentsSource _agentsSource;
    private readonly LimitsSource _limitsSource;
    private readonly CiSource _ciSource;
    private readonly MediaSource _mediaSource;
    private readonly DevicesSource _devicesSource;
    private const string CalendarDismissedKey = "calendarDismissed";  // state.json: the calendar events the user hid
    private bool _shelfTargeted;  // a file is dragged over the pill: the shelf card shows its drop prompt
    private readonly RecentCache<System.Windows.Media.ImageSource> _mediaArt = new(8);  // the artwork of the last eight keys
    private const int WorkerLogTail = 2_000_000;   // the bytes of a GitHub worker's log the module reads
    private AskWatcher? _watcher;
    /// The last list the watcher gave (SyncAsks), and Codex's questions from the agents source (SetCodexAsks).
    private IReadOnlyList<AgentAsk> _watched = Array.Empty<AgentAsk>();
    private IReadOnlyList<AgentAsk> _codexAsks = Array.Empty<AgentAsk>();
    /// The Codex questions answered in this session (the Mac's answeredCodex): they stay gone while the agents module cycles.
    private readonly HashSet<string> _answeredCodex = new(StringComparer.Ordinal);
    private readonly ICodexCli _codex;
    private readonly INotifier _notifier;  // posts the Windows toasts: a toast that notifies goes here when the setting is on
    private readonly Func<ICameraSession> _cameraFactory;  // the mirror's camera (AppStateOptions.Camera; this PC's camera by default)
    private readonly MirrorFrames _mirrorFrames = new();
    private ICameraSession? _camera;  // the running mirror's camera: null while the mirror is closed (written with Volatile.Write)
    private Task _cameraReleased = Task.CompletedTask;  // the last closed camera's release: the next camera starts after it
    private bool _disposed;

    /// Creates the state on the UI thread. Call StartWatching next. The modules that are on start here. options holds the OS
    /// readers (AppStateOptions): null, or the default options, reads this PC; tests pass fakes for each reader.
    public AppState(SettingsStore settings, AskStore store, AppStateOptions? options = null)
    {
        var o = options ?? AppStateOptions.Production;
        Settings = settings;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Asks = new ObservableCollection<AgentAsk>();
        _clock = new SharedClock(Publish);
        Timer = new TimerModule(this);
        Battery = new BatteryModule(this);
        _batterySource = new BatterySource(Battery, Publish, o.Power);
        Network = new NetworkModule(this);
        Calendar = new CalendarModule(this, LoadCalendarDismissed, SaveCalendarDismissed);
        Levels = new LevelsModule();
        Levels.Changed += OnLevelsChanged;
        Downloads = new DownloadsModule(this, o.DownloadsFolder ?? DownloadFolder.Locate() ?? string.Empty);
        Health = new HealthModule(this);
        _healthSource = new HealthSource(Health, PublishLoad, settings, o.Health);
        Helper = o.Helper ?? SensorHelper.Create(settings);
        _thermalSource = new ThermalSource(settings, Helper, PublishThermal);
        Agents = new AgentsModule(this);
        Media = new MediaModule();
        LimitsModule = new LimitsModule(this);
        var (agentReaders, limitReaders) = ResolveReaders(o);
        Processes = agentReaders.Processes;
        _codex = o.Codex ?? new CodexCli(HomeOf(o));
        _cameraFactory = o.Camera ?? (() => new CameraSession());
        _notifier = o.Notifier ?? new SystemNotifier();
        _agentsSource = new AgentsSource(Agents, settings, agentReaders, Publish, SetCodexAsks);
        _limitsSource = new LimitsSource(LimitsModule, settings, limitReaders, _agentsSource, Publish);
        BuildAgents = new AgentsModule(this, build: true);
        Ci = new CiModule(this, CiState.Get, CiState.Set, settings.AddSeenTools,
            path => RunnerLogs.ReadTail(path, WorkerLogTail));
        _ciSource = new CiSource(Ci, BuildAgents, settings, o.Ci ?? CiReaders.Production(Processes, HomeOf(o)), Publish);
        _mediaSource = new MediaSource(Media, o.Media ?? new SmtcReader(), Publish, _mediaArt);
        Devices = new DevicesModule(this, DevicesTint);
        UsageCounters.Start();  // the once-a-minute counts, when NOTCHTASKS_FRAMELOG is set (nothing runs otherwise)
        _devicesSource = new DevicesSource(Devices, o.Devices ?? new ContainerReader(), Publish);
        Shelf = new ShelfStore(
            key => StateJson.Get(AppPaths.StateFile, key),
            (key, value) => StateJson.Set(AppPaths.StateFile, key, value),
            path => File.Exists(path) || Directory.Exists(path));
        Shelf.Changed += OnShelfChanged;
        Sensors = new SensorsModule(this,
            () => ActiveModules(settings).Contains("privacy"),
            () => ActiveModules(settings).Contains("calls"));
        _sensorsSource = new SensorsSource(Sensors, Publish, _clock, o.Sensors);
        _host = new ModuleHost(
            CreateSources(o.Network, o.Calendar ?? new AppointmentCalendarOpener(), o.Outputs ?? (() => new CoreAudioOutputs()), o.Brightness ?? (() => new WmiBrightness())),
            OnTurnedOff,
            (message, e) => ErrorLog.Write(new InvalidOperationException(message, e)));
        Helper.Start();  // no UAC here: the helper is only started by the user's click (SensorHelper)
        _host.Apply(ActiveModules(settings));
        Snapshot = BuildSnapshot();
        settings.Changed += OnSettingsChanged;
        Bridge = o.Sync is null ? null : SyncBridge.Create(this, o.Sync);
        // The update notice is not a sound toast: it is announced without the notification (the Mac's notify: false).
        Updates = new UpdateService(o.Update ?? new UpdateClient(), () => AppPaths.StateFile,
            toast => Announce(toast, notify: false), App.Quit, UpdateService.StartInstaller, _dispatcher);
    }

    public SettingsStore Settings { get; }

    /// The app's self-update: its check, offer and install. The app starts it (Start) once it is up; the Control Panel's footer shows it.
    public UpdateService Updates { get; }

    /// The asks still waiting, oldest first. Changed on the UI thread only.
    public ObservableCollection<AgentAsk> Asks { get; }

    /// The toasts the pill shows, in order. Asks queue here too.
    public ToastQueue Toasts { get; } = new();

    /// The agents module: its states, and the agents that are working (the snapshot's Running). Its source runs while agents is on.
    public AgentsModule Agents { get; }

    /// The Now Playing session the card shows (the snapshot's Media). Its source (MediaSource) runs while media is on.
    public MediaModule Media { get; }

    /// The devices the pill lists and their toasts (the snapshot does not carry them: the devices card reads this). Its source
    /// (DevicesSource) runs while devices is on.
    public DevicesModule Devices { get; }

    /// The AI limits (the snapshot's Limits). Its source runs while Claude, Codex or Antigravity is enabled and limits is on.
    public LimitsModule LimitsModule { get; }

    /// The Build & CI module: its dev, file, runner and GitHub rows (the build row), its failure alerts and its toasts. Its source
    /// (CiSource) runs while ci is on.
    public CiModule Ci { get; }

    /// The build watch items (the enabled items marked Build & CI, and the dev tools): their states, fed by the Build & CI source.
    public AgentsModule BuildAgents { get; }

    /// The file shelf: the files and folders kept from drops on the pill (state.json "shelf"). Its changes publish, so the card follows.
    public ShelfStore Shelf { get; }

    /// True while a file is dragged over the pill (the shelf's drop prompt). Setting it publishes, so the shelf card follows.
    public bool ShelfTargeted
    {
        get => _shelfTargeted;
        set
        {
            if (_shelfTargeted == value)
            {
                return;
            }
            _shelfTargeted = value;
            if (!_disposed)
            {
                Publish();
            }
        }
    }

    /// Raised when the shelf's file dialog is about to open: the pill closes first, so the dialog is not under it.
    public event Action? ShelfPickStarting;

    /// The shelf's "+": closes the pill, then shows the multi-select file dialog once the close has begun, and adds the chosen files.
    public void PickShelfFiles()
    {
        if (_disposed)
        {
            return;
        }
        ShelfPickStarting?.Invoke();
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowShelfPicker));
    }

    private void ShowShelfPicker()
    {
        if (_disposed)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = L10n.L("Add to shelf"),
        };
        if (dialog.ShowDialog() == true)
        {
            ShelfSave.TrySave(() => Shelf.Add(dialog.FileNames));  // a failed save is logged and the shelf stays as it was
        }
    }

    private void OnShelfChanged()
    {
        if (!_disposed)
        {
            Publish();
        }
    }

    /// The process table reader the agent icons use (this PC's, or the options' fake in a test).
    public IProcessReader Processes { get; }

    /// The focus timer. Its controls call Start, Extend or Stop, then Publish.
    public TimerModule Timer { get; }

    /// The battery: its reading is the snapshot's Battery (null with no battery or with the module off).
    public BatteryModule Battery { get; }

    /// The network: it only makes toasts (no snapshot field), so Publish is not needed.
    public NetworkModule Network { get; }

    /// The calendar: its events and hint (the calendar card reads them), the event the chip shows (the snapshot's SoonEvent), and the
    /// meeting toasts. Its source (CalendarSource) runs while calendar is on.
    public CalendarModule Calendar { get; }

    /// The volume and brightness bar (the Mac's level HUD): the bar the notch shows, and its state. Its source (LevelsSource) runs while
    /// levels is on. Nothing is announced: the bar is drawn on the pill only.
    public LevelsModule Levels { get; }

    /// The bar the pill shows now (the Mac's shownLevel), or null when none. It is null while the pill is open.
    public LevelHud? Level => Levels.Hud;

    /// Raised when the level bar shows or hides; the pill follows it.
    public event Action? LevelChanged;

    private void OnLevelsChanged()
    {
        if (!_disposed)
        {
            LevelChanged?.Invoke();
        }
    }

    /// Hides a calendar event from the notch and keeps that across runs, then publishes so the card and the chip follow.
    public void DismissCalendarEvent(string id)
    {
        Calendar.Dismiss(id);
        if (!_disposed)
        {
            Publish();
        }
    }

    /// The events the user hid (state.json "calendarDismissed"). A missing or invalid value is none.
    private static IReadOnlyList<string> LoadCalendarDismissed() =>
        (StateJson.Get(AppPaths.StateFile, CalendarDismissedKey) as JsonArray)?
            .Select(node => node?.GetValue<string>())
            .OfType<string>()
            .ToList() ?? new List<string>();

    /// Keeps the hidden event ids in state.json, so a hidden event stays hidden after a restart (the Mac's dismissedEvents).
    private static void SaveCalendarDismissed(IReadOnlyList<string> ids) =>
        StateJson.Set(AppPaths.StateFile, CalendarDismissedKey, new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id)!).ToArray()));

    /// The downloads: it only makes toasts (no snapshot field), so Publish is not needed.
    public DownloadsModule Downloads { get; }

    /// The health module: its disk warning is a toast, and its load is the snapshot's Load (null when not sampled).
    public HealthModule Health { get; }

    /// The calls and privacy sensors: the snapshot's Call and Sensors, and the call card's state. Its source runs while either
    /// module is on.
    public SensorsModule Sensors { get; }

    /// The temperature helper (Windows only, off by default). Its state drives the Permissions row and the header's reading.
    public SensorHelper Helper { get; }

    /// The hottest temperature the header shows, or null when none is shown (the helper is off, the pill closed, or no reading).
    public ThermalReading? Thermal { get; private set; }

    /// Raised when Thermal changes: the header follows it.
    public event Action? ThermalChanged;

    /// Raised when only the load changed (a meter tick): the header follows it, and the rest of the notch is not rebuilt.
    public event Action? LoadChanged;

    /// The state the closed pill's chips are built from. Rebuilt by Publish.
    public NotchSnapshot Snapshot { get; private set; }

    /// The phone sync's bridge: null when the options pass no sync (the tests that do not sync). It is sent the state at the end of
    /// every Publish, and receives the phone's answers and the notable toasts.
    public SyncBridge? Bridge { get; private set; }

    /// Raised after Snapshot is rebuilt.
    public event Action? NotchChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// The modules that are on, in the user's order. Goes through ModuleCatalog.Order so a fresh install runs the modules the
    /// Modules pane shows on (devices, calls, privacy, mirror), not only the short saved list.
    public static IReadOnlyList<string> ActiveModules(SettingsStore s)
    {
        var disabled = s.Disabled;
        return ModuleCatalog.Order(s.Modules).Where(id => !disabled.Contains(id)).ToList();
    }

    /// Plays the toast's sound (its Event), posts it as a Windows notification when notify is set and the systemNotifications setting
    /// is on (the Mac's announce: the notification is what reaches the user while the notch is collapsed), and queues it on the pill.
    public void Announce(Toast t, bool notify = true)
    {
        Bridge?.Record(t);
        if (t.Event is { } sound)
        {
            Sounds.Play(sound);
        }
        if (notify && Settings.SystemNotifications)
        {
            _notifier.Post(t);
        }
        Toasts.Enqueue(t, DateTimeOffset.Now);
    }

    /// Rebuilds Snapshot from the modules and the asks, and raises NotchChanged.
    public void Publish()
    {
        UsageCounters.Publish();
        Snapshot = BuildSnapshot();
        NotchChanged?.Invoke();
        Bridge?.Update();
    }

    /// Rebuilds only Snapshot (its Load) and raises LoadChanged. The chips, the sections and the toasts are left alone.
    private void PublishLoad()
    {
        if (_disposed)
        {
            return;
        }
        Snapshot = BuildSnapshot();
        LoadChanged?.Invoke();
        Bridge?.Update();  // the phone gets the load too (the input reads Snapshot.Load)
    }

    /// The thermal feed's reading changed (null when none is shown). Only the header reads it.
    private void PublishThermal(ThermalReading? reading)
    {
        if (_disposed)
        {
            return;
        }
        Thermal = reading;
        ThermalChanged?.Invoke();
        Bridge?.Update();  // the phone gets the temperature too (the input reads Thermal)
    }

    /// The power status changed (the AC line or the battery): the battery reads it now, while the battery module is on.
    public void PowerStatusChanged()
    {
        if (!_disposed)
        {
            _batterySource.Refresh();
        }
    }

    /// Starts watching the Asks folder. The current list is reported at once (through the dispatcher).
    public void StartWatching()
    {
        _watcher = new AskWatcher(_store, list => _dispatcher.BeginInvoke(new Action(() => SyncAsks(list))));
    }

    /// Writes the answer for an ask, then removes the ask from Asks at once, and its toast with it. When the reply cannot
    /// be written, the ask stays in the list so the user can try again, and the failure is logged.
    public void Answer(string askId, AskReply reply)
    {
        if (!IsCodexAsk(askId))
        {
            try
            {
                _store.Reply(askId, reply);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(new IOException($"NotchTasks: could not write the reply for ask {askId}", e));
                return;
            }
        }
        if (IsCodexAsk(askId))
        {
            AnswerCodex(askId, reply);
        }
        else
        {
            _tracker.MarkAnswered(askId);
        }
        for (var i = Asks.Count - 1; i >= 0; i--)
        {
            if (Asks[i].Id == askId)
            {
                Asks.RemoveAt(i);
            }
        }
        Toasts.DropAsks(new HashSet<string>(new[] { askId }, StringComparer.Ordinal), DateTimeOffset.Now);
        Publish();
    }

    /// Codex's questions (the agents source's list). They are merged into Asks by SyncAsks, keyed by their ids, so an answered
    /// one leaves. A list with the same ids and times changes nothing.
    private void SetCodexAsks(IReadOnlyList<AgentAsk> list)
    {
        var kept = list.Where(a => !_answeredCodex.Contains(a.Id)).ToList();
        if (_disposed || SameAsks(_codexAsks, kept))
        {
            return;
        }
        _codexAsks = kept;
        SyncAsks(_watched);
    }

    /// Answers a Codex question: its option or reply text goes into the thread (`codex queue`); if that cannot run, or the answer
    /// is "Answer in Codex", the Codex window comes to the front instead.
    private void AnswerCodex(string askId, AskReply reply)
    {
        _answeredCodex.Add(askId);
        var ask = Asks.FirstOrDefault(a => a.Id == askId) ?? _codexAsks.FirstOrDefault(a => a.Id == askId);
        var thread = CodexReply.Thread(askId);
        if (reply.Kind != ReplyKinds.Answers || ask is null || thread is null)
        {
            BringCodexToFront();
            return;
        }
        var text = CodexReply.Message(ask, reply);
        if (text.Length > 0)
        {
            SendCodexReply(thread, text);
        }
    }

    private async void SendCodexReply(string thread, string text)
    {
        try
        {
            var output = await _codex.QueueAsync(thread, text).ConfigureAwait(true);
            if (output is null)
            {
                BringCodexToFront();
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
    }

    private void BringCodexToFront()
    {
        if (!_disposed)
        {
            _codex.BringToFront();
        }
    }

    private static bool SameAsks(IReadOnlyList<AgentAsk> a, IReadOnlyList<AgentAsk> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Id == p.Second.Id && p.First.At == p.Second.At);

    /// A Codex question is answered in the Codex app, not here: it has no reply file. Answering it (its link, or any
    /// reply) only takes it off the pill.
    private static bool IsCodexAsk(string askId) => askId.StartsWith("codex|", StringComparison.Ordinal);

    /// Brings Asks in line with the watcher's list: drops the asks that are gone (and their toasts), and inserts the new
    /// ones at their place in the list, so Asks stays oldest first. Each new ask is announced: its agentAsk sound plays
    /// and its toast queues.
    private void SyncAsks(IReadOnlyList<AgentAsk> list)
    {
        if (_disposed)
        {
            return;
        }
        _watched = list;
        var visible = _tracker.Filter(list.Concat(_codexAsks.Where(a => !_answeredCodex.Contains(a.Id))).OrderBy(a => a.At).ToList());
        var agentsOn = ActiveModules(Settings).Contains("agents");
        if (!agentsOn)
        {
            PassAsks(visible);
            return;
        }
        var wanted = new HashSet<string>(visible.Select(a => a.Id), StringComparer.Ordinal);
        var left = new HashSet<string>(StringComparer.Ordinal);
        for (var i = Asks.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Asks[i].Id))
            {
                left.Add(Asks[i].Id);
                Asks.RemoveAt(i);
            }
        }
        if (left.Count > 0)
        {
            Toasts.DropAsks(left, DateTimeOffset.Now);
        }
        // After the removals Asks is a subsequence of the list, so each new ask goes in at its own index.
        for (var i = 0; i < visible.Count; i++)
        {
            if (i < Asks.Count && Asks[i].Id == visible[i].Id)
            {
                continue;
            }
            Asks.Insert(Math.Min(i, Asks.Count), visible[i]);
            Announce(AskToast(visible[i], Settings.Items));
        }
        Publish();
    }

    /// With agents off, every pending ask is passed back and leaves the pill: the agent asks as usual instead of blocking on an
    /// answer nobody will give (Mac Model.swift refreshAsks). Codex questions have no reply file, so they are only dropped here.
    private void PassAsks(IReadOnlyList<AgentAsk> pending)
    {
        foreach (var ask in pending)
        {
            if (IsCodexAsk(ask.Id))
            {
                _answeredCodex.Add(ask.Id);
                continue;
            }
            try
            {
                _store.Reply(ask.Id, new AskReply(ReplyKinds.Pass, null));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write(new IOException($"NotchTasks: could not write the pass for ask {ask.Id}", e));
                continue;
            }
            _tracker.MarkAnswered(ask.Id);
        }
        Asks.Clear();
        if (pending.Count > 0)
        {
            Toasts.DropAsks(pending.Select(a => a.Id).ToHashSet(StringComparer.Ordinal), DateTimeOffset.Now);
        }
        Publish();
    }

    /// The toast a new ask shows: a hand for an approval, a question mark for questions. Its texts come from
    /// AskPresentation, the same as the card's.
    private static Toast AskToast(AgentAsk ask, IReadOnlyList<WatchItem> items) => new(
        ask.IsQuestion ? "question" : "hand",
        AskTint,
        AskPresentation.Title(ask),
        AskPresentation.Subtitle(ask),
        Event: "agentAsk",
        Sticky: true,
        AskId: ask.Id,
        Module: "agents",
        Icon: AgentItems.ItemOf(ask, items));

    /// The agents and limits readers: the options' fakes, else this PC's (the process table, sqlite3, the loopback HTTP, the
    /// LM Studio runner and the claude CLI). The agent folders are under Home; the Claude folder is CLAUDE_CONFIG_DIR when it is
    /// set (production only; a test's Home is used as it is).
    private static string HomeOf(AppStateOptions o) => o.Home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static (AgentReaders Agents, LimitReaders Limits) ResolveReaders(AppStateOptions o)
    {
        var home = HomeOf(o);
        var claudeDir = ClaudeSessions.Dir(home, o.Home is null ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") : null);
        IProcessReader processes = o.Processes ?? new ProcessReader();
        ISqliteReader sqlite = o.Sqlite ?? new SqliteCli(home);
        IHttpPost http = o.Http ?? HttpLoopback.Shared;
        IHttpGet httpGet = o.HttpGet ?? HttpLoopback.Shared;
        IClaudeCli cli = o.ClaudeCli ?? new ClaudeCli(home);
        Func<string, string?> lms = o.LmsPsJson ?? (exe => LmsRunner.PsJson(exe));
        return (
            new AgentReaders(processes, sqlite, httpGet, lms, home, claudeDir),
            new LimitReaders(http, cli, home, claudeDir));
    }

    /// The module sources by module id, built once before the first Apply. Modules register their sources here: a module
    /// with no entry is active but runs nothing. Timer is made before this runs.
    private IReadOnlyDictionary<string, IModuleSource> CreateSources(
        NetworkReaders? network,
        ICalendarOpener calendar,
        Func<IOutputDevices> outputs,
        Func<IBrightness> brightness) =>
        new Dictionary<string, IModuleSource>(StringComparer.Ordinal)
        {
            ["timer"] = new TimerSource(Timer, _clock),
            ["battery"] = _batterySource,
            ["network"] = new NetworkSource(Network, network),
            // the store is opened while the module is on: no access, or no store, shows the card's hint and nothing runs
            ["calendar"] = new CalendarSource(Calendar, calendar, Publish, _clock),
            // the bar's feed: the default output and the panel, by events; a PC with no panel shows volume only
            ["levels"] = new LevelsSource(Levels, outputs, brightness),
            ["downloads"] = new DownloadsSource(Downloads),
            // one source for both: the temperature feed runs only while the health module runs and the pill is open
            ["health"] = new CompositeSource(_healthSource, _thermalSource),
            // one source for both: it counts its starts, so it runs while either module is on
            ["privacy"] = _sensorsSource,
            ["calls"] = _sensorsSource,
            ["agents"] = _agentsSource,
            ["limits"] = _limitsSource,
            ["ci"] = _ciSource,
            ["media"] = _mediaSource,
            ["devices"] = _devicesSource,
        };

    /// The snapshot's ask count: the asks belong to the agents module, so none count while it is off.
    private NotchSnapshot BuildSnapshot()
    {
        var modules = ActiveModules(Settings);
        return new NotchSnapshot(
        modules,
        modules.Contains("agents") ? Asks.Count : 0,
        this.Timer.State,
        Sensors.Call,
        Sensors.Sensors,
        Battery.State,
        Health.Load)
        {
            Running = modules.Contains("agents") ? Agents.Running : Array.Empty<ItemState>(),
            JobCount = modules.Contains("agents") ? Agents.States.Sum(item => item.Jobs.Count) : 0,
            BuildRunning = modules.Contains("ci") ? BuildRunningRows() : Array.Empty<ItemState>(),
            Alerts = modules.Contains("ci") ? Ci.Alerts : Array.Empty<JobAlert>(),
            Limits = modules.Contains("limits") ? LimitsModule.Limits : Array.Empty<ProviderLimits>(),
            LimitWarn = Settings.LimitWarn,
            Media = modules.Contains("media") ? this.Media.Current : null,
            // the calendar's chip event, picked now: a timed event soon or running (none unless calendar is on)
            SoonEvent = modules.Contains("calendar") ? this.Calendar.SoonEvent(DateTimeOffset.Now) : null,
            // the first ask's agent, whose icon leads the ask chip (Mac: asks.first)
            AskItem = modules.Contains("agents") && Asks.Count > 0 ? AgentItems.ItemOf(Asks[0], Settings.Items) : null,
        };
    }

    /// The Build & CI rows that are working: the build row's working jobs first, then the build watch items' working rows.
    private IReadOnlyList<ItemState> BuildRunningRows()
    {
        var rows = new List<ItemState>();
        if (Ci.BuildState is { } build)
        {
            var working = build.Jobs.Where(j => j.Working).ToList();
            if (working.Count > 0)
            {
                rows.Add(new ItemState(build.Item, working));
            }
        }
        rows.AddRange(BuildAgents.Running);
        return rows;
    }

    /// The devices module's tint: the devices colour when it is set (null: the module's default blue).
    private string? DevicesTint() =>
        Settings.ModuleColors.TryGetValue("devices", out var hex) && !string.IsNullOrWhiteSpace(hex) ? hex : null;

    /// The artwork image of a media key, or null when it is not cached (the last eight keys are).
    public System.Windows.Media.ImageSource? MediaArt(string key) => _mediaArt.Get(key);

    /// A media key from the card or the chip. A toggle flips the card's play state at once (the Mac's optimistic toggle); the
    /// command then goes to the session, and SMTC's next event reads the real state.
    public void MediaControl(MediaCommand command)
    {
        if (_disposed)
        {
            return;
        }
        if (command == MediaCommand.Toggle && Media.Current is { } shown)
        {
            Media.SetPlaying(!shown.Playing, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Publish();
        }
        _ = _mediaSource.Send(command);
    }

    /// The seek bar's release: the shown session moves to the position, in seconds.
    public void MediaSeek(double seconds)
    {
        if (!_disposed)
        {
            _ = _mediaSource.Seek(seconds);
        }
    }

    /// The call card's mute button: sets the default mic's mute, and publishes the new reading.
    public void ToggleCallMute()
    {
        if (!_disposed)
        {
            _sensorsSource.ToggleMute();
        }
    }

    /// True while the camera mirror card is on in the open pill (the quick bar's Mirror button sets it).
    public bool MirrorOn { get; private set; }

    /// How the mirror's camera started: null while it starts (or the mirror is closed), then its result.
    public CameraStart? MirrorStatus { get; private set; }

    /// A camera frame for the mirror card, on the UI thread. The pixels are valid only during the handlers.
    public event Action<MirrorPixels>? MirrorFrameReady;

    /// Opens the mirror and starts its camera. Only while the mirror module is on, and the mirror is not open already.
    public void OpenMirror()
    {
        if (_disposed || MirrorOn || !ActiveModules(Settings).Contains("mirror"))
        {
            return;
        }
        MirrorOn = true;
        MirrorStatus = null;
        var camera = _cameraFactory();
        // A camera closed a moment ago may still be releasing: this one starts only after that release, so our own old
        // session never holds the camera and forces the shared fallback.
        var previous = _cameraReleased;
        Volatile.Write(ref _camera, camera);
        _ = StartMirrorAsync(camera, previous);
        Publish();
    }

    /// Closes the mirror: its camera stops and is released. Nothing happens when it is closed already.
    public void CloseMirror()
    {
        if (_disposed || !MirrorOn)
        {
            return;
        }
        StopMirror();
        Publish();
    }

    /// Starts the camera and keeps its result, unless the mirror was closed (or the camera replaced) while it started.
    private async Task StartMirrorAsync(ICameraSession camera, Task previous)
    {
        CameraStart result;
        try
        {
            if (!previous.IsCompleted)
            {
                try
                {
                    await previous;
                }
                catch (Exception)
                {
                    // the old camera's release logs its own errors; this camera starts anyway
                }
            }
            if (!ReferenceEquals(Volatile.Read(ref _camera), camera))
            {
                return;  // closed while the old camera was releasing: this one is stopped and never started
            }
            result = await camera.Start((width, height, bgra) => OnMirrorFrame(camera, width, height, bgra));
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
            result = CameraStart.Failed;
        }
        if (ReferenceEquals(Volatile.Read(ref _camera), camera) && MirrorOn)
        {
            MirrorStatus = result;
            Publish();
        }
    }

    /// Stops the mirror without publishing: the mirror is off, its frames are dropped, and its camera is stopped and released.
    private void StopMirror()
    {
        MirrorOn = false;
        MirrorStatus = null;
        _mirrorFrames.Clear();
        var camera = Volatile.Read(ref _camera);
        Volatile.Write(ref _camera, null);
        if (camera is not null)
        {
            _cameraReleased = StopCameraAsync(camera);
        }
    }

    private static async Task StopCameraAsync(ICameraSession camera)
    {
        try
        {
            await camera.Stop();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            camera.Dispose();
        }
    }

    /// A camera frame, on the camera's thread: kept for the UI thread when it is still the mirror's camera. The UI is asked to
    /// deliver it only when no delivery waits already.
    private void OnMirrorFrame(ICameraSession camera, int width, int height, byte[] bgra)
    {
        if (!ReferenceEquals(Volatile.Read(ref _camera), camera))
        {
            return;
        }
        if (_mirrorFrames.Offer(width, height, bgra))
        {
            _dispatcher.BeginInvoke(new Action(DeliverMirrorFrame));
        }
    }

    /// Hands the waiting frame to the mirror card, then returns its buffer.
    private void DeliverMirrorFrame()
    {
        if (_mirrorFrames.Take() is not { } frame)
        {
            return;
        }
        try
        {
            MirrorFrameReady?.Invoke(frame);
        }
        finally
        {
            _mirrorFrames.Return(frame);
        }
    }

    /// The open pill starts or stops the load sampling: the CPU and RAM are read only while the pill is open (and SystemLoad is on).
    /// The mirror closes with the pill: no camera runs while the pill is closed.
    public void PillOpened(bool open)
    {
        if (!_disposed)
        {
            _healthSource.PillOpened(open);
            _thermalSource.PillOpened(open);
            _mediaSource.FollowTimeline(open);  // the seek bar moves only while the pill is open
            if (!open && MirrorOn)
            {
                CloseMirror();
            }
        }
    }

    /// Starts and stops the module sources when the modules or the disabled list change.
    private void OnSettingsChanged(IReadOnlyCollection<string> keys) =>
        _dispatcher.BeginInvoke(new Action(() =>
        {
            OnPropertyChanged(nameof(Settings));
            if (keys.Any(k => k is "modules" or "disabled"))
            {
                ApplyModules();
            }
            if (!_disposed && keys.Any(k => k == "systemLoad"))
            {
                _healthSource.SystemLoadChanged();
            }
            if (!_disposed && keys.Any(k => k == "items"))
            {
                _agentsSource.ItemsChanged();
                _limitsSource.ItemsChanged();
            }
            if (!_disposed && keys.Any(k => k is "systemLoad" or "sensorHelper" or "sensorHelperAtSignIn"))
            {
                _thermalSource.SettingsChanged();
            }
        }));

    private void ApplyModules()
    {
        if (_disposed)
        {
            return;
        }
        var active = ActiveModules(Settings);
        _host.Apply(active);
        if (!active.Contains("agents"))
        {
            SyncAsks(_watched);  // agents off: the asks already pending are passed back now, not at the next file change
        }
        Publish();
    }

    /// A module was turned off: its toasts go. The calls module turned off during a call forgets the call at once, with no
    /// toast (privacy keeps the sensors, if it is on).
    private void OnTurnedOff(string id)
    {
        Toasts.DropModule(id, DateTimeOffset.Now);
        if (id == "calls")
        {
            _sensorsSource.ForgetCall();
        }
        if (id == "mirror" && MirrorOn)
        {
            StopMirror();  // the mirror's camera goes off with its module
        }
        Publish();
    }

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        Settings.Changed -= OnSettingsChanged;
        Updates.Dispose();  // the update timers stop
        Bridge?.Close();  // the hub stops: its stream, its tick and its listener
        UsageCounters.Stop();
        _watcher?.Dispose();
        StopMirror();  // the camera is released, and no frame is delivered after this
        _host.StopAll();
        _thermalSource.Dispose();  // unsubscribes from the helper's StateChanged
    }
}
