using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Tasks;
using NotchTasks.Core.Sensors;

namespace NotchTasksSensors;

/// The serve loop: one named pipe per user, one connection at a time, and the stop event. The pipe is created with
/// FirstPipeInstance, so an elevated helper never joins a pipe someone else created first, and its ACL grants only the user, SYSTEM
/// and Administrators. The loop waits on the pipe with no polling, so an idle helper costs no CPU.
internal static class SensorHost
{
    private const int PipeBufferBytes = 1024;

    /// Serves until the parent process ends (when parentPid is given), or the stop event is set. A second helper for the same user
    /// (the stop event or the pipe is already taken) exits 0 quietly. A failed connection is logged once and the next one is served.
    public static async Task<int> ServeAsync(string sid, int? parentPid, IThermalReader reader)
    {
        using var stop = CreateStopEvent(sid);
        if (stop is null)
        {
            return 0;  // a helper for this user is already running
        }

        using var cts = new CancellationTokenSource();
        WatchStopEvent(stop, cts);
        if (parentPid is { } pid && !WatchParent(pid, cts))
        {
            return 0;  // the app is already gone
        }

        var security = PipeSecurityFor(sid);
        while (!cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(
                    SensorNames.PipeName(sid),
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
                    PipeBufferBytes,
                    PipeBufferBytes,
                    security);
            }
            catch (IOException)
            {
                return 0;  // FirstPipeInstance: this user's pipe is taken by another instance
            }

            using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cts.Token);
                    await SensorProtocol.ServeAsync(pipe, reader, cts.Token);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    HelperLog.Once(e);
                    await Task.Delay(TimeSpan.FromMilliseconds(100));
                }
            }
        }
        return 0;
    }

    /// The per-user stop event the app sets. Created here (never opened), so a second helper cannot take it over. Null when the
    /// event already exists.
    public static EventWaitHandle? CreateStopEvent(string sid)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(sid), EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), EventWaitHandleRights.FullControl, AccessControlType.Allow));

        var stop = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, SensorNames.StopEventName(sid), out var created, security);
        if (created)
        {
            return stop;
        }
        stop.Dispose();
        return null;
    }

    /// The pipe's ACL: the user, SYSTEM and Administrators, read and write. Nobody else.
    public static PipeSecurity PipeSecurityFor(string sid)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    /// Cancels the serve loop when the stop event is set. A thread blocked on the event costs no CPU.
    private static void WatchStopEvent(EventWaitHandle stop, CancellationTokenSource cts)
    {
        var thread = new Thread(() =>
        {
            try
            {
                stop.WaitOne();
                cts.Cancel();
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
            {
                // The loop ended first and disposed the event.
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
    }

    /// Cancels the serve loop when the parent process ends. False when it has already ended.
    private static bool WatchParent(int pid, CancellationTokenSource cts)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var thread = new Thread(() =>
        {
            try
            {
                parent.WaitForExit();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Treated as ended.
            }
            finally
            {
                parent.Dispose();
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The loop ended first.
                }
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return true;
    }
}

/// The helper's own error log: the first failure of a run is written, the rest are not (the helper has no window).
internal static class HelperLog
{
    private static bool _written;

    public static void Once(Exception error)
    {
        if (_written)
        {
            return;
        }
        _written = true;
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "NotchTasksSensors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging never throws.
        }
    }
}
