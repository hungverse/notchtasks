namespace NotchTasks.Core.Modules;

/// The fixed-value rule for the thermal zones the app reads without administrator. Some boards publish a thermal zone whose value never
/// moves (the ACPI zone of this PC reads 3010 tenths of a kelvin, 28 C, on every read): that is a BIOS value, not the CPU. A zone is
/// shown once it has changed (at any read); a zone that stays on one value for its first Reads reads is fixed, and it is not shown; a
/// zone that has not yet had Reads reads is not shown either. Pure: the caller counts the reads, one Apply per read of the zones.
public sealed class FixedZoneRule
{
    /// The reads a zone must hold one value for before it is taken as fixed.
    public const int Reads = 10;

    private readonly Dictionary<string, Zone> _zones = new(StringComparer.Ordinal);

    /// What the rule knows about one zone.
    public enum Verdict
    {
        /// Fewer than Reads reads, and no change yet: not shown yet.
        Unknown,

        /// Reads Reads times on one value: a fixed BIOS value, dropped.
        Fixed,

        /// It changed: a live reading, shown.
        Live,
    }

    private sealed class Zone
    {
        public required double First { get; init; }
        public int Count { get; set; }
        public bool Changed { get; set; }
    }

    /// Counts one read of the zones in readings and returns the zones to show (the live ones). A zone missing from a read is not counted.
    public IReadOnlyList<ThermalReading> Apply(IEnumerable<ThermalReading> readings)
    {
        var read = readings.ToList();
        foreach (var reading in read)
        {
            if (!_zones.TryGetValue(reading.Name, out var zone))
            {
                zone = new Zone { First = reading.Celsius };
                _zones[reading.Name] = zone;
            }
            zone.Count++;
            if (reading.Celsius != zone.First)
            {
                zone.Changed = true;
            }
        }

        // The live zones, with the value of this read.
        return read.Where(reading => _zones[reading.Name].Changed).ToList();
    }

    /// What the rule knows about a zone by its name.
    public Verdict VerdictOf(string name)
    {
        if (!_zones.TryGetValue(name, out var zone))
        {
            return Verdict.Unknown;
        }
        if (zone.Changed)
        {
            return Verdict.Live;
        }
        return zone.Count >= Reads ? Verdict.Fixed : Verdict.Unknown;
    }
}
