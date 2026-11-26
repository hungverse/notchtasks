using NotchTasks.Core.Agents;
using NotchTasks.Core.Calendars;
using NotchTasks.Core.Limits;
using NotchTasks.Core.Media;

namespace NotchTasks.Core.Notch;

/// The kinds of chip the closed pill can show. More come with later modules.
public enum ChipKind { Call, Ask, Timer, Sensor, Limit, Agent, Alerts, Media, Calendar }

/// A provider's limit on the pill: the provider's name and its window at or above the warning level (Mac: NotchView .limit).
public sealed record LimitChip(string Name, LimitWindow W);

/// One chip on the closed pill. Module is the owning module id (used for placement); Id is unique per chip.
/// Only the payload for its Kind is set: Timer for Timer, Call for Call, Sensors for Sensor, AskCount for Ask, Limit for Limit, Agent for Agent,
/// AlertCount for Alerts, Media for Media.
public sealed record Chip(ChipKind Kind, string Module, string Id)
{
    public TimerState? Timer { get; init; }
    public CallInfo? Call { get; init; }
    public SensorState? Sensors { get; init; }
    public int AskCount { get; init; }

    /// The item of the agent asking first: its icon leads the ask chip (Mac's ask chip shows the asking agent's icon).
    public WatchItem? AskItem { get; init; }
    public int AlertCount { get; init; }
    public LimitChip? Limit { get; init; }
    public ItemState? Agent { get; init; }
    public NowPlaying? Media { get; init; }

    /// The calendar event the chip shows: a timed one starting soon, or running (Mac: the calendar chip's soonEvent).
    public CalendarEvent? Event { get; init; }
}
