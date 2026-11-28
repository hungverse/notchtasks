using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The helper's reader: the CPU and GPU temperatures from the hardware library. When the hardware gives no CPU temperature (no PawnIO
/// driver, or a CPU the library does not read), the ACPI zones from WMI answer instead, so the helper still answers when it can.
internal sealed class HardwareThermalReader : IThermalReader, IDisposable
{
    private readonly IHardwareSource _hardware;
    private readonly IThermalReader _fallback;

    /// The reader over hardware and the fallback (the WMI reader in the helper; a fake in tests).
    public HardwareThermalReader(IHardwareSource hardware, IThermalReader fallback)
    {
        _hardware = hardware;
        _fallback = fallback;
    }

    public IReadOnlyList<ThermalReading> Read()
    {
        var items = UpdateOrNothing();
        var cpu = HardwareSensorChoice.Cpu(items);
        var gpu = HardwareSensorChoice.Gpu(items);
        if (cpu is null)
        {
            // No CPU temperature from the hardware: the ACPI zones answer, with the GPU reading when there is one. A fallback that fails
            // gives no zones; it never ends the read.
            var zones = FallbackOrNothing();
            return gpu is { } gpuReading ? [.. zones, gpuReading] : zones;
        }
        // The fallback is not asked when the hardware gave the CPU reading.
        return gpu is { } gpuValue ? [cpu.Value, gpuValue] : [cpu.Value];
    }

    public void Dispose() => _hardware.Dispose();

    /// The fallback's zones. A failure is reported and counts as no zones.
    private IReadOnlyList<ThermalReading> FallbackOrNothing()
    {
        try
        {
            return _fallback.Read();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            HelperLog.Once(e);
            return Array.Empty<ThermalReading>();
        }
    }

    /// The hardware's update. A failed update counts as no hardware, so the fallback answers.
    private IReadOnlyList<HardwareSnapshot> UpdateOrNothing()
    {
        try
        {
            return _hardware.Update();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            HelperLog.Once(e);
            return Array.Empty<HardwareSnapshot>();
        }
    }
}
