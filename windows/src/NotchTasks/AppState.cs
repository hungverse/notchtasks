using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Threading;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Core.Settings;
using NotchTasks.Services;

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
    private readonly Dictionary<string, IModuleSource> _sources = new(StringComparer.Ordinal);
    private readonly ModuleHost _host;
    private AskWatcher? _watcher;
    private bool _disposed;

    /// Creates the state on the UI thread. Call StartWatching next. The modules that are on start here.
    public AppState(SettingsStore settings, AskStore store)
    {
        Settings = settings;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Asks = new ObservableCollection<AgentAsk>();
        _host = new ModuleHost(_sources, OnTurnedOff);
        _host.Apply(ActiveModules(settings));
        Snapshot = BuildSnapshot();
        settings.Changed += OnSettingsChanged;
    }

    public SettingsStore Settings { get; }

    /// The asks still waiting, oldest first. Changed on the UI thread only.
    public ObservableCollection<AgentAsk> Asks { get; }

    /// The toasts the pill shows, in order. Asks queue here too.
    public ToastQueue Toasts { get; } = new();

    /// The state the closed pill's chips are built from. Rebuilt by Publish.
    public NotchSnapshot Snapshot { get; private set; }

    /// Raised after Snapshot is rebuilt.
    public event Action? NotchChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// The modules that are on: Modules without Disabled, in the user's order.
    public static IReadOnlyList<string> ActiveModules(SettingsStore s)
    {
        var disabled = s.Disabled;
        return s.Modules.Where(id => !disabled.Contains(id)).ToList();
    }

    /// Plays the toast's sound (its Event) and queues the toast. notify is ignored until system notifications exist.
    public void Announce(Toast t, bool notify = true)
    {
        if (t.Event is { } sound)
        {
            Sounds.Play(sound);
        }
        Toasts.Enqueue(t, DateTimeOffset.Now);
    }

    /// Rebuilds Snapshot from the modules and the asks, and raises NotchChanged.
    public void Publish()
    {
        Snapshot = BuildSnapshot();
        NotchChanged?.Invoke();
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
        try
        {
            _store.Reply(askId, reply);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException($"NotchTasks: could not write the reply for ask {askId}", e));
            return;
        }
        _tracker.MarkAnswered(askId);
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

    /// Brings Asks in line with the watcher's list: drops the asks that are gone (and their toasts), and inserts the new
    /// ones at their place in the list, so Asks stays oldest first. Each new ask is announced: its agentAsk sound plays
    /// and its toast queues.
    private void SyncAsks(IReadOnlyList<AgentAsk> list)
    {
        if (_disposed)
        {
            return;
        }
        var visible = _tracker.Filter(list);
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
            Announce(AskToast(visible[i]));
        }
        Publish();
    }

    /// The toast a new ask shows: a hand for an approval, a question mark for questions. Its texts come from
    /// AskPresentation, the same as the card's.
    private static Toast AskToast(AgentAsk ask) => new(
        ask.IsQuestion ? "question" : "hand",
        AskTint,
        AskPresentation.Title(ask),
        AskPresentation.Subtitle(ask),
        Event: "agentAsk",
        Sticky: true,
        AskId: ask.Id,
        Module: "agents");

    private NotchSnapshot BuildSnapshot() => new(
        ActiveModules(Settings),
        Asks.Count,
        null,
        null,
        new SensorState(Array.Empty<string>(), false, false),
        null,
        null);

    /// Starts and stops the module sources when the modules or the disabled list change.
    private void OnSettingsChanged(IReadOnlyCollection<string> keys) =>
        _dispatcher.BeginInvoke(new Action(() =>
        {
            OnPropertyChanged(nameof(Settings));
            if (keys.Any(k => k is "modules" or "disabled"))
            {
                ApplyModules();
            }
        }));

    private void ApplyModules()
    {
        if (_disposed)
        {
            return;
        }
        _host.Apply(ActiveModules(Settings));
        Publish();
    }

    /// A module was turned off: its toasts go.
    private void OnTurnedOff(string id)
    {
        Toasts.DropModule(id, DateTimeOffset.Now);
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
        _watcher?.Dispose();
        _host.StopAll();
    }
}
