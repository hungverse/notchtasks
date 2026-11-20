using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Devices;

/// The devices module (Mac: devicesFound in Model.swift). The first scan is a baseline with no toasts. Later scans
/// toast each new device as connected, each gone device as disconnected, and a device at 15% or below once as low
/// battery; the low warning re-arms when the level is above 30. The source feeds DeviceFact lists; there are no
/// timers and no OS calls.
public sealed class DevicesModule(IAnnouncer announce, Func<string?> tint)
{
    private const int LowLevel = 15;
    private const int RearmLevel = 30;
    private const string DefaultTint = "#0A84FF";
    private const string DisconnectedTint = "#8E8E93";
    private const string LowTint = "#FF453A";

    private readonly HashSet<string> _lowWarned = new(StringComparer.Ordinal);
    private bool _baseline;

    /// The device rows, in the order the source gave them.
    public IReadOnlyList<Device> Devices { get; private set; } = Array.Empty<Device>();

    /// Takes a scan. Returns true when Devices changed. Facts that are not shown are dropped, and a repeated id keeps
    /// its first fact.
    public bool Update(IReadOnlyList<DeviceFact> found)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var next = new List<Device>();
        foreach (var f in found)
        {
            if (!DeviceKinds.Shown(f.Categories) || !seen.Add(f.Id))
            {
                continue;
            }
            next.Add(new Device(
                f.Id,
                f.Name,
                DeviceKinds.Symbol(f.Name, f.Categories),
                f.Battery is int b ? $"{b}%" : null,
                f.Battery));
        }

        var old = Devices;
        var changed = !old.SequenceEqual(next);
        Devices = next;

        if (!_baseline)
        {
            // The first scan is the baseline. A device already low is taken as warned, so it does not toast later.
            _baseline = true;
            foreach (var d in next)
            {
                if (d.Lowest is int low && low <= LowLevel)
                {
                    _lowWarned.Add(d.Id);
                }
            }
            return changed;
        }

        var oldIds = new HashSet<string>(old.Select(d => d.Id), StringComparer.Ordinal);
        var newIds = new HashSet<string>(next.Select(d => d.Id), StringComparer.Ordinal);

        foreach (var d in next)
        {
            if (!oldIds.Contains(d.Id))
            {
                announce.Announce(new Toast(
                    d.Symbol,
                    tint() ?? DefaultTint,
                    L10n.L("{0} connected", d.Name),
                    d.Battery ?? L10n.L("Ready"),
                    Event: "deviceOn",
                    Art: new DeviceArt(d.Symbol, true),
                    Module: "devices"), notify: false);
            }
        }

        foreach (var d in old)
        {
            if (!newIds.Contains(d.Id))
            {
                _lowWarned.Remove(d.Id);
                announce.Announce(new Toast(
                    d.Symbol,
                    DisconnectedTint,
                    L10n.L("{0} disconnected", d.Name),
                    Event: "deviceOff",
                    Art: new DeviceArt(d.Symbol, false),
                    Module: "devices"), notify: false);
            }
        }

        foreach (var d in next)
        {
            if (d.Lowest is not int low)
            {
                continue;
            }
            if (low > RearmLevel)
            {
                _lowWarned.Remove(d.Id);
            }
            else if (low <= LowLevel && _lowWarned.Add(d.Id))
            {
                announce.Announce(new Toast(
                    "battery.25percent",
                    LowTint,
                    L10n.L("{0} battery low", d.Name),
                    $"{low}%",
                    Event: "lowBattery",
                    Module: "devices"), notify: false);
            }
        }

        return changed;
    }

    /// Drops every row and the baseline, so the next scan is a new baseline with no toasts.
    public void Clear()
    {
        Devices = Array.Empty<Device>();
        _lowWarned.Clear();
        _baseline = false;
    }
}
