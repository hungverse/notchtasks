using System.Windows.Threading;
using Microsoft.Win32;

namespace NotchTasks.Pill;

/// Why the sprites stand still. The clock runs only when no reason holds.
[Flags]
internal enum ClockPause
{
    None = 0,
    SessionLocked = 1,
    Suspended = 2,
    DisplayOff = 4,
}

/// One shared timer for every sprite on the pill. It ticks at the fastest visible sprite's frame time, and each sprite
/// steps on its own frames. The timer runs only while a sprite is visible, and stops when none is, or while a pause holds.
internal static class SpriteClock
{
    private static readonly List<SpriteView> Sprites = new();
    private static DispatcherTimer? _timer;
    private static ClockPause _pauses;
    private static bool _watching;
    private static long _epochMs;

    /// The moment (Unix ms) that frame 0 of every sprite falls on: state.json animationEpoch, set by Sync motion.
    public static long EpochMs => _epochMs;

    /// Sets the moment the sprites are in step with (read at start, and written by Sync motion). Every sprite moves to its
    /// frame for the new moment at once.
    public static void SetEpoch(long epochMs)
    {
        _epochMs = epochMs;
        foreach (var sprite in Sprites)
        {
            sprite.Step();
        }
    }

    /// Milliseconds since the epoch (negative before it).
    internal static double ElapsedMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _epochMs;

    /// Pauses the sprites for a reason (the session locked, the machine asleep, the display off), or resumes them.
    public static void SetPause(ClockPause reason, bool on)
    {
        _pauses = on ? _pauses | reason : _pauses & ~reason;
        Refresh();
    }

    /// Follows the session lock and the sleep and resume of the machine. The display state comes from the pill window's
    /// power notification. Call once; later calls do nothing.
    public static void WatchSystem()
    {
        if (_watching)
        {
            return;
        }
        _watching = true;
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionUnlock)
            {
                SetPause(ClockPause.SessionLocked, e.Reason == SessionSwitchReason.SessionLock);
            }
        };
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
            {
                SetPause(ClockPause.Suspended, e.Mode == PowerModes.Suspend);
            }
        };
    }

    public static void Add(SpriteView sprite)
    {
        if (!Sprites.Contains(sprite))
        {
            Sprites.Add(sprite);
        }
        Refresh();
    }

    public static void Remove(SpriteView sprite)
    {
        Sprites.Remove(sprite);
        Refresh();
    }

    /// Re-reads which sprites are visible and sets the timer to match. Call when a sprite's visibility changes.
    public static void Refresh()
    {
        var visible = Sprites.Where(s => s.IsVisible).ToList();
        if (visible.Count == 0 || _pauses != ClockPause.None)
        {
            _timer?.Stop();
            return;
        }

        var interval = visible.Min(s => s.FrameSeconds);
        foreach (var sprite in visible)
        {
            sprite.Stride = Math.Max(1, (int)Math.Round(sprite.FrameSeconds / interval));
        }

        _timer ??= CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(interval);
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            foreach (var sprite in Sprites)
            {
                if (!sprite.IsVisible)
                {
                    continue;
                }
                sprite.TicksSinceStep++;
                if (sprite.TicksSinceStep >= sprite.Stride)
                {
                    sprite.TicksSinceStep = 0;
                    sprite.Step();
                }
            }
        };
        return timer;
    }
}
