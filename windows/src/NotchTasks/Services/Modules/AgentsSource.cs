using System.IO;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;

namespace NotchTasks.Services.Modules;

/// The OS readers the agents and limits sources run on (AppStateOptions): the process table, sqlite3, a loopback GET (Ollama),
/// the LM Studio runner, the user's profile folder (Home) and the Claude config folder (ClaudeDir).
public sealed record AgentReaders(
    IProcessReader Processes,
    ISqliteReader Sqlite,
    IHttpGet HttpGet,
    Func<string, string?> LmsPsJson,
    string Home,
    string ClaudeDir);

/// The agents module's feed (module "agents"). While it is started and an enabled non-build item exists, a 2 s timer reads the
/// process table and the agent folders, off the UI thread, one read at a time, and hands the result to AgentsModule.Update on the
/// UI thread. Local models are read every 30 s, and only when an LM Studio or Ollama item is enabled. The Codex questions go to
/// the app as asks. Stop stops the timer and clears the module. With no enabled item the timer does not run.
public sealed class AgentsSource : IModuleSource
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LocalEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CodexLimitsEvery = TimeSpan.FromSeconds(10);
    private const string LmStudioBundleId = "ai.elementlabs.lmstudio";
    private const string OllamaProcess = "ollama";

    private readonly AgentsModule _module;
    private readonly SettingsStore _settings;
    private readonly AgentReaders _reads;
    private readonly Action _publish;
    private readonly Action<IReadOnlyList<AgentAsk>> _codexAsks;
    private readonly CpuMeter _cpu = new();
    private readonly CodexScanner _codex;
    private readonly AntigravityScanner _antigravity;
    private readonly DispatcherTimer _timer = new() { Interval = TickEvery };
    private bool _started;
    private bool _busy;
    private DateTimeOffset _nextLocal = DateTimeOffset.MinValue;
    private DateTimeOffset _nextCodexLimits = DateTimeOffset.MinValue;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _local = new Dictionary<string, IReadOnlyList<string>>();
    private ProviderLimits? _codexLimits;

    /// module gets the states; publish is called when they change; codexAsks gets Codex's waiting questions after each read.
    public AgentsSource(
        AgentsModule module,
        SettingsStore settings,
        AgentReaders reads,
        Action publish,
        Action<IReadOnlyList<AgentAsk>> codexAsks)
    {
        _module = module;
        _settings = settings;
        _reads = reads;
        _publish = publish;
        _codexAsks = codexAsks;
        _codex = new CodexScanner(Path.Combine(reads.Home, ".codex", "sessions"));
        _antigravity = new AntigravityScanner(Path.Combine(reads.Home, ".gemini", "antigravity", "conversations"), reads.Sqlite);
        _timer.Tick += (_, _) => Tick();
    }

    /// The Codex limits this source last read. Valid while SharesCodexLimits is true (the limits module uses them then).
    public ProviderLimits? CodexLimits => _codexLimits;

    /// True while this source runs and a Codex item is enabled: the limits module then takes CodexLimits instead of reading Codex itself.
    public bool SharesCodexLimits => _started && _settings.Items.Any(i => i.Enabled && i.Kind == WatchKind.Codex);

    public void Start()
    {
        _started = true;
        Sync();
    }

    /// Stops the timer and clears the module and the Codex questions. A read still in flight is dropped when it lands.
    public void Stop()
    {
        _started = false;
        _timer.Stop();
        Clear();
    }

    /// The watch list changed: the timer follows the enabled items, and the states are cleared when none is left.
    public void ItemsChanged()
    {
        if (_started)
        {
            Sync();
        }
    }

    private void Sync()
    {
        var want = _started && _settings.Items.Any(i => i.Enabled && !WatchItems.InBuild(i));
        if (want)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
            return;
        }
        if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
        if (_started && Clear())
        {
            _publish();
        }
    }

    /// Empties the module and the Codex questions. Returns true when the module's states changed.
    private bool Clear()
    {
        _codexLimits = null;
        _codexAsks(Array.Empty<AgentAsk>());
        var empty = new AgentInputs(
            Array.Empty<WatchItem>(),
            false,
            new ProcSnapshot(Array.Empty<ProcInfo>()),
            _cpu,
            _ => Array.Empty<string>(),
            Array.Empty<Job>(),
            Array.Empty<Job>(),
            Array.Empty<Job>(),
            new Dictionary<string, IReadOnlyList<string>>(),
            0);
        return _module.Update(empty, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    /// One timer tick: the reads it needs start in the background; the result is applied here, on the UI thread.
    private async void Tick()
    {
        if (_busy || !_started)
        {
            return;
        }
        _busy = true;
        try
        {
            var request = MakeRequest(DateTimeOffset.UtcNow);
            var reading = await Task.Run(() => Gather(request)).ConfigureAwait(true);
            if (_started)
            {
                Apply(request, reading);
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _busy = false;
        }
    }

    /// What one tick reads, decided on the UI thread: which items are on, and which slow reads are due.
    private sealed record Request(
        IReadOnlyList<WatchItem> Items,
        double DoneAfter,
        DateTimeOffset Now,
        bool ClaudeOn,
        bool CodexOn,
        bool AgyOn,
        bool ArgvNeeded,
        bool ReadLocal,
        bool ReadCodexLimits,
        ProviderLimits? PreviousCodexLimits);

    private Request MakeRequest(DateTimeOffset now)
    {
        var items = _settings.Items;
        var on = items.Where(i => i.Enabled && !WatchItems.InBuild(i)).ToList();
        var localOn = on.Any(i => LocalKey(i) is not null);
        return new Request(
            items,
            _settings.DoneAfter,
            now,
            ClaudeOn: on.Any(i => i.Kind == WatchKind.Claude),
            CodexOn: on.Any(i => i.Kind == WatchKind.Codex),
            AgyOn: on.Any(i => i.Kind == WatchKind.Process && i.Cli == "agy"),
            ArgvNeeded: on.Any(i => i.Kind is WatchKind.Process or WatchKind.App),
            ReadLocal: localOn && now >= _nextLocal,
            ReadCodexLimits: on.Any(i => i.Kind == WatchKind.Codex) && now >= _nextCodexLimits,
            PreviousCodexLimits: _codexLimits);
    }

    /// The reads of one tick, made on a background thread.
    private sealed record Reading(
        ProcSnapshot Snapshot,
        Func<ProcInfo, IReadOnlyList<string>> Argv,
        IReadOnlyList<Job> Claude,
        IReadOnlyList<Job> Codex,
        IReadOnlyList<AgentAsk> Questions,
        ProviderLimits? CodexLimits,
        IReadOnlyList<Job> Antigravity,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Local);

    private Reading Gather(Request r)
    {
        var snapshot = _reads.Processes.Snapshot();
        var nowMs = r.Now.ToUnixTimeMilliseconds();
        var claude = r.ClaudeOn
            ? ClaudeSessions.Jobs(ClaudeSessions.Read(_reads.ClaudeDir), snapshot, nowMs)
            : Array.Empty<Job>();

        IReadOnlyList<Job> codexJobs = Array.Empty<Job>();
        IReadOnlyList<AgentAsk> questions = Array.Empty<AgentAsk>();
        var codexLimits = r.PreviousCodexLimits;
        if (r.CodexOn)
        {
            // Jobs first: the questions are read from what the last scan saw
            codexJobs = _codex.Jobs(r.Now);
            questions = _codex.Questions(r.Now);
            if (r.ReadCodexLimits)
            {
                codexLimits = _codex.Limits(r.Now);
            }
        }

        var antigravity = r.AgyOn ? _antigravity.Jobs(r.Now) : Array.Empty<Job>();
        var local = r.ReadLocal ? ReadLocal() : null;

        return new Reading(
            snapshot,
            r.ArgvNeeded ? PrefetchArgv(snapshot) : (_ => Array.Empty<string>()),
            claude,
            codexJobs,
            questions,
            codexLimits,
            antigravity,
            local);
    }

    /// The command-line words of every process, read now (the reader caches them), so the module's lookups need no OS calls.
    private Func<ProcInfo, IReadOnlyList<string>> PrefetchArgv(ProcSnapshot snapshot)
    {
        var words = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var p in snapshot.All)
        {
            words[p.Pid] = ProcTable.SplitArgs(_reads.Processes.CommandLine(p));
        }
        return p => words.TryGetValue(p.Pid, out var list) ? list : Array.Empty<string>();
    }

    /// LM Studio (its lms CLI, through LocalModels) and Ollama (its loopback API) are asked for what they have loaded.
    private IReadOnlyDictionary<string, IReadOnlyList<string>> ReadLocal()
    {
        var found = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (_settings.Items.Any(i => i.Enabled && LocalKey(i) == "lmstudio"))
        {
            found["lmstudio"] = LocalModels.LmStudioLoaded(_reads.Home, _reads.LmsPsJson);
        }
        if (_settings.Items.Any(i => i.Enabled && LocalKey(i) == "ollama"))
        {
            // on a background thread: blocking here is fine, and Ollama's own timeout is 1.5 s
            found["ollama"] = LocalModels.OllamaLoadedAsync(_reads.HttpGet).GetAwaiter().GetResult();
        }
        return found;
    }

    /// Hands a reading to the module, on the UI thread. Publishes when the states changed, and passes the Codex questions on.
    private void Apply(Request r, Reading g)
    {
        if (r.ReadLocal)
        {
            _local = g.Local ?? _local;
            _nextLocal = r.Now + LocalEvery;
        }
        if (r.ReadCodexLimits)
        {
            _nextCodexLimits = r.Now + CodexLimitsEvery;
        }
        _codexLimits = r.CodexOn ? g.CodexLimits : null;
        _codexAsks(g.Questions);

        var changed = _module.Update(
            new AgentInputs(
                r.Items,
                true,
                g.Snapshot,
                _cpu,
                g.Argv,
                g.Claude,
                g.Codex,
                g.Antigravity,
                _local,
                r.DoneAfter),
            r.Now.ToUnixTimeMilliseconds());
        if (changed)
        {
            _publish();
        }
    }

    /// The local model key of a runner item (the Mac's localKey), or null for other items.
    private static string? LocalKey(WatchItem item) =>
        item.BundleID == LmStudioBundleId ? "lmstudio" : item.Process == OllamaProcess ? "ollama" : null;
}
