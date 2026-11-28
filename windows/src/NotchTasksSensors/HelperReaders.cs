using System.Runtime.CompilerServices;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// Makes the helper's temperature reader: the hardware library's CPU and GPU readings, with the WMI zones as the fallback.
internal static class HelperReaders
{
    /// The reader over the hardware that open gives, and the fallback. When open fails with an ordinary error (the library cannot open its
    /// sensors), the reader runs with no hardware and the zones answer alone. A missing, unloadable or broken assembly is not ordinary: it is
    /// rethrown, so the helper's reader stage fails and reports it (exit code 11) instead of running without the library.
    public static HardwareThermalReader CreateReader(Func<IHardwareSource> open, IThermalReader fallback)
    {
        IHardwareSource hardware;
        try
        {
            hardware = open();
        }
        catch (Exception e) when (!IsLoadFailure(e))
        {
            HelperLog.Once(e);
            hardware = new NoHardwareSource();
        }
        return new HardwareThermalReader(hardware, fallback);
    }

    /// True for a missing, unloadable or broken assembly. DllNotFoundException is an ordinary failure (a native library the open needs), so
    /// it is not one: it derives from TypeLoadException, and only the exact type counts.
    internal static bool IsLoadFailure(Exception e) =>
        e is FileNotFoundException or FileLoadException or BadImageFormatException
        || e.GetType() == typeof(TypeLoadException);

    /// The real hardware: LibreHardwareMonitor's Computer. The only place that names the library's types is LibreHardwareSource, and this
    /// call is not inlined, so the library's loading happens inside the caller's try, not while the caller is compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IHardwareSource OpenLibreHardware() => new LibreHardwareSource();
}
