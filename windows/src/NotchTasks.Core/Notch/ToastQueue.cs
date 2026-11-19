namespace NotchTasks.Core.Notch;

/// The toasts the pill shows, one at a time, in order (Model.swift announce and showNextToast).
/// Pure logic: no timers. The pill calls Tick from its own timer, and time is passed in as `now`.
public sealed class ToastQueue
{
    private static readonly TimeSpan CalmTime = TimeSpan.FromMilliseconds(1500);

    private readonly List<Toast> _queue = new();
    private DateTimeOffset _shownAt;
    private DateTimeOffset? _calm;

    /// The toast on screen, or null.
    public Toast? Current { get; private set; }

    /// Raised whenever Current changes.
    public event Action? Changed;

    /// How long a toast stays: sticky 30 s, compact 3 s, else 4.5 s.
    public static TimeSpan ShowTime(Toast t) =>
        t.Sticky ? TimeSpan.FromSeconds(30) : t.Compact ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(4.5);

    /// Adds a toast; it shows at once when nothing else is on screen.
    public void Enqueue(Toast t, DateTimeOffset now)
    {
        _queue.Add(t);
        if (Current is null)
        {
            ShowNext(now);
        }
    }

    /// Takes the current toast off screen and shows the next one.
    public void Dismiss(DateTimeOffset now) => ShowNext(now);

    /// Removes a module's toasts, queued and shown.
    public void DropModule(string module, DateTimeOffset now) =>
        Drop(t => t.Module == module, now);

    /// Removes the toasts of answered asks, queued and shown.
    public void DropAsks(IReadOnlySet<string> askIds, DateTimeOffset now) =>
        Drop(t => t.AskId is { } id && askIds.Contains(id), now);

    /// Removes every toast. Used when the pill opens: the asks stay answerable in the cards.
    public void Clear()
    {
        _queue.Clear();
        if (Current is not null)
        {
            Current = null;
            Changed?.Invoke();
        }
    }

    /// Called from the pill's timer. Moves on when the toast has been up for its ShowTime and the pointer
    /// has been off it for 1.5 s. Returns true when Current changed.
    public bool Tick(DateTimeOffset now, bool hovered)
    {
        if (Current is null)
        {
            return false;
        }
        if (hovered)
        {
            _calm = now;
            return false;
        }
        if (now - _shownAt < ShowTime(Current))
        {
            return false;
        }
        if (_calm is { } calm && now - calm < CalmTime)
        {
            return false;
        }
        ShowNext(now);
        return true;
    }

    private void Drop(Func<Toast, bool> match, DateTimeOffset now)
    {
        _queue.RemoveAll(t => match(t));
        if (Current is { } shown && match(shown))
        {
            ShowNext(now);
        }
    }

    private void ShowNext(DateTimeOffset now)
    {
        var before = Current;
        Current = null;
        if (_queue.Count > 0)
        {
            Current = _queue[0];
            _queue.RemoveAt(0);
        }
        _shownAt = now;
        _calm = null;
        if (!ReferenceEquals(before, Current))
        {
            Changed?.Invoke();
        }
    }
}
