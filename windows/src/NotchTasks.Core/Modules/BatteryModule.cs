using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Modules;

/// The battery module (Mac: updateBattery in Model.swift). The first reading is the baseline: a plug change after it
/// announces a compact toast with its charge art, without a notification. Plugging in re-arms the low warning, which
/// fires once when the battery is unplugged at 20% or less. No timers and no OS calls: the source feeds PowerFacts.
public sealed class BatteryModule
{
    private const int LowPercent = 20;

    private readonly IAnnouncer _announce;
    private bool _lowWarned;

    /// The announcer receives the battery toasts.
    public BatteryModule(IAnnouncer announce)
    {
        _announce = announce;
    }

    /// The last reading, or null when this PC has no battery (or the module is off).
    public BatteryState? State { get; private set; }

    /// Takes a reading. Returns true when State changed. With no battery State becomes null; an unknown level keeps the
    /// state as it was, as the Mac does.
    public bool Update(PowerFacts f)
    {
        if (!f.HasBattery)
        {
            return Clear();
        }
        if (f.Percent < 0)
        {
            return false;
        }

        var b = new BatteryState(f.Percent, f.Charging, f.Plugged);
        var old = State;
        if (old is not null && old.Plugged != b.Plugged)
        {
            _announce.Announce(
                b.Plugged
                    ? new Toast(
                        "bolt",
                        "#30D158",
                        L10n.L("Charging"),
                        $"{b.Percent}%",
                        Event: "charging",
                        Compact: true,
                        Art: new ChargeArt(b.Percent, true),
                        Module: "battery")
                    : new Toast(
                        "plug",
                        "#8E8E93",
                        L10n.L("Unplugged"),
                        $"{b.Percent}%",
                        Event: "unplugged",
                        Compact: true,
                        Art: new ChargeArt(b.Percent, false),
                        Module: "battery"),
                notify: false);
        }
        if (b.Plugged)
        {
            _lowWarned = false;
        }
        if (!b.Plugged && b.Percent <= LowPercent && !_lowWarned)
        {
            _lowWarned = true;
            _announce.Announce(new Toast(
                "batterylow",
                "#FF453A",
                L10n.L("Low battery"),
                L10n.L("{0}% left", b.Percent),
                Event: "lowBattery",
                Module: "battery"), notify: true);
        }
        if (State == b)
        {
            return false;
        }
        State = b;
        return true;
    }

    /// Drops the reading without a toast: the module is off. Returns true when State changed.
    public bool Clear()
    {
        if (State is null)
        {
            return false;
        }
        State = null;
        return true;
    }
}
