using System.Windows.Threading;

namespace NotchTasks.Pill;

/// One shared timer for every sprite on the pill. It ticks at the fastest visible sprite's frame time, and each sprite
/// steps on its own frames. The timer runs only while a sprite is visible, and stops when none is.
internal static class SpriteClock
{
    private static readonly List<SpriteView> Sprites = new();
    private static DispatcherTimer? _timer;

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
        if (visible.Count == 0)
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
