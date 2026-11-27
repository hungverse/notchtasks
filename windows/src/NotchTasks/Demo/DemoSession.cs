using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Demo;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services.Notifications;

namespace NotchTasks.Demo;

/// The demo run's state and its timeline (`NotchTasks.exe --demo`). The content is made up, and it goes into the same state objects
/// the real sources feed (the agents, the build row, the asks and the toasts), so the pill's own views draw it. Tick applies the frame
/// of the timeline at a time: the ask, the Allow hold, the toast and the build's end. The pill never opens its card stack. Nothing reaches
/// the OS: the state has no source, no asks folder write and no settings file (see NewState).
public sealed class DemoSession
{
    private readonly AppState _state;
    private readonly Action<bool> _pressAllow;
    private DemoFrame _frame = new(AskPending: false, AllowPressed: false, Toast: DemoToast.None, BuildDone: false);
    private WatchItem _claude = null!;
    private WatchItem _codex = null!;
    private AgentAsk _ask = null!;
    private Toast _askToast = null!;
    private Toast _pushedToast = null!;
    private Toast _chargeToast = null!;

    /// The demo over a state (NewState). pressAllow holds the ask toast's Allow down or lets it go.
    public DemoSession(AppState state, Action<bool> pressAllow)
    {
        _state = state;
        _pressAllow = pressAllow;
    }

    /// The app state the demo runs on: the settings in memory (the defaults), no source, and no write to the asks folder or the
    /// settings file. The options are the demo's (AppStateOptions.Demo); a test passes readers of its own to see that none is read.
    public static AppState NewState(AppStateOptions? options = null)
    {
        var o = (options ?? new AppStateOptions()) with
        {
            Demo = true,
            Notifier = options?.Notifier ?? new NullNotifier(),
        };
        return new AppState(SettingsStore.InMemory(), new AskStore(AppPaths.AsksDir), o);
    }

    /// The state the demo starts with: the closed pill, the agents running and the build running. Call once, before the first Tick.
    public void Start(DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        _claude = WatchItemOf(WatchKind.Claude, "claude", "Claude", "sparkles");
        _codex = WatchItemOf(WatchKind.Codex, "codex", "Codex", "terminal.fill");
        _ask = new AgentAsk(
            Id: "demo-ask-push",
            Pid: 0,
            Session: DemoContent.AskSession,
            Tool: "Bash",
            Summary: DemoContent.AskSummary,
            Questions: Array.Empty<AskQuestion>(),
            At: now,
            Agent: "claude");
        _askToast = new Toast(
            "hand",
            "#FF9F0A",
            AskPresentation.Title(_ask),
            AskPresentation.Subtitle(_ask),
            Event: "agentAsk",
            Sticky: true,
            AskId: _ask.Id,
            Module: "agents",
            Icon: _claude);
        _pushedToast = new Toast("check", "#30D158", DemoContent.PushedTitle, Event: "taskDone", Module: "agents", Icon: _claude);
        _chargeToast = new Toast("headphones", "#30D158", DemoContent.ChargeTitle, DemoContent.ChargeSubtitle, Event: "deviceOn",
            Module: "devices");

        _state.Agents.Update(DemoContent.AgentsInput(_claude, _codex, nowMs), nowMs);
        _state.Ci.Update(DemoContent.BuildInput(done: false), nowMs);
        _state.Publish();
    }

    /// Applies the frame of the timeline at a time (any time: the loop wraps). Publishes when a shown part changed. Start comes first.
    public void Tick(TimeSpan time, DateTimeOffset now)
    {
        var frame = DemoTimeline.At(time);
        var before = _frame;
        var changed = false;

        if (frame.AskPending && !before.AskPending)
        {
            _state.Asks.Add(_ask);
            changed = true;
        }
        if (frame.AllowPressed != before.AllowPressed)
        {
            _pressAllow(frame.AllowPressed);
        }
        if (frame.Toast != before.Toast)
        {
            ShowToast(frame.Toast, now);
            changed = true;
        }
        if (!frame.AskPending && before.AskPending)
        {
            _state.Asks.Remove(_ask);  // after the toast: the ask's toast leaves with the ask's answer
            changed = true;
        }
        if (frame.BuildDone != before.BuildDone)
        {
            _state.Ci.Update(DemoContent.BuildInput(done: frame.BuildDone), now.ToUnixTimeMilliseconds());
            changed = true;
        }

        _frame = frame;
        if (changed)
        {
            _state.Publish();
        }
    }

    /// The toast the frame shows: the pill is at the closed size while none is up, and a toast replaces none directly (the timeline
    /// always has a closed gap between two toasts, so the pill shrinks and grows through its own animation).
    private void ShowToast(DemoToast kind, DateTimeOffset now)
    {
        var toast = kind switch
        {
            DemoToast.Ask => _askToast,
            DemoToast.Pushed => _pushedToast,
            DemoToast.Charge => _chargeToast,
            _ => null,
        };
        if (toast is null)
        {
            _state.Toasts.Clear();
            return;
        }
        // A toast that replaces one on screen is queued first and the shown one is dismissed after it: the pill sees one change (toast to
        // toast), as the app's own queue makes it. Clearing first would show the closed pill for a moment between the two.
        var shown = _state.Toasts.Current;
        _state.Toasts.Enqueue(toast, now);
        if (shown is not null)
        {
            _state.Toasts.Dismiss(now);
        }
    }

    /// The watch item of a kind from the settings (its icon and colours), enabled; a built-in one when the settings have none.
    private WatchItem WatchItemOf(WatchKind kind, string id, string name, string symbol) =>
        _state.Settings.Items.FirstOrDefault(i => i.Kind == kind) is { } item
            ? item with { Enabled = true }
            : new WatchItem(id, name, kind, SymbolName: symbol);

    /// The notifier of the demo run: it posts nothing, so the demo shows no Windows toast.
    private sealed class NullNotifier : INotifier
    {
        public void Post(Toast t)
        {
        }
    }
}
