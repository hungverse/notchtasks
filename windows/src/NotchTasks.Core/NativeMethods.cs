using System.Runtime.InteropServices;

namespace NotchTasks.Core;

/// The Windows calls behind ProcessChecks. LibraryImport generates the marshalling.
internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    /// The full path of the program as UTF-16 in name; size is the buffer length in characters in, the characters written out (no NUL).
    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageNameW(nint process, uint flags, byte[] name, ref uint size);

    /// The system power status: AC line, battery flag and life percent (see PowerFacts.FromStatus).
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(nint process, int informationClass, ref ProcessBasicInformation information, int length, out int returnLength);
}

/// PROCESS_BASIC_INFORMATION: only InheritedFromUniqueProcessId (the parent pid) is read.
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessBasicInformation
{
    public nint Reserved1;
    public nint PebBaseAddress;
    public nint Reserved2;
    public nint Reserved3;
    public nuint UniqueProcessId;
    public nuint InheritedFromUniqueProcessId;
}

/// SYSTEM_POWER_STATUS: the fields PowerFacts reads, in the Windows layout.
[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte ACLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}
