using System.IO;
using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Settings;

namespace NotchTasks.Services.Modules;

/// The OS readers of the limits source (AppStateOptions): the loopback POST (Antigravity's language server), the claude CLI, the
/// user's profile folder (Home) and the Claude config folder (ClaudeDir).
public sealed record LimitReaders(IHttpPost Http, IClaudeCli Cli, string Home, string ClaudeDir);

/// The limits module's feed (module "limits"). While it is started and Claude, Codex or Antigravity is enabled, a 10 s timer
/// runs:
/// - the claude CLI (`/usage`) runs at start and then every 30 min, one call at a time, only while the Claude
///   opt-in (claudeLimits) is on; it is given up after 30 s, and the statusline snapshot stands in until it answers;
/// - the Claude statusline is read every tick, in the background;
/// - Codex limits come from AgentsSource's scanner while the agents module runs with a Codex item, else from this source's own;
/// - Antigravity's quota is fetched every 60 s, in the background.
/// Stop stops the timer and clears the module.
public sealed class LimitsSource : IModuleSource
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CliEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CliGiveUpAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AntigravityEvery = TimeSpan.FromSeconds(60);

    private readonly LimitsModule _module;
    private readonly SettingsStore _settings;
    private readonly LimitReaders _reads;
    private readonly AgentsSource _agents;
    private readonly Action _publish;
    private readonly TimeSpan _cliGiveUp;
    private readonly CodexScanner _codex;
    private readonly AntigravityLimits _antigravity;
    private readonly ClaudeLimitsSetupRunner? _setup;
    private DateTimeOffset _nextRecheck = DateTimeOffset.MinValue;
    private bool _rechecking;
    private readonly DispatcherTimer _timer = new() { Interval = TickEvery };
    private bool _started;
    private bool _reading;
    private bool _cliBusy;
    private bool _antigravityBusy;
    private int _generation;  // every Start and Stop: a result of an older one is dropped when it lands
    private DateTimeOffset _nextCli = DateTimeOffset.MinValue;
    private DateTimeOffset _nextAntigravity = DateTimeOffset.MinValue;
    private ProviderLimits? _statusline;
    private IReadOnlyList<LimitWindow>? _live;
    private DateTimeOffset _liveAt;
    private ProviderLimits? _ownCodex;
    private ProviderLimits? _antigravityLimits;

    /// agents is the agents source whose Codex scanner this shares. cliGiveUp is how long the CLI may take before the statusline
    /// stands in (30 s; a test passes less). setup gives the status each CLI reply lands in, and the one-minute re-check while the
    /// sign-in or the CLI is not known to be good; when null there is neither.
    public LimitsSource(
        LimitsModule module,
        SettingsStore settings,
        LimitReaders reads,
        AgentsSource agents,
        Action publish,
        TimeSpan? cliGiveUp = null,
        ClaudeLimitsSetupRunner? setup = null)
    {
        _setup = setup;
        _module = module;
        _settings = settings;
        _reads = reads;
        _agents = agents;
        _publish = publish;
        _cliGiveUp = cliGiveUp ?? CliGiveUpAfter;
        _codex = new CodexScanner(Path.Combine(reads.Home, ".codex", "sessions"));
        _antigravity = new AntigravityLimits(PidAlive.Alive);
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        _generation++;
        _started = true;
        // the CLI runs once at start, and then again when its 30 min are up
        _nextCli = DateTimeOffset.MinValue;
        _nextAntigravity = DateTimeOffset.MinValue;
        _nextRecheck = DateTimeOffset.MinValue;
        Sync();
        Tick();
    }

    /// Stops the timer and clears the limits. A read still in flight is dropped when it lands.
    public void Stop()
    {
        _generation++;
        _started = false;
        _timer.Stop();
        _statusline = null;
        _live = null;
        _ownCodex = null;
        _antigravityLimits = null;
        _module.Update(new LimitInputs(false, false, false, false, null, null, null, _settings.LimitWarn), DateTimeOffset.UtcNow);
    }

    /// The Claude opt-in changed. On: the CLI runs now, not at its next 30 min. Off: the CLI's reading is forgotten, so the
    /// statusline (or nothing) stands in, and a call still in flight is dropped when it lands.
    public void ClaudeLimitsChanged()
    {
        if (!_started)
        {
            return;
        }
        if (_settings.ClaudeLimits)
        {
            _nextCli = DateTimeOffset.MinValue;
            _nextRecheck = DateTimeOffset.MinValue;
        }
        else
        {
            _live = null;
        }
        Tick();
    }

    /// The watch list changed: the timer follows the enabled providers, and the limits are recomposed.
    public void ItemsChanged()
    {
        if (!_started)
        {
            return;
        }
        Sync();
        Compose(DateTimeOffset.UtcNow);
    }

    private void Sync()
    {
        var want = _started && AnyProviderOn(_settings.Items);
        if (want && !_timer.IsEnabled)
        {
            _timer.Start();
        }
        else if (!want && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private static bool AnyProviderOn(IReadOnlyList<WatchItem> items) =>
        items.Any(i => i.Enabled && i.Kind is WatchKind.Claude or WatchKind.Codex) || AgyOn(items);

    /// One tick (UI thread): starts what is due, and recomposes the limits from what is known.
    private void Tick()
    {
        if (!_started)
        {
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var items = _settings.Items;
        var claudeOn = items.Any(i => i.Enabled && i.Kind == WatchKind.Claude);
        var codexOn = items.Any(i => i.Enabled && i.Kind == WatchKind.Codex);
        var agyOn = AgyOn(items);

        if (claudeOn && _settings.ClaudeLimits && !_cliBusy && now >= _nextCli)
        {
            _nextCli = now + CliEvery;
            RunCli(now);
        }
        if (claudeOn && _settings.ClaudeLimits && _setup is { } setup)
        {
            RecheckIfDue(setup, now);
        }
        if (agyOn && !_antigravityBusy && now >= _nextAntigravity)
        {
            _nextAntigravity = now + AntigravityEvery;
            FetchAntigravity(now);
        }
        var ownCodex = codexOn && !_agents.SharesCodexLimits;
        if (!_reading && (claudeOn || ownCodex))
        {
            ReadInBackground(claudeOn, ownCodex, now);
        }
        Compose(now);
    }

    /// The fast re-check (ClaudeLimitsSetup.RecheckEvery): every minute while the sign-in or the CLI is not known to be good, in
    /// the background, and never a window. It stops once both are known good; the 30 min reading then carries on.
    private void RecheckIfDue(ClaudeLimitsSetupRunner setup, DateTimeOffset now)
    {
        if (_rechecking || now < _nextRecheck)
        {
            return;
        }
        if (ClaudeLimitsSetup.RecheckEvery(setup.Status.SignedIn, setup.Status.CliFound) is not { } wait)
        {
            return;
        }
        _nextRecheck = now + wait;
        RecheckInBackground(setup);
    }

    private async void RecheckInBackground(ClaudeLimitsSetupRunner setup)
    {
        _rechecking = true;
        try
        {
            await setup.RecheckAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _rechecking = false;
        }
    }

    private static bool AgyOn(IReadOnlyList<WatchItem> items) => items.Any(IsAgy);

    /// The Antigravity item: an enabled process item whose cli is agy.
    private static bool IsAgy(WatchItem i) => i.Enabled && i.Kind == WatchKind.Process && i.Cli == "agy";

    /// The statusline (Claude) and this source's own Codex limits, read on a background thread.
    private async void ReadInBackground(bool claude, bool ownCodex, DateTimeOffset now)
    {
        _reading = true;
        var generation = _generation;
        try
        {
            var reading = await Task.Run(() => new LimitsReading(
                claude ? ClaudeUsage.FromStatusline(_reads.ClaudeDir, now) : null,
                ownCodex ? _codex.Limits(now) : null)).ConfigureAwait(true);
            if (_started && generation == _generation)
            {
                _statusline = claude ? reading.Statusline : null;
                _ownCodex = ownCodex ? reading.Codex : null;
                Compose(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _reading = false;
        }
    }

    private sealed record LimitsReading(ProviderLimits? Statusline, ProviderLimits? Codex);

    /// Runs the Claude CLI. It is given up after the give-up time: the statusline stands in, and the call is still awaited, so
    /// no second call starts while the first is running.
    private async void RunCli(DateTimeOffset now)
    {
        _cliBusy = true;
        var generation = _generation;
        try
        {
            using var cts = new CancellationTokenSource();
            var call = AskCli(cts.Token);
            if (await Task.WhenAny(call, Task.Delay(_cliGiveUp)).ConfigureAwait(true) != call && _started && generation == _generation)
            {
                cts.Cancel();
                Compose(DateTimeOffset.UtcNow);
            }
            var text = await call.ConfigureAwait(true);
            if (text is not null && _started && generation == _generation && _settings.ClaudeLimits)
            {
                _setup?.Status.Record(text, DateTimeOffset.UtcNow);
                var windows = ClaudeUsage.Parse(text, DateTimeOffset.UtcNow);
                if (windows.Count > 0)
                {
                    _live = windows;
                    _liveAt = DateTimeOffset.UtcNow;
                }
            }
            if (_started && generation == _generation)
            {
                Compose(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _cliBusy = false;
        }
    }

    private async Task<string?> AskCli(CancellationToken ct)
    {
        try
        {
            return await _reads.Cli.UsageAsync(ct).ConfigureAwait(true);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Antigravity's quota from its running language server; null (gone from the pill) when it is not running.
    private async void FetchAntigravity(DateTimeOffset now)
    {
        _antigravityBusy = true;
        var generation = _generation;
        try
        {
            var found = await Task.Run(() => _antigravity.FetchAsync(_reads.Home, _reads.Http, now, CancellationToken.None))
                .ConfigureAwait(true);
            if (_started && generation == _generation)
            {
                _antigravityLimits = found;
                Compose(DateTimeOffset.UtcNow);
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            _antigravityBusy = false;
        }
    }

    /// The limits the module shows, from what is known now. Publishes when they changed.
    private void Compose(DateTimeOffset now)
    {
        var items = _settings.Items;
        var claudeOn = items.Any(i => i.Enabled && i.Kind == WatchKind.Claude);
        var codexOn = items.Any(i => i.Enabled && i.Kind == WatchKind.Codex);
        var agyOn = AgyOn(items);

        var liveAt = _live is null ? (DateTimeOffset?)null : _liveAt;
        var claude = claudeOn ? ClaudeUsage.Now(_live, liveAt, _statusline, now) : null;
        ProviderLimits? codex = null;
        if (codexOn)
        {
            codex = _agents.SharesCodexLimits ? _agents.CodexLimits : _ownCodex;
        }
        var changed = _module.Update(
            new LimitInputs(true, claudeOn, codexOn, agyOn, claude, codex, agyOn ? _antigravityLimits : null, _settings.LimitWarn),
            now);
        if (changed)
        {
            _publish();
        }
    }
}
