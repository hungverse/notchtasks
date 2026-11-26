namespace NotchTasks.Core.Modules;

/// One temperature reading: the thermal zone's name and its temperature in degrees Celsius.
public readonly record struct ThermalReading(string Name, double Celsius);

/// The temperature rules of the notch (Mac: Thermals in NotchView.swift). The header shows the hottest plausible reading, in
/// a tint that says how hot it is. Pure: no OS calls. The WMI values come in through FromKelvinTenths.
public static class Thermals
{
    private const double MinCelsius = 5;
    private const double MaxCelsius = 120;

    /// The hottest reading between 5 and 120 C (both included). Readings outside that range are dropped as implausible.
    /// Null when none is left.
    public static ThermalReading? Hottest(IEnumerable<ThermalReading> readings)
    {
        ThermalReading? hottest = null;
        foreach (var reading in readings)
        {
            if (!IsPlausible(reading))
            {
                continue;
            }
            if (hottest is not { } current || reading.Celsius > current.Celsius)
            {
                hottest = reading;
            }
        }
        return hottest;
    }

    /// The plausible readings (see Hottest), the hottest first, ties by name. The Temperatures pane lists them.
    public static IReadOnlyList<ThermalReading> Plausible(IEnumerable<ThermalReading> readings) =>
        readings.Where(IsPlausible)
            .OrderByDescending(reading => reading.Celsius)
            .ThenBy(reading => reading.Name, StringComparer.Ordinal)
            .ToList();

    private static bool IsPlausible(ThermalReading reading) =>
        !double.IsNaN(reading.Celsius) && reading.Celsius >= MinCelsius && reading.Celsius <= MaxCelsius;

    /// The header's tint for a temperature: green below 60 C, orange below 85 C, red from 85 C.
    public static string Tint(double celsius) =>
        celsius < 60 ? "#30D158" : celsius < 85 ? "#FF9F0A" : "#FF453A";

    /// WMI's CurrentTemperature (MSAcpi_ThermalZoneTemperature) is in tenths of a kelvin: (tenths - 2731.5) / 10 C.
    public static IReadOnlyList<ThermalReading> FromKelvinTenths(IEnumerable<(string zone, int tenths)> zones) =>
        zones.Select(zone => new ThermalReading(zone.zone, (zone.tenths - 2731.5) / 10.0)).ToList();
}
