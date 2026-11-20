namespace NotchTasks.Core.Devices;

/// The cache decision of the device reader: whether a container's kind must be read again from its device nodes. A container that is
/// new, or was not an accessory on the last read, is read again: its nodes may have been enumerated after the container appeared (a device
/// plugged in), so an empty classification is never trusted. Only a container already known as an accessory keeps its kind, and only
/// its battery is read again.
public static class DeviceCache
{
    /// True when the container's kind must be read from its nodes: no known fact, or a known fact with no categories.
    public static bool NeedsClassify(DeviceFact? known) => known is null || known.Categories.Count == 0;
}
