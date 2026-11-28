using NotchTasks.Core.Modules;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The fixed-value rule for the counter's thermal zones (a zone that never moves is a BIOS value), and the header's headline reading.
public sealed class FixedZoneRuleTests
{
    private const string Zone = "\\_TZ.TZ00";

    private static ThermalReading At(double celsius) => new(Zone, celsius);

    [Fact]
    public void TenEqualReads_AreDropped()
    {
        var rule = new FixedZoneRule();

        for (var read = 1; read <= 10; read++)
        {
            Assert.Empty(rule.Apply([At(28)]));
        }

        Assert.Equal(FixedZoneRule.Verdict.Fixed, rule.VerdictOf(Zone));
    }

    [Fact]
    public void FewerThanTenReads_AreNotYetShown()
    {
        var rule = new FixedZoneRule();

        for (var read = 1; read <= 9; read++)
        {
            Assert.Empty(rule.Apply([At(28)]));
        }

        Assert.Equal(FixedZoneRule.Verdict.Unknown, rule.VerdictOf(Zone));
    }

    [Fact]
    public void AChangeWithinTheFirstTen_KeepsTheZone()
    {
        var rule = new FixedZoneRule();
        rule.Apply([At(28)]);
        rule.Apply([At(28)]);

        var shown = rule.Apply([At(31.5)]);

        Assert.Equal([At(31.5)], shown);
        Assert.Equal(FixedZoneRule.Verdict.Live, rule.VerdictOf(Zone));
    }

    [Fact]
    public void ALiveZone_StaysShownWhenItsValueRepeats()
    {
        var rule = new FixedZoneRule();
        rule.Apply([At(28)]);
        rule.Apply([At(30)]);

        for (var read = 1; read <= 12; read++)
        {
            Assert.Equal([At(30)], rule.Apply([At(30)]));
        }
    }

    [Fact]
    public void ZonesAreJudgedSeparately()
    {
        var rule = new FixedZoneRule();
        var fixedZone = new ThermalReading("\\_TZ.TZ00", 28);
        for (var read = 1; read <= 10; read++)
        {
            rule.Apply([fixedZone, new ThermalReading("\\_TZ.TZ01", 40 + read % 2)]);
        }

        var shown = rule.Apply([fixedZone, new ThermalReading("\\_TZ.TZ01", 41)]);

        Assert.Equal([new ThermalReading("\\_TZ.TZ01", 41)], shown);
        Assert.Equal(FixedZoneRule.Verdict.Fixed, rule.VerdictOf(fixedZone.Name));
    }

    [Fact]
    public void AZoneMissingFromARead_IsNotCounted()
    {
        var rule = new FixedZoneRule();
        for (var read = 1; read <= 9; read++)
        {
            rule.Apply([At(28)]);
        }

        rule.Apply([]);  // a read that gave no zones

        Assert.Equal(FixedZoneRule.Verdict.Unknown, rule.VerdictOf(Zone));
        Assert.Empty(rule.Apply([At(28)]));
        Assert.Equal(FixedZoneRule.Verdict.Fixed, rule.VerdictOf(Zone));
    }

    [Fact]
    public void Headline_PrefersTheCpuReading()
    {
        var readings = new[] { new ThermalReading("GPU", 70), new ThermalReading("CPU", 52), new ThermalReading("Board", 60) };

        Assert.Equal(new ThermalReading("CPU", 52), Thermals.Headline(readings));
    }

    [Fact]
    public void Headline_WithoutAPlausibleCpu_IsTheHottest()
    {
        var readings = new[] { new ThermalReading("CPU", 300), new ThermalReading("Board", 60), new ThermalReading("GPU", 70) };

        Assert.Equal(new ThermalReading("GPU", 70), Thermals.Headline(readings));
        Assert.Null(Thermals.Headline(Array.Empty<ThermalReading>()));
    }
}
