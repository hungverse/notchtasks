namespace NotchTasks.Core.Devices;

/// A connected accessory as the device watcher reports it: the container id, its name, its device categories
/// (for example "Audio.Headphone") and a battery level in percent, or null when the PC cannot read one.
public sealed record DeviceFact(string Id, string Name, IReadOnlyList<string> Categories, int? Battery);

/// A device row the pill shows. Battery is "N%" or null; Lowest is the level the low warning reads (null when unknown).
public sealed record Device(string Id, string Name, string Symbol, string? Battery, int? Lowest);
