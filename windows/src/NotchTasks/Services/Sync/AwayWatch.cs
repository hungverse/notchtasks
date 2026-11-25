using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NotchTasks.Services.Sync;

/// Whether the user is away (sync.js "away", the Mac's syncOnlyAway test): the session is locked, or no input for 120 s.
public sealed class AwayWatch : IDisposable
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromSeconds(120);

    private readonly Func<TimeSpan> _idle;
    private readonly bool _subscribed;
    private volatile bool _locked;

    /// The app's watch: the session events and GetLastInputInfo.
    public AwayWatch()
        : this(SystemIdle)
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        _subscribed = true;
    }

    /// A watch with a fake idle reading and no session events (the tests).
    internal AwayWatch(Func<TimeSpan> idle) => _idle = idle;

    public bool IsAway() => _locked || _idle() >= IdleLimit;

    /// Set by the session event; the tests set it directly.
    internal void SetLocked(bool locked) => _locked = locked;

    public void Dispose()
    {
        if (_subscribed)
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            SetLocked(true);
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            SetLocked(false);
        }
    }

    /// Time since the last keyboard or mouse input. Zero when Windows cannot say, so the PC is not taken for away.
    private static TimeSpan SystemIdle()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!AwayNative.GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }
        // Both tick counts wrap at 32 bits (GetTickCount), so the difference is taken in uint.
        var idleMs = unchecked((uint)Environment.TickCount64 - info.Time);
        return TimeSpan.FromMilliseconds(idleMs);
    }

}

/// The user32 call behind AwayWatch.
internal static partial class AwayNative
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetLastInputInfo(ref LastInputInfo info);
}

/// LASTINPUTINFO: the size of the struct and the tick count of the last input.
[StructLayout(LayoutKind.Sequential)]
internal struct LastInputInfo
{
    public uint Size;
    public uint Time;
}
