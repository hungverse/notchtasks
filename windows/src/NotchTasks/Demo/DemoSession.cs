using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Demo;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Media;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services.Notifications;

namespace NotchTasks.Demo;

/// The demo run's state and its timeline (`NotchTasks.exe --demo`). The content is made up, and it goes into the same state objects
/// the real sources feed (the agents, the build row, the media session, the limits, the timer, the asks and the toasts), so the
/// pill's own views draw it. Tick applies the frame of the timeline at a time: the pill's open or closed state, the ask, the Allow
/// hold and the toast. Nothing reaches the OS: the state has no source, no asks folder write and no settings file (see NewState).
public sealed class DemoSession
{
    private readonly AppState _state;
    private readonly Action<bool> _setOpen;
    private readonly Action<bool> _pressAllow;
    private DemoFrame _frame = new(PillOpen: false, AskPending: false, AllowPressed: false, Toast: DemoToast.None);
    private WatchItem _claude = null!;
    private WatchItem _codex = null!;
    private AgentAsk _ask = null!;
    private Toast _askToast = null!;
    private Toast _pushedToast = null!;
    private Toast _chargeToast = null!;
    private long _shownSecond = -1;  // the focus timer's whole seconds as the last publish showed them

    /// The demo over a state (NewState). setOpen opens or closes the pill; pressAllow holds the ask toast's Allow down or lets it go.
    public DemoSession(AppState state, Action<bool> setOpen, Action<bool> pressAllow)
    {
        _state = state;
        _setOpen = setOpen;
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

    /// The state the demo's pill opens with: the agents, the build row, the now playing session, the limits and the focus timer.
    /// Call once, before the first Tick.
    public void Start(DateTimeOffset now)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        _claude = WatchItemOf(WatchKind.Claude, "claude", "Claude", "sparkles");
        _codex = WatchItemOf(WatchKind.Codex, "codex", "Codex", "terminal.fill");
        _ask = new AgentAsk(
            Id: "demo-ask-push",
            Pid: 0,
            Session: "api-server",
            Tool: "Bash",
            Summary: "git push origin main",
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
        _pushedToast = new Toast("check", "#30D158", DemoContent.PushedTitle, DemoContent.PushedSubtitle, Event: "taskDone",
            Module: "agents", Icon: _claude);
        _chargeToast = new Toast("headphones", "#30D158", DemoContent.ChargeTitle, DemoContent.ChargeSubtitle, Event: "deviceOn",
            Module: "devices", Art: new DeviceArt("airpodspro", true));

        _state.Agents.Update(DemoContent.AgentsInput(_claude, _codex, nowMs), nowMs);
        _state.Ci.Update(DemoContent.BuildInput(nowMs), nowMs);
        _state.Media.Update(new[] { DemoContent.MediaSession(nowMs) }, nowMs);
        _state.PutMediaArt(DemoContent.ArtKey, DemoContent.Artwork());
        _state.LimitsModule.Update(DemoContent.LimitsInput(now, _state.Settings.LimitWarn), now);
        StartTimer(now);
        _state.Publish();
    }

    /// The watch item of a kind from the settings (its icon and colours), enabled; a built-in one when the settings have none.
    private WatchItem WatchItemOf(WatchKind kind, string id, string name, string symbol) =>
        _state.Settings.Items.FirstOrDefault(i => i.Kind == kind) is { } item
            ? item with { Enabled = true }
            : new WatchItem(id, name, kind, SymbolName: symbol);

    /// Applies the frame of the timeline at a time (any time: the loop wraps). Publishes when a shown part changed, or the focus timer
    /// showed another second. Start comes first.
    public void Tick(TimeSpan time, DateTimeOffset now)
    {
        var frame = DemoTimeline.At(time);
        var before = _frame;
        var changed = false;

        if (frame.PillOpen != before.PillOpen)
        {
            _setOpen(frame.PillOpen);
            if (frame.PillOpen)
            {
                StartTimer(now);  // the focus timer reads 18:42 as the pill opens, in every loop
            }
            changed = true;
        }
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

        _frame = frame;
        var second = WholeSecondsLeft(now);
        if (changed || second != _shownSecond)
        {
            _shownSecond = second;
            _state.Publish();
        }
    }

    private void ShowToast(DemoToast kind, DateTimeOffset now)
    {
        _state.Toasts.Clear();
        var toast = kind switch
        {
            DemoToast.Ask => _askToast,
            DemoToast.Pushed => _pushedToast,
            DemoToast.Charge => _chargeToast,
            _ => null,
        };
        if (toast is not null)
        {
            _state.Toasts.Enqueue(toast, now);
        }
    }

    /// The focus timer: 18 min 42 s left from `now`. Its total is the 19 min the timer was started with.
    private void StartTimer(DateTimeOffset now) => _state.Timer.Start(19, now.AddSeconds(-18));

    private long WholeSecondsLeft(DateTimeOffset now) =>
        _state.Timer.State is { } timer ? (long)Math.Floor((timer.End - now).TotalSeconds) : -1;

    /// The notifier of the demo run: it posts nothing, so the demo shows no Windows toast.
    private sealed class NullNotifier : INotifier
    {
        public void Post(Toast t)
        {
        }
    }
}
