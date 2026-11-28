using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The thermal zones from WMI (root\WMI, MSAcpi_ThermalZoneTemperature). Needs administrator. A failed query gives no zones (it is
/// reported once, see HelperLog): on boards whose ACPI zones answer "Not supported" the helper reads nothing from here and the other reader
/// answers. An empty result means the PC reports no zones.
internal sealed class WmiThermalReader : IThermalReader
{
    private const string Scope = @"root\WMI";
    private const string Query = "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature";

    private readonly Func<IEnumerable<(string zone, int tenths)>> _query;

    /// The reader over the real WMI query.
    public WmiThermalReader()
        : this(QueryZones)
    {
    }

    /// The reader over a query (the tests pass a fake that throws, or gives zones).
    internal WmiThermalReader(Func<IEnumerable<(string zone, int tenths)>> query)
    {
        _query = query;
    }

    public IReadOnlyList<ThermalReading> Read()
    {
        try
        {
            return Thermals.FromKelvinTenths(_query());
        }
        catch (Exception e) when (e is ManagementException or COMException or TimeoutException or UnauthorizedAccessException
                                  or InvalidCastException or OverflowException or FormatException)
        {
            HelperLog.Once(e);
            return Array.Empty<ThermalReading>();
        }
    }

    /// The zones now. A query that hangs gives up after 2 s (a ManagementException, a timeout).
    private static IEnumerable<(string zone, int tenths)> QueryZones()
    {
        var zones = new List<(string zone, int tenths)>();
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(Scope),
            new ObjectQuery(Query),
            new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(2) });
        using var results = searcher.Get();
        foreach (ManagementObject zone in results)
        {
            using (zone)
            {
                if (zone["CurrentTemperature"] is not { } raw)
                {
                    continue;
                }
                var name = zone["InstanceName"] as string ?? "ACPI";
                var tenths = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                zones.Add((name, (int)Math.Clamp(tenths, 0, int.MaxValue)));
            }
        }
        return zones;
    }
}
