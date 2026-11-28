using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using NotchTasks.Core.Settings;

namespace NotchTasks.Services;

/// Where the temperature helper stands. Off: not wanted. Starting: asked, not answered yet. On: answering. Declined: the user
/// cancelled the UAC prompt (never asked again by itself). Unavailable: the PC reports no thermal zone. Paused: wanted, but not
/// started at this app start (it needs a UAC click, so the app waits for one). Failed: Turn on started the helper, and it neither
/// answered nor kept running (the log says why); the row offers Try again.
public enum HelperState
{
    Off,
    Starting,
    On,
    Declined,
    Unavailable,
    Paused,
    Failed,
}

/// Starts a program elevated (UAC), or fails with Win32Exception 1223 when the prompt is cancelled. Only the user's clicks in the
/// Permissions pane reach it, through SensorHelper.TurnOnAsync and TurnOffAsync. Tests pass a fake. The UAC prompt is modal, so
/// the real launcher starts the program off the UI thread and the caller awaits it.
public interface IProcessLauncher
{
    Task<Process?> RunAsAsync(string fileName, string arguments);
}

/// The real launcher: ShellExecute with the runas verb, so Windows shows the UAC prompt. Process.Start runs on a thread-pool thread,
/// so the UI thread is not blocked while the prompt is up; a cancelled prompt surfaces as Win32Exception 1223 through the await.
public sealed class ElevatedLauncher : IProcessLauncher
{
    public Task<Process?> RunAsAsync(string fileName, string arguments) => Task.Run<Process?>(() => Process.Start(new ProcessStartInfo(fileName, arguments)
    {
        Verb = "runas",
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    }));
}

/// One request to the helper's pipe. Null when no answer came (the helper is not running, or the read failed).
public interface ISensorPipe
{
    Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct);
}

/// The real pipe client: connects within 500 ms, and the whole exchange gives up after 2 s. It asks with the identification level, so
/// the server can learn who asked but cannot act as the user.
public sealed class PipeSensorPipe : ISensorPipe
{
    private const int ConnectMilliseconds = 500;
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2);
    private readonly string _sid;

    public PipeSensorPipe(string sid)
    {
        _sid = sid;
    }

    public async Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct)
    {
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(ReplyTimeout);
            using var pipe = new NamedPipeClientStream(".", SensorNames.PipeName(_sid), PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(ConnectMilliseconds, deadline.Token);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(SensorProtocol.ReadCommand + "\n"), deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            var line = await SensorProtocol.ReadLineAsync(pipe, SensorProtocol.MaxReplyBytes, deadline.Token);
            return line is null ? null : ThermalWire.Parse(line);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or Win32Exception
                                  or OperationCanceledException)
        {
            return null;
        }
    }
}

/// Asks the helper to stop: sets the user's stop event. Needs no UAC, because the event's ACL lets the user set it.
public interface IHelperStop
{
    /// True when a helper was there to be asked.
    bool Signal();
}

public sealed class EventHelperStop : IHelperStop
{
    private readonly string _name;

    public EventHelperStop(string sid)
    {
        _name = SensorNames.StopEventName(sid);
    }

