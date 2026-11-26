using NotchTasks.Core.Agents;
using NotchTasks.Core.Ci;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Media;

namespace NotchTasks.Core.Notch;

/// The state the closed pill's chips are built from. ActiveModules are the module ids that are on, in the user's order.
/// Limits are the providers' limits (none unless the limits module is on); LimitWarn is the level a window must reach to
/// show a limit chip (the Mac's limitWarn, 50-100).
public sealed record NotchSnapshot(
    IReadOnlyList<string> ActiveModules,
    int AskCount,
    TimerState? Timer,
    CallInfo? Call,
    SensorState Sensors,
    BatteryState? Battery,
    SystemLoad? Load)
{
    /// The agents that are working now, in the watch list's order (none unless the agents module is on).
    public IReadOnlyList<ItemState> Running { get; init; } = Array.Empty<ItemState>();

    /// Every agent job, working or idle (zero unless the agents module is on). The header's "waiting" is this minus the working ones.
    public int JobCount { get; init; }

    /// The Build & CI rows that are working now: the build row first, then the build watch items (none unless ci is on).
    public IReadOnlyList<ItemState> BuildRunning { get; init; } = Array.Empty<ItemState>();

    /// The Build & CI failure alerts, GitHub's first (none unless ci is on).
    public IReadOnlyList<JobAlert> Alerts { get; init; } = Array.Empty<JobAlert>();

    public IReadOnlyList<ProviderLimits> Limits { get; init; } = Array.Empty<ProviderLimits>();

    public double LimitWarn { get; init; } = 80;

    /// The session the Now Playing card shows (none unless the media module is on and a session is listed). Its chip shows only while it plays.
    public NowPlaying? Media { get; init; }

    /// The item of the first ask waiting, whose icon the ask chip shows (null when no ask waits or the agents module is off).
    public WatchItem? AskItem { get; init; }
}
