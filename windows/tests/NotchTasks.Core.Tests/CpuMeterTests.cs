using NotchTasks.Core.Agents;
using Xunit;

namespace NotchTasks.Core.Tests;

// Ported from electron/test/linux-proc.test.js (CpuMeter): percent between two samples, and pid reuse.
public sealed class CpuMeterTests
{
    [Fact]
    public void CpuMeter_TwoSamples()
    {
        var meter = new CpuMeter();
        meter.Sample(new ProcSnapshot(new[] { new ProcInfo(7, 1, "node.exe", 100, 0) }), 0);
        // 0.5 s of CPU (5_000_000 ticks of 100 ns) over 1 s of wall time
        meter.Sample(new ProcSnapshot(new[] { new ProcInfo(7, 1, "node.exe", 100, 5_000_000) }), 1000);
        Assert.Equal(50.0, meter.Percent(7), 6);
    }

    [Fact]
    public void PidReused_StartsAtZero()
    {
        var meter = new CpuMeter();
        meter.Sample(new ProcSnapshot(new[] { new ProcInfo(7, 1, "node.exe", 100, 0) }), 0);
        meter.Sample(new ProcSnapshot(new[] { new ProcInfo(7, 1, "other.exe", 200, 5_000_000) }), 1000);
        Assert.Equal(0.0, meter.Percent(7), 6);
    }

    [Fact]
    public void CpuMeter_FirstSample_IsZero()
    {
        var meter = new CpuMeter();
        meter.Sample(new ProcSnapshot(new[] { new ProcInfo(7, 1, "node.exe", 100, 5_000_000) }), 0);
        Assert.Equal(0.0, meter.Percent(7), 6);
        Assert.Equal(0.0, meter.Percent(12345), 6);
    }
}
