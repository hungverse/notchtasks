namespace NotchTasks.Core.Notch;

/// The state the closed pill's chips are built from. ActiveModules are the module ids that are on, in the user's order.
public sealed record NotchSnapshot(
    IReadOnlyList<string> ActiveModules,
    int AskCount,
    TimerState? Timer,
    CallInfo? Call,
    SensorState Sensors,
    BatteryState? Battery,
    SystemLoad? Load);
