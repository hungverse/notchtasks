using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services;

/// The temperatures the app reads itself, with no administrator and no helper: the ACPI thermal zones that Windows' thermal zone
/// counter reports to a normal user. Tests pass a fake (AppStateOptions.ThermalCounter).
public interface IThermalCounter
{
    /// The zones now (every zone the counter lists, implausible ones included). Empty when the counter gives none, fails, or does
    /// not answer in 2 s. Never throws.
    Task<IReadOnlyList<ThermalReading>> ReadAsync();
}

/// The counter with the fixed-value rule (FixedZoneRule): a zone that holds one value for its first 10 reads is a BIOS value, and is not
/// returned. The header, the heat check and the panes share one instance, so their reads count together.
public sealed class FixedZoneCounter : IThermalCounter
{
    private readonly IThermalCounter _inner;
    private readonly FixedZoneRule _rule = new();

    public FixedZoneCounter(IThermalCounter inner)
    {
        _inner = inner;
    }

    public async Task<IReadOnlyList<ThermalReading>> ReadAsync()
    {
        var zones = await _inner.ReadAsync();
        lock (_rule)
        {
            return _rule.Apply(zones);
        }
    }
}

/// The real counter: Win32_PerfFormattedData_Counters_ThermalZoneInformation (root\cimv2). Its HighPrecisionTemperature is in tenths
/// of a kelvin, the same data as the elevated helper's MSAcpi_ThermalZoneTemperature, and a normal user may read it.
public sealed class WmiThermalCounter : IThermalCounter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private const string Query = "SELECT Name, HighPrecisionTemperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation";

    public async Task<IReadOnlyList<ThermalReading>> ReadAsync()
    {
        var read = Task.Run(Read);
        var done = await Task.WhenAny(read, Task.Delay(Timeout));
        return done == read ? await read : Array.Empty<ThermalReading>();
    }

    /// The counter's zones, read on a thread-pool thread (the query blocks). A failed query gives none.
    private static IReadOnlyList<ThermalReading> Read()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\cimv2", Query) { Options = { Timeout = Timeout } };
            var zones = new List<(string zone, int tenths)>();
            foreach (var item in searcher.Get())
            {
                using (item)
                {
                    if (item["Name"] is string name && item["HighPrecisionTemperature"] is { } tenths)
                    {
                        zones.Add((name, Convert.ToInt32(tenths, CultureInfo.InvariantCulture)));
                    }
                }
            }
            return Thermals.FromKelvinTenths(zones);
        }
        catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException or InvalidCastException
                                  or OverflowException or FormatException)
        {
            return Array.Empty<ThermalReading>();
        }
    }
}
