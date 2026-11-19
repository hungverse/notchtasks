using System.Runtime.InteropServices;

namespace NotchTasks.Services;

/// Whether a process id is running: OpenProcess with SYNCHRONIZE, then WaitForSingleObject with no wait (a running process has
/// not signalled yet). A process that cannot be opened (gone, or access denied) is not alive.
internal static partial class PidAlive
{
    private const uint Synchronize = 0x00100000;
    private const uint WaitTimeout = 0x00000102;

    public static bool Alive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        var handle = OpenProcess(Synchronize, false, (uint)pid);
        if (handle == 0)
        {
            return false;
        }
        try
        {
            return WaitForSingleObject(handle, 0) == WaitTimeout;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
