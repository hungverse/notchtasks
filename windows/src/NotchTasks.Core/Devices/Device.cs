namespace NotchTasks.Core.Devices;

/// A connected accessory as the device watcher reports it: the container id, its name, its device categories
/// (for example "Audio.Headphone") and a battery level in percent, or null when the PC cannot read one.
public sealed record DeviceFact(string Id, string Name, IReadOnlyList<string> Categories, int? Battery)
{
    /// Whether one of the container's device nodes had a class GUID when it was read. A container with no such node has not been
    /// enumerated yet, so an empty kind is read again (DeviceCache.NeedsClassify).
    public bool HasClassNode { get; init; }
}

/// A device row the pill shows. Battery is "N%" or null; Lowest is the level the low warning reads (null when unknown).
public sealed record Device(string Id, string Name, string Symbol, string? Battery, int? Lowest);
