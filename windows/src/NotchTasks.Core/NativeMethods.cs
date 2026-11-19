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

    /// The full path of a known folder as a UTF-16 string allocated by the shell (free it with CoTaskMemFree). The result is an HRESULT.
    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    /// Frees a string the shell or COM allocated (the path from SHGetKnownFolderPath).
    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint memory);

    /// The idle, kernel and user CPU times since boot, each a FILETIME (100 ns ticks) read as one 64-bit value. Kernel includes idle.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    /// The memory status. status.Length must be set to the struct's size before the call.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// The free bytes available to the caller on the volume of directoryName (the W entry point; the A one is never used).
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalNumberOfBytes, out ulong totalNumberOfFreeBytes);
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

/// MEMORYSTATUSEX: only MemoryLoad (dwMemoryLoad, the percent in use) is read; the rest keeps the Windows layout.
[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint Length;
    public uint MemoryLoad;
    public ulong TotalPhys;
    public ulong AvailPhys;
    public ulong TotalPageFile;
    public ulong AvailPageFile;
    public ulong TotalVirtual;
    public ulong AvailVirtual;
    public ulong AvailExtendedVirtual;
}
