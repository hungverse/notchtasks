using NotchTasks.Core.Limits;

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
    public IReadOnlyList<ProviderLimits> Limits { get; init; } = Array.Empty<ProviderLimits>();

    public double LimitWarn { get; init; } = 80;
}
