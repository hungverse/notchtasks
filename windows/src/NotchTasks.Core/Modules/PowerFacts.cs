namespace NotchTasks.Core.Modules;

/// The power state the battery module reads, from GetSystemPowerStatus. Percent is -1 when the level is unknown.
public readonly record struct PowerFacts(bool HasBattery, int Percent, bool Charging, bool Plugged)
{
    /// The Windows call's status: BatteryFlag 128 means no system battery, bit 8 means charging; ACLineStatus 1 means
    /// plugged in; BatteryLifePercent 255 means unknown. Pure, so the mapping is tested without the OS.
    public static PowerFacts FromStatus(byte acLineStatus, byte batteryFlag, byte batteryLifePercent) => new(
        HasBattery: batteryFlag != 128,
        Percent: batteryLifePercent == 255 ? -1 : batteryLifePercent,
        Charging: (batteryFlag & 8) != 0,
        Plugged: acLineStatus == 1);

    /// The power state now. A failed call reads as no battery.
    public static PowerFacts Read()
    {
        if (!NativeMethods.GetSystemPowerStatus(out var status))
        {
            return default;
        }
        return FromStatus(status.ACLineStatus, status.BatteryFlag, status.BatteryLifePercent);
    }

    /// Whether this PC has a system battery.
    public static bool HasSystemBattery() => Read().HasBattery;
}
