using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Settings;
using NotchTasks.Services;

namespace NotchTasks;

/// The app's state. It lives on the UI thread: the watcher and the settings callbacks run on other threads and are
/// marshalled to the dispatcher captured when this object was made.
public sealed class AppState : INotifyPropertyChanged, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly AskStore _store;
    private readonly AskTracker _tracker = new();
    private AskWatcher? _watcher;
    private bool _disposed;

    /// Creates the state on the UI thread. Call StartWatching next.
    public AppState(SettingsStore settings, AskStore store)
    {
        Settings = settings;
        _store = store;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Asks = new ObservableCollection<AgentAsk>();
        settings.Changed += OnSettingsChanged;
    }

    public SettingsStore Settings { get; }

    /// The asks still waiting, oldest first. Changed on the UI thread only.
    public ObservableCollection<AgentAsk> Asks { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Starts watching the Asks folder. The current list is reported at once (through the dispatcher).
    public void StartWatching()
    {
        _watcher = new AskWatcher(_store, list => _dispatcher.BeginInvoke(new Action(() => SyncAsks(list))));
    }

    /// Writes the answer for an ask, then removes the ask from Asks at once.
    public void Answer(string askId, AskReply reply)
    {
        _store.Reply(askId, reply);
        _tracker.MarkAnswered(askId);
        for (var i = Asks.Count - 1; i >= 0; i--)
        {
            if (Asks[i].Id == askId)
            {
                Asks.RemoveAt(i);
            }
        }
    }

    /// Brings Asks in line with the watcher's list: drops the asks that are gone, and inserts the new ones at their
    /// place in the list, so Asks stays oldest first. Plays the agentAsk sound for each new ask.
    private void SyncAsks(IReadOnlyList<AgentAsk> list)
    {
        if (_disposed)
        {
            return;
        }
        var visible = _tracker.Filter(list);
        var wanted = new HashSet<string>(visible.Select(a => a.Id), StringComparer.Ordinal);
        for (var i = Asks.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Asks[i].Id))
            {
                Asks.RemoveAt(i);
            }
        }
        // After the removals Asks is a subsequence of the list, so each new ask goes in at its own index.
        for (var i = 0; i < visible.Count; i++)
        {
            if (i < Asks.Count && Asks[i].Id == visible[i].Id)
            {
                continue;
            }
            Asks.Insert(Math.Min(i, Asks.Count), visible[i]);
            Sounds.Play("agentAsk");
        }
    }

    private void OnSettingsChanged(IReadOnlyCollection<string> keys) =>
        _dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(nameof(Settings))));

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
    }
}
