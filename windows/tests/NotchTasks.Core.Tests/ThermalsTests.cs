using NotchTasks.Core.Modules;
using Xunit;

namespace NotchTasks.Core.Tests;

public sealed class ThermalsTests
{
    [Fact]
    public void Hottest_DropsImplausible()
    {
        // 130 C and 0 C are outside 5..120 and are dropped; 61.5 wins over 48.
        var hottest = Thermals.Hottest(new[]
        {
            new ThermalReading("hot", 130),
            new ThermalReading("cold", 0),
            new ThermalReading("low", 48),
            new ThermalReading("cpu", 61.5),
        });

        Assert.NotNull(hottest);
        Assert.Equal("cpu", hottest.Value.Name);
        Assert.Equal(61.5, hottest.Value.Celsius);
    }

    [Fact]
    public void Hottest_NoPlausibleReading_IsNull()
    {
        Assert.Null(Thermals.Hottest(new[] { new ThermalReading("a", 4.9), new ThermalReading("b", 120.1) }));
        Assert.Null(Thermals.Hottest(Array.Empty<ThermalReading>()));
    }

    [Fact]
    public void Plausible_DropsImplausible_AndListsTheHottestFirst()
    {
        var sorted = Thermals.Plausible(new[]
        {
            new ThermalReading("hot", 130),
            new ThermalReading("low", 48),
            new ThermalReading("cpu", 61.5),
            new ThermalReading("cold", 4.9),
            new ThermalReading("edge", 120),
        });

        Assert.Equal(new[] { "edge", "cpu", "low" }, sorted.Select(reading => reading.Name));
    }

    [Fact]
    public void Tint_Thresholds()
    {
        // Green below 60, orange below 85, red from 85 (NotchView.swift).
        Assert.Equal("#30D158", Thermals.Tint(59.9));
        Assert.Equal("#FF9F0A", Thermals.Tint(60));
        Assert.Equal("#FF9F0A", Thermals.Tint(84.9));
        Assert.Equal("#FF453A", Thermals.Tint(85));
    }

    [Fact]
    public void FromKelvinTenths_Converts()
    {
        // WMI reports tenths of a kelvin: (3332 - 2731.5) / 10 = 60.05 C.
        var readings = Thermals.FromKelvinTenths(new[] { ("TZ00", 3332) });

        Assert.Single(readings);
        Assert.Equal("TZ00", readings[0].Name);
        Assert.Equal(60.05, readings[0].Celsius, 6);
    }
}
