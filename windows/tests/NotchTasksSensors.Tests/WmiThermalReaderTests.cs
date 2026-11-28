using System.Management;
using NotchTasks.Core.Modules;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The WMI zones with a fake query: a failing query (a "Not supported" answer, a timeout, a COM error) gives no zones and is reported
/// once; a working query gives its zones in degrees.
public sealed class WmiThermalReaderTests
{
    private sealed class NoSink : IFailureSink
    {
        public void Report(Exception error, int eventId)
        {
        }
    }

    public WmiThermalReaderTests()
    {
        HelperLog.Sink = new NoSink();
    }

    [Fact]
    public void NotSupportedAnswer_GivesNoZones()
    {
        var reader = new WmiThermalReader(() => throw new ManagementException("Not supported"));

        Assert.Empty(reader.Read());
    }

    [Fact]
    public void TimedOutQuery_GivesNoZones()
    {
        var reader = new WmiThermalReader(() => throw new ManagementException("Timeout"));

        Assert.Empty(reader.Read());
    }

    [Fact]
    public void ComError_GivesNoZones()
    {
        var reader = new WmiThermalReader(() => throw new System.Runtime.InteropServices.COMException("RPC unavailable"));

        Assert.Empty(reader.Read());
    }

    [Fact]
    public void WorkingQuery_GivesTheZonesInDegrees()
    {
        // 3010 tenths of a kelvin is 27.85 C.
        var reader = new WmiThermalReader(() => [("\\_TZ.TZ00", 3010)]);

        var zones = reader.Read();

        Assert.Equal([new ThermalReading("\\_TZ.TZ00", 27.85)], zones);
    }
}
