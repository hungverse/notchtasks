namespace NotchTasks.Core.Notch;

/// The kinds of chip the closed pill can show. More come with later modules.
public enum ChipKind { Call, Ask, Timer, Sensor }

/// One chip on the closed pill. Module is the owning module id (used for placement); Id is unique per chip.
/// Only the payload for its Kind is set: Timer for Timer, Call for Call, Sensors for Sensor, AskCount for Ask.
public sealed record Chip(ChipKind Kind, string Module, string Id)
{
    public TimerState? Timer { get; init; }
    public CallInfo? Call { get; init; }
    public SensorState? Sensors { get; init; }
    public int AskCount { get; init; }
}
