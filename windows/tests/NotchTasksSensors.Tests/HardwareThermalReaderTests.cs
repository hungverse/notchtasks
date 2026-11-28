using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The helper's reader over a fake hardware source and a fake fallback: the hardware's CPU and GPU first, the WMI zones when the
/// hardware gives no CPU temperature.
public sealed class HardwareThermalReaderTests
{
    private sealed class FakeHardware : IHardwareSource
    {
        public IReadOnlyList<HardwareSnapshot> Items { get; set; } = Array.Empty<HardwareSnapshot>();
        public Exception? Failure { get; set; }
        public int Disposed { get; private set; }

        public IReadOnlyList<HardwareSnapshot> Update()
        {
            if (Failure is not null)
            {
                throw Failure;
            }
            return Items;
        }

        public void Dispose() => Disposed++;
    }

    private sealed class FakeFallback : IThermalReader
    {
        public IReadOnlyList<ThermalReading> Zones { get; set; } = Array.Empty<ThermalReading>();
        public int Reads { get; private set; }

        public IReadOnlyList<ThermalReading> Read()
        {
            Reads++;
            return Zones;
        }
    }

    private sealed class ThrowingFallback : IThermalReader
    {
        public IReadOnlyList<ThermalReading> Read() => throw new InvalidOperationException("WMI");
    }

    private static HardwareSnapshot Cpu(string name, double celsius) =>
        new(HardwareKind.Cpu, [new TemperatureSensor(name, celsius)]);

    [Fact]
    public void CpuFromHardware_IsReadWithoutTheFallback()
    {
        var hardware = new FakeHardware { Items = [Cpu("CPU Package", 58)] };
        var fallback = new FakeFallback { Zones = [new ThermalReading("\\_TZ.TZ00", 28)] };

        var readings = new HardwareThermalReader(hardware, fallback).Read();

        Assert.Equal([new ThermalReading("CPU", 58)], readings);
        Assert.Equal(0, fallback.Reads);
    }

    [Fact]
    public void GpuCoreIsReadWithTheCpu()
    {
        var hardware = new FakeHardware
        {
            Items = [Cpu("CPU Package", 58), new HardwareSnapshot(HardwareKind.Gpu, [new TemperatureSensor("GPU Core", 63)])],
        };

        var readings = new HardwareThermalReader(hardware, new FakeFallback()).Read();

        Assert.Equal([new ThermalReading("CPU", 58), new ThermalReading("GPU", 63)], readings);
    }

    [Fact]
    public void NoCpuTemperature_UsesTheFallback()
    {
        var hardware = new FakeHardware { Items = [new HardwareSnapshot(HardwareKind.Other, [new TemperatureSensor("CPU Package", 60)])] };
        var fallback = new FakeFallback { Zones = [new ThermalReading("\\_TZ.TZ00", 41)] };

        var readings = new HardwareThermalReader(hardware, fallback).Read();

        Assert.Equal([new ThermalReading("\\_TZ.TZ00", 41)], readings);
        Assert.Equal(1, fallback.Reads);
    }

    [Fact]
    public void NoCpuButAGpu_UsesTheFallbackWithTheGpu()
    {
        var hardware = new FakeHardware { Items = [new HardwareSnapshot(HardwareKind.Gpu, [new TemperatureSensor("GPU Core", 55)])] };
        var fallback = new FakeFallback { Zones = [new ThermalReading("\\_TZ.TZ00", 41)] };

        var readings = new HardwareThermalReader(hardware, fallback).Read();

        Assert.Equal([new ThermalReading("\\_TZ.TZ00", 41), new ThermalReading("GPU", 55)], readings);
    }

    [Fact]
    public void FailedUpdate_IsNoHardware_SoTheFallbackAnswers()
    {
        var hardware = new FakeHardware { Failure = new InvalidOperationException("driver") };
        var fallback = new FakeFallback { Zones = [new ThermalReading("\\_TZ.TZ00", 41)] };

        var readings = new HardwareThermalReader(hardware, fallback).Read();

        Assert.Equal([new ThermalReading("\\_TZ.TZ00", 41)], readings);
    }

    [Fact]
    public void FallbackThatFails_Throws()
    {
        var reader = new HardwareThermalReader(new NoHardwareSource(), new ThrowingFallback());

        Assert.Throws<InvalidOperationException>(() => reader.Read());
    }

    [Fact]
    public void Dispose_ClosesTheHardware()
    {
        var hardware = new FakeHardware();

        new HardwareThermalReader(hardware, new FakeFallback()).Dispose();

        Assert.Equal(1, hardware.Disposed);
    }
}
