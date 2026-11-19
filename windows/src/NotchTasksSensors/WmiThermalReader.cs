using System.Globalization;
using System.Management;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The thermal zones from WMI (root\WMI, MSAcpi_ThermalZoneTemperature). Needs administrator. A failed query throws, so the
/// helper answers nothing; an empty result means the PC reports no zones.
internal sealed class WmiThermalReader : IThermalReader
{
    private const string Scope = @"root\WMI";
    private const string Query = "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature";

    public IReadOnlyList<ThermalReading> Read()
    {
        var zones = new List<(string zone, int tenths)>();
        // A query that hangs gives up after 2 s: the helper then answers nothing, and the app keeps what it has.
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
        return Thermals.FromKelvinTenths(zones);
    }
}