    public bool Signal()
    {
        try
        {
            if (!EventWaitHandleAcl.TryOpenExisting(_name, EventWaitHandleRights.Modify, out var stop))
            {
                return false;
            }
            using (stop)
            {
                stop.Set();
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// The temperature helper on the app's side (Windows only). The helper is an elevated exe that reads WMI's thermal zones. It is
/// off by default. Only TurnOnAsync and TurnOffAsync start it, each from a user's click (one UAC prompt each); Start, ReadAsync and
/// a cancelled prompt never do. Declined stays declined until the user clicks again.
///
/// Whether this user has a sign-in helper is local truth: the marker file in the shared Program Files folder. The synced settings
/// are not trusted for that. Turning off always looks at the marker, so an orphaned task can always be removed.
public sealed class SensorHelper
{
    private static readonly TimeSpan StartEvery = TimeSpan.FromSeconds(2);
    private const int StartTries = 5;
    private static readonly TimeSpan AnswerEvery = TimeSpan.FromMilliseconds(500);
    private const int AnswerTries = 90;  // 45 s: the install (copy, task, start) takes a while

    private readonly SettingsStore _settings;
    private readonly IProcessLauncher _launcher;
    private readonly ISensorPipe _pipe;
    private readonly IHelperStop _stop;
    private readonly string _helperExe;
    private readonly string _sid;
    private readonly int _appPid;
    private readonly Func<string, bool> _fileExists;
    private bool _busy;

    /// The helper for this user. helperExe is the helper next to the app; sid names the pipe, the stop event and the marker; appPid
    /// is the --parent the helper watches. fileExists tells whether a file is there (tests pass a fake; null is File.Exists).
    public SensorHelper(SettingsStore settings, IProcessLauncher launcher, ISensorPipe pipe, IHelperStop stop,
        string helperExe, string sid, int appPid, Func<string, bool>? fileExists = null)
    {
        _settings = settings;
        _launcher = launcher;
        _pipe = pipe;
        _stop = stop;
        _helperExe = helperExe;
        _sid = sid;
        _appPid = appPid;
        _fileExists = fileExists ?? File.Exists;
    }

    /// Raised on the UI thread when State changes.
    public event Action? StateChanged;

    public HelperState State { get; private set; } = HelperState.Off;

    /// True when this user's sign-in helper is installed: its marker is in the Program Files folder. Read on every call.
    public bool HasSignInTask => _fileExists(SensorNames.MarkerPath(_sid));

    /// The helper for the current user, with the real launcher, pipe and stop event.
    public static SensorHelper Create(SettingsStore settings)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The user SID is not known.");
        return new SensorHelper(settings, new ElevatedLauncher(), new PipeSensorPipe(sid), new EventHelperStop(sid),
            AppFiles.SensorExe, sid, Environment.ProcessId);
    }

    /// At the app's start. Off when the feature is off. With the marker present and the feature on, the logon helper is probed
    /// through the pipe (nothing is started). Without the marker, the synced sign-in setting only chooses Off or Paused: never a launch.
    public void Start()
    {
        if (HasSignInTask && _settings.SensorHelper)
        {
            SetState(HelperState.Starting);
            _ = ProbeAtStartAsync();
            return;
        }
        if (!_settings.SensorHelper)
        {
            SetState(HelperState.Off);
            return;
        }
        SetState(_settings.SensorHelperAtSignIn ? HelperState.Off : HelperState.Paused);
    }

    /// Turns the feature on, from the user's click. atSignIn adds the logon task (one UAC prompt for both). A cancelled prompt
    /// is Declined: the setting goes false, and nothing is retried. Waits up to 45 s for the helper to answer. After a timeout the
    /// marker is checked before anything is cleared: an installed task is kept, shown as Paused with Try again. A helper that exits
    /// before answering ends the wait at once and is Failed (see NoAnswer).
    public async Task TurnOnAsync(bool atSignIn)
    {
        if (_busy || State is HelperState.On or HelperState.Starting)
        {
            return;
        }
        _busy = true;
        try
        {
            SaveFlag("sensorHelper", true);
            SaveFlag("sensorHelperAtSignIn", atSignIn);
            SetState(HelperState.Starting);

            var arguments = atSignIn
                ? SensorArgs.InstallLogon(_sid, _appPid)
                : SensorArgs.Serve(_sid, _appPid);
            Process? started;
            try
            {
                started = await _launcher.RunAsAsync(_helperExe, arguments);
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            {
                // ERROR_CANCELLED: the user said no to UAC. Declined, and nothing starts it again by itself.
                ClearSettings();
                SetState(HelperState.Declined);
                return;
            }
            catch (Win32Exception e)
            {
                ErrorLog.Write(e);
                ClearSettings();
                SetState(HelperState.Off);
                return;
            }

            // The started process is kept until the answer or the timeout: its exit code says why no answer came.
            try
            {
                var answer = await WaitForAnswerAsync(started);
                if (answer is null)
                {
                    NoAnswer(started);
                    return;
                }
                Settle(answer, fromUser: true);
            }
            finally
            {
                started?.Dispose();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    /// The helper gave no answer. A process that already exited is logged with its exit code, and a process still running is logged as
    /// slow. The failure is not a silent Off: the row says the helper did not start, with Try again. The settings are cleared, unless
    /// the logon task is installed and the helper is only slow (then it is kept, shown as Paused with Try again and Turn off).
    private void NoAnswer(Process? started)
    {
        var exited = started is not null && started.HasExited;
        if (exited)
        {
            ErrorLog.Write(new InvalidOperationException($"NotchTasks: the temperature helper exited with code {started!.ExitCode} before answering."));
        }
        else
        {
            ErrorLog.Write(new InvalidOperationException("NotchTasks: the temperature helper did not answer in 45 s."));
        }
        if (!exited && HasSignInTask)
        {
            SetState(HelperState.Paused);  // installed but slow to answer: keep it, the row offers Try again and Turn off
            return;
        }
        ClearSettings();
        SetState(HelperState.Failed);
    }

    /// Turns the feature off, from the user's click. When this user's marker exists, one UAC prompt removes the task, the marker
    /// and the copy (when no other user needs it). A cancelled prompt, or a failed removal, changes nothing. Without the marker, the
    /// helper is only asked to stop, with no prompt.
    public async Task TurnOffAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            if (HasSignInTask)
            {
                try
                {
                    using var uninstall = await _launcher.RunAsAsync(_helperExe, SensorArgs.UninstallLogonArgs(_sid));
                    if (uninstall is null)
                    {
                        ErrorLog.Write(new InvalidOperationException("NotchTasks: the sensor helper uninstall did not start."));
                        return;
                    }
                    await uninstall.WaitForExitAsync();
                    if (uninstall.ExitCode != 0)
                    {
                        ErrorLog.Write(new InvalidOperationException($"NotchTasks: the sensor helper uninstall returned {uninstall.ExitCode}."));
                        return;  // the task may remain: nothing is changed, the row still offers Turn off
                    }
                }
                catch (Win32Exception e) when (e.NativeErrorCode == 1223)
                {
                    return;  // the user said no to UAC: nothing changes
                }
                catch (Win32Exception e)
                {
                    ErrorLog.Write(e);
                    return;
                }
            }

            _stop.Signal();
            ClearSettings();
            SetState(HelperState.Off);
        }
        finally
        {
            _busy = false;
        }
    }

    /// The zones the helper reports now. Empty when the helper is not On or Starting (no pipe is opened then), or when the read fails.
    public async Task<IReadOnlyList<ThermalReading>> ReadAsync()
    {
        if (State is not (HelperState.On or HelperState.Starting))
        {
            return Array.Empty<ThermalReading>();
        }
        return await _pipe.QueryAsync(CancellationToken.None) ?? Array.Empty<ThermalReading>();
    }

    /// The probe at the app's start, with the marker present: a few quick tries, since the logon helper may still be starting.
    private async Task ProbeAtStartAsync()
    {
        for (var attempt = 0; attempt < StartTries; attempt++)
        {
            var answer = await _pipe.QueryAsync(CancellationToken.None);
            if (State != HelperState.Starting)
            {
                return;  // the user acted meanwhile
            }
            if (answer is not null)
            {
                Settle(answer, fromUser: false);
                return;
            }
            await Task.Delay(StartEvery);
        }
        if (State == HelperState.Starting)
        {
            SetState(HelperState.Off);  // not running: Turn on starts it
        }
    }

    /// Waits for the helper's first answer after a start (through the UAC prompt and the install). The wait ends at once when the
    /// started process has exited: the helper runs until it stops, so an exit means no answer is coming.
    private async Task<IReadOnlyList<ThermalReading>?> WaitForAnswerAsync(Process? started)
    {
        for (var attempt = 0; attempt < AnswerTries; attempt++)
        {
            var answer = await _pipe.QueryAsync(CancellationToken.None);
            if (answer is not null)
            {
                return answer;
            }
            if (started is not null && started.HasExited)
            {
                return null;
            }
            await Task.Delay(AnswerEvery);
        }
        return null;
    }

    /// An answer decides: zones make it On; no zones make it Unavailable. Only the user's click (fromUser) may change the settings:
    /// there, no zones stop the helper and clear the setting. The start-up probe (fromUser false) never writes a flag and never
    /// signals the helper; it only shows Unavailable.
    private void Settle(IReadOnlyList<ThermalReading> answer, bool fromUser)
    {
        if (answer.Count == 0)
        {
            if (fromUser)
            {
                _stop.Signal();
                ClearSettings();
            }
            SetState(HelperState.Unavailable);
            return;
        }
        SetState(HelperState.On);
    }

    private void ClearSettings()
    {
        SaveFlag("sensorHelper", false);
        SaveFlag("sensorHelperAtSignIn", false);
    }

    /// Saves a flag. A failed save is logged; the state still follows the answer.
    private void SaveFlag(string key, bool value)
    {
        try
        {
            _settings.Set(key, System.Text.Json.Nodes.JsonValue.Create(value));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException($"NotchTasks: could not save {key}", e));
        }
    }

    private void SetState(HelperState state)
    {
        if (State == state)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke();
    }
}
