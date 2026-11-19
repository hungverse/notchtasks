using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// Watches the sensor state for a change. The source makes one at Start and stops it at the last Stop; a test puts a fake in
/// its place, so no test waits on the real registry.
public interface ISensorWatch
{
    /// Stops watching. Returns once the watch is stopped.
    void Stop();
}

/// The registry watch: a background thread waits on RegNotifyChangeKeyValue for the mic and camera consent keys (their
/// subtrees: an app's last-used times live below the key), and calls onChange on that thread for each change. Nothing polls:
/// the thread sleeps in WaitAny until a key changes or Stop is called. A key that is missing is not watched.
internal sealed class RegistryWatch : ISensorWatch
{
    private const uint KeyNotify = 0x0010;
    private const uint KeyRead = 0x20019;
    private const uint RegNotifyChangeName = 0x0001;
    private const uint RegNotifyChangeLastSet = 0x0004;
    private static readonly nint HkeyCurrentUser = unchecked((nint)(int)0x80000001);
    private static readonly string[] Keys = { ConsentStore.MicKey, ConsentStore.CameraKey };
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly Action _onChange;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;

    /// Starts the thread. onChange runs on it, once for each change.
    public RegistryWatch(Action onChange)
    {
        _onChange = onChange;
        _thread = new Thread(Run) { IsBackground = true, Name = "NotchTasks sensors" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Set();
        // The thread closes its own keys and events when it leaves. The stop event is disposed only once the thread has
        // gone: a thread still in WaitAny would otherwise wait on a disposed handle.
        if (_thread.Join(JoinTimeout))
        {
            _stop.Dispose();
        }
    }

    private void Run()
    {
        var keys = new List<SafeRegistryHandle>();
        var events = new List<AutoResetEvent>();
        try
        {
            foreach (var path in Keys)
            {
                if (RegistryNative.RegOpenKeyEx(HkeyCurrentUser, path, 0, KeyNotify | KeyRead, out var raw) != 0)
                {
                    continue;  // a missing key: the other one is still watched
                }
                keys.Add(new SafeRegistryHandle(raw, ownsHandle: true));
                events.Add(new AutoResetEvent(false));
            }
            if (keys.Count == 0)
            {
                return;
            }

            var waits = new WaitHandle[events.Count + 1];
            for (var i = 0; i < events.Count; i++)
            {
                waits[i] = events[i];
                Arm(keys[i], events[i]);
            }
            waits[^1] = _stop;

            while (true)
            {
                var index = WaitHandle.WaitAny(waits);
                if (index == events.Count)
                {
                    return;  // the stop event
                }
                Arm(keys[index], events[index]);  // a notification is one-shot: arm it again before reading
                _onChange();
            }
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        finally
        {
            foreach (var key in keys)
            {
                key.Dispose();
            }
            foreach (var evt in events)
            {
                evt.Dispose();
            }
        }
    }

    /// Asks for one notification on the key: a change to a value or a subkey name, anywhere below the key, signals the event.
    private static void Arm(SafeRegistryHandle key, AutoResetEvent evt) =>
        RegistryNative.RegNotifyChangeKeyValue(
            key.DangerousGetHandle(),
            watchSubtree: true,
            RegNotifyChangeLastSet | RegNotifyChangeName,
            evt.SafeWaitHandle.DangerousGetHandle(),
            asynchronous: true);
}

/// The registry calls the watch needs. The handles are raw; the watch wraps the open key in a SafeRegistryHandle.
internal static partial class RegistryNative
{
    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int RegOpenKeyEx(nint hKey, string subKey, uint options, uint samDesired, out nint result);

    [LibraryImport("advapi32.dll")]
    public static partial int RegNotifyChangeKeyValue(
        nint hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        nint hEvent,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}
