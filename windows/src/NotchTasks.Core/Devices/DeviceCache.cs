namespace NotchTasks.Core.Devices;

/// The cache decision of the device reader: whether a container's kind must be read again from its device nodes. A container with no
/// kind is read again while its nodes have no class yet (they may be enumerated after the container appeared, a device plugged in), or
/// within the 30 s after its Added event (the hot-plug window). A kind that is known is kept, and only its battery is read again.
public static class DeviceCache
{
    /// The window after a container's Added event in which an empty kind is read again.
    public static readonly TimeSpan HotPlugWindow = TimeSpan.FromSeconds(30);

    /// True when the container's kind must be read from its nodes: no known fact, or a known fact with no categories whose nodes have
    /// no class yet or that was added within the hot-plug window.
    public static bool NeedsClassify(DeviceFact? known, bool addedRecently = false)
    {
        if (known is null)
        {
            return true;
        }
        if (known.Categories.Count > 0)
        {
            return false;
        }
        return !known.HasClassNode || addedRecently;
    }
}
