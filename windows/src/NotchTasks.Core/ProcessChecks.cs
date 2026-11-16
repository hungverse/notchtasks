using System.Runtime.InteropServices;

namespace NotchTasks.Core;

/// Process checks the hook and the app need: is a pid alive, and who started this process.
/// The Windows calls are in NativeMethods (LibraryImport, so AOT-safe: no Process class, no reflection).
public static class ProcessChecks
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private const int ErrorAccessDenied = 5;

    /// True when the process exists. A process we may not open (another user's, a protected one) counts as alive,
    /// the way EPERM does in the Electron build; a finished process does not, even while a handle to it is open.
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == 0)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied;
        }
        try
        {
            return NativeMethods.GetExitCodeProcess(handle, out var code) && code == StillActive;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// The pid of the process that started this one, or null when it cannot be read.
    public static int? CurrentParentId()
    {
        var info = new ProcessBasicInformation();
        // -1 is the pseudo handle of this process: always openable with full access.
        var status = NativeMethods.NtQueryInformationProcess(new nint(-1), 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _);
        return status == 0 && info.InheritedFromUniqueProcessId != 0 ? (int)info.InheritedFromUniqueProcessId : null;
    }
}
