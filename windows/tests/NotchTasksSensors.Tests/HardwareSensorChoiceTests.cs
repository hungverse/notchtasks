using NotchTasks.Core.Modules;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The CPU and GPU sensor choice, through fake hardware trees (no LibreHardwareMonitor, no PawnIO).
public sealed class HardwareSensorChoiceTests
{
    private static HardwareSnapshot Cpu(params (string Name, double? Celsius)[] sensors) =>
        new(HardwareKind.Cpu, sensors.Select(s => new TemperatureSensor(s.Name, s.Celsius)).ToList());

    private static HardwareSnapshot Gpu(params (string Name, double? Celsius)[] sensors) =>
        new(HardwareKind.Gpu, sensors.Select(s => new TemperatureSensor(s.Name, s.Celsius)).ToList());

    [Fact]
    public void IntelPackage_IsTheCpuReading()
    {
        var items = new[] { Cpu(("Core #1", 50), ("Core #2", 55), ("CPU Package", 58)) };

        Assert.Equal(new ThermalReading("CPU", 58), HardwareSensorChoice.Cpu(items));
    }

    [Fact]
    public void AmdTctlTdie_IsPreferredOverThePackage()
    {
        var items = new[] { Cpu(("CPU Package", 61), ("Core (Tctl/Tdie)", 64), ("Core #1", 70)) };

        Assert.Equal(new ThermalReading("CPU", 64), HardwareSensorChoice.Cpu(items));
    }

    [Fact]
    public void CoresOnly_IsTheHottestCore()
    {
        var items = new[] { Cpu(("Core #1", 47), ("Core #2", 52.5), ("Core #3", 49), ("Bus Speed", 99)) };

        Assert.Equal(new ThermalReading("CPU", 52.5), HardwareSensorChoice.Cpu(items));
    }

    [Fact]
    public void CpuWithoutAValue_IsNoReading()
    {
        var items = new[] { Cpu(("CPU Package", null), ("Core #1", null)) };

        Assert.Null(HardwareSensorChoice.Cpu(items));
    }

    [Fact]
    public void NoCpuHardware_IsNoReading()
    {
        var items = new[] { new HardwareSnapshot(HardwareKind.Other, [new TemperatureSensor("CPU Package", 60)]) };

        Assert.Null(HardwareSensorChoice.Cpu(items));
    }

    [Fact]
    public void GpuCore_IsTheGpuReading()
    {
        var items = new[] { Gpu(("GPU Hot Spot", 80), ("GPU Core", 63)) };

        Assert.Equal(new ThermalReading("GPU", 63), HardwareSensorChoice.Gpu(items));
    }

    [Fact]
    public void GpuWithoutCore_IsNoReading()
    {
        var items = new[] { Gpu(("GPU Memory", 70)), Gpu(("GPU Core", null)) };

        Assert.Null(HardwareSensorChoice.Gpu(items));
    }

    [Fact]
    public void NothingReported_IsNoReadings()
    {
        Assert.Null(HardwareSensorChoice.Cpu(Array.Empty<HardwareSnapshot>()));
        Assert.Null(HardwareSensorChoice.Gpu(Array.Empty<HardwareSnapshot>()));
    }
}
