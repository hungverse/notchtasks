using LibreHardwareMonitor.Hardware;

namespace NotchTasksSensors;

/// LibreHardwareMonitor as the helper's hardware source. The Computer opens once (it needs the PawnIO driver, which the helper's
/// --install-pawnio step installs first); each Update refreshes every item and its sub-hardware.
internal sealed class LibreHardwareSource : IHardwareSource
{
    private readonly Computer _computer;

    /// Opens the CPU and GPU sensors. Throws when the library cannot open them (the caller falls back to no hardware).
    public LibreHardwareSource()
    {
        _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
        _computer.Open();
    }

    public IReadOnlyList<HardwareSnapshot> Update()
    {
        var items = new List<HardwareSnapshot>();
        foreach (var hardware in _computer.Hardware)
        {
            hardware.Update();
            Collect(hardware, items);
        }
        return items;
    }

    public void Dispose() => _computer.Close();

    private static void Collect(IHardware hardware, List<HardwareSnapshot> items)
    {
        var kind = hardware.HardwareType switch
        {
            HardwareType.Cpu => HardwareKind.Cpu,
            HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => HardwareKind.Gpu,
            _ => HardwareKind.Other,
        };
        var temperatures = hardware.Sensors
            .Where(sensor => sensor.SensorType == SensorType.Temperature)
            .Select(sensor => new TemperatureSensor(sensor.Name, sensor.Value is { } value ? value : null))
            .ToList();
        items.Add(new HardwareSnapshot(kind, temperatures));

        foreach (var sub in hardware.SubHardware)
        {
            sub.Update();
            Collect(sub, items);
        }
    }
}
