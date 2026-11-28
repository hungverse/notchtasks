using NotchTasks.Core.Modules;

namespace NotchTasksSensors;

/// The kind of a hardware item, as the sensor choice reads it: the CPU, a GPU, or anything else (memory, storage, a board).
internal enum HardwareKind
{
    Cpu,
    Gpu,
    Other,
}

/// One temperature sensor of a hardware item: its name, and its value in degrees Celsius (null when it has none now).
internal readonly record struct TemperatureSensor(string Name, double? Celsius);

/// One hardware item (the CPU, a GPU, ...) with its temperature sensors.
internal sealed record HardwareSnapshot(HardwareKind Kind, IReadOnlyList<TemperatureSensor> Temperatures);

/// The hardware the helper reads its CPU and GPU temperatures from (LibreHardwareMonitor). Tests pass a fake.
internal interface IHardwareSource : IDisposable
{
    /// Updates every hardware item and returns them, the sub-hardware included. Empty when nothing can be read (no driver, or the
    /// open failed). Throws only when the update itself fails; the reader treats a throw as nothing.
    IReadOnlyList<HardwareSnapshot> Update();
}

/// A source with no hardware: the helper runs with the WMI zones alone when LibreHardwareMonitor cannot open.
internal sealed class NoHardwareSource : IHardwareSource
{
    public IReadOnlyList<HardwareSnapshot> Update() => Array.Empty<HardwareSnapshot>();

    public void Dispose()
    {
    }
}

/// Which sensor gives the CPU and the GPU temperature. Pure: it works on the snapshots, so the choice is tested without the hardware
/// library.
internal static class HardwareSensorChoice
{
    /// The reading names the app shows: the CPU package and the GPU core.
    public const string CpuName = "CPU";
    public const string GpuName = "GPU";

    /// The CPU temperature. AMD's Tctl/Tdie first, then Intel's "CPU Package", then the hottest "Core" sensor. Null when none has a value.
    public static ThermalReading? Cpu(IReadOnlyList<HardwareSnapshot> items)
    {
        var sensors = items.Where(item => item.Kind == HardwareKind.Cpu).SelectMany(item => item.Temperatures).ToList();
        var celsius = ValueOf(sensors, "Core (Tctl/Tdie)")
            ?? ValueOf(sensors, "CPU Package")
            ?? HottestCore(sensors);
        return celsius is { } value ? new ThermalReading(CpuName, value) : null;
    }

    /// The GPU core temperature ("GPU Core") of the first GPU that reports one. Null when none does.
    public static ThermalReading? Gpu(IReadOnlyList<HardwareSnapshot> items)
    {
        foreach (var item in items.Where(item => item.Kind == HardwareKind.Gpu))
        {
            if (ValueOf(item.Temperatures, "GPU Core") is { } celsius)
            {
                return new ThermalReading(GpuName, celsius);
            }
        }
        return null;
    }

    private static double? ValueOf(IEnumerable<TemperatureSensor> sensors, string name)
    {
        foreach (var sensor in sensors)
        {
            if (sensor.Name == name && sensor.Celsius is { } celsius)
            {
                return celsius;
            }
        }
        return null;
    }

    private static double? HottestCore(IEnumerable<TemperatureSensor> sensors)
    {
        double? hottest = null;
        foreach (var sensor in sensors)
        {
            if (sensor.Name.StartsWith("Core", StringComparison.Ordinal) && sensor.Celsius is { } celsius
                && (hottest is not { } current || celsius > current))
            {
                hottest = celsius;
            }
        }
        return hottest;
    }
}
