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
/// cancelled the UAC prompt (never asked again by itself). Unavailable: the PC reports no thermal zone. Paused: wanted, but the
/// helper is not started at this app start (it needs a UAC click, so the app waits for one).
public enum HelperState
{
    Off,
    Starting,
    On,
    Declined,
    Unavailable,
    Paused,
}

/// Starts a program elevated (UAC), or fails with Win32Exception 1223 when the prompt is cancelled. Only the user's clicks in the
/// Permissions pane reach it, through SensorHelper.TurnOnAsync and TurnOffAsync. Tests pass a fake.
public interface IProcessLauncher
{
    Process? RunAs(string fileName, string arguments);
}

/// The real launcher: ShellExecute with the runas verb, so Windows shows the UAC prompt.
public sealed class ElevatedLauncher : IProcessLauncher
{
    public Process? RunAs(string fileName, string arguments) => Process.Start(new ProcessStartInfo(fileName, arguments)
    {
        Verb = "runas",
        UseShellExecute = true,
        WindowStyle = ProcessWindowStyle.Hidden,
    });
}

/// One request to the helper's pipe. Null when no answer came (the helper is not running, or the read failed).
public interface ISensorPipe
{
    Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct);
}

/// The real pipe client: connects within 500 ms, sends "read", and reads one reply line.
public sealed class PipeSensorPipe : ISensorPipe
{
    private const int ConnectMilliseconds = 500;
    private readonly string _sid;

    public PipeSensorPipe(string sid)
    {
        _sid = sid;
    }

    public async Task<IReadOnlyList<ThermalReading>?> QueryAsync(CancellationToken ct)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", SensorNames.PipeName(_sid), PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ConnectMilliseconds, ct);
            await pipe.WriteAsync(Encoding.ASCII.GetBytes(SensorProtocol.ReadCommand + "\n"), ct);
            await pipe.FlushAsync(ct);
            var line = await SensorProtocol.ReadLineAsync(pipe, SensorProtocol.MaxReplyBytes, ct);
            return line is null ? null : ThermalWire.Parse(line);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or Win32Exception)
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
public sealed class SensorHelper
{
    private static readonly TimeSpan StartEvery = TimeSpan.FromSeconds(2);
    private const int StartTries = 5;
    private static readonly TimeSpan AnswerEvery = TimeSpan.FromMilliseconds(500);
    private const int AnswerTries = 30;

    private readonly SettingsStore _settings;
    private readonly IProcessLauncher _launcher;
    private readonly ISensorPipe _pipe;
    private readonly IHelperStop _stop;
    private readonly string _helperExe;
    private readonly string _sid;
    private readonly int _appPid;
    private bool _busy;

    /// The helper for this user. helperExe is the helper next to the app; sid names the pipe and the stop event; appPid is the
    /// --parent the helper watches.
    public SensorHelper(SettingsStore settings, IProcessLauncher launcher, ISensorPipe pipe, IHelperStop stop,
        string helperExe, string sid, int appPid)
    {
        _settings = settings;
        _launcher = launcher;
        _pipe = pipe;
        _stop = stop;
        _helperExe = helperExe;
        _sid = sid;
        _appPid = appPid;
    }

    /// Raised on the UI thread when State changes.
    public event Action? StateChanged;

    public HelperState State { get; private set; } = HelperState.Off;

    /// The helper for the current user, with the real launcher, pipe and stop event.
    public static SensorHelper Create(SettingsStore settings)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("The user SID is not known.");
        return new SensorHelper(settings, new ElevatedLauncher(), new PipeSensorPipe(sid), new EventHelperStop(sid),
            AppFiles.SensorExe, sid, Environment.ProcessId);
    }

    /// At the app's start. Off when the feature is off. Paused when it is on but not at sign-in: no UAC is shown, the row waits for
    /// a click. With sign-in on, the logon helper is probed through the pipe, and nothing is started.
    public void Start()
    {
        if (!_settings.SensorHelper)
        {
            SetState(HelperState.Off);
            return;
        }
        if (!_settings.SensorHelperAtSignIn)
        {
            SetState(HelperState.Paused);
            return;
        }
        SetState(HelperState.Starting);
        _ = ProbeAtStartAsync();
    }

    /// Turns the feature on, from the user's click. atSignIn adds the logon task (one UAC prompt for both). A cancelled prompt
    /// is Declined: the setting goes false, and nothing is retried. Waits for the helper to answer; no answer is Off.
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
            try
            {
                using var started = _launcher.RunAs(_helperExe, arguments);
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

            var answer = await WaitForAnswerAsync();
            if (answer is null)
            {
                ClearSettings();
                SetState(HelperState.Off);
                return;
            }
            Settle(answer);
        }
        finally
        {
            _busy = false;
        }
    }

    /// Turns the feature off, from the user's click. With the logon task there is one UAC prompt, which removes the task and its
    /// copy (and stops the helper). A cancelled prompt changes nothing. Without it the helper is only asked to stop.
    public async Task TurnOffAsync()
    {
        if (_busy)
        {
            return;
        }
        _busy = true;
        try
        {
            if (_settings.SensorHelperAtSignIn)
            {
                try
                {
                    using var uninstall = _launcher.RunAs(_helperExe, SensorArgs.UninstallLogonArgs(_sid))
                        ?? throw new InvalidOperationException("The helper did not start to uninstall.");
                    await uninstall.WaitForExitAsync();
                    if (uninstall.ExitCode != 0)
                    {
                        ErrorLog.Write(new InvalidOperationException($"NotchTasks: the sensor helper uninstall returned {uninstall.ExitCode}."));
                        return;  // the logon task may remain: the setting stays, so the row still shows the state
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

    /// The hottest-sample feed's read: the zones the helper reports now. Empty when the helper is not On (no pipe is opened then),
    /// or when the read fails.
    public async Task<IReadOnlyList<ThermalReading>> ReadAsync()
    {
        if (State is not (HelperState.On or HelperState.Starting))
        {
            return Array.Empty<ThermalReading>();
        }
        return await _pipe.QueryAsync(CancellationToken.None) ?? Array.Empty<ThermalReading>();
    }

    /// The probe at the app's start, with sign-in on: a few quick tries, since the logon helper may still be starting.
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
                Settle(answer);
                return;
            }
            await Task.Delay(StartEvery);
        }
        if (State == HelperState.Starting)
        {
            SetState(HelperState.Off);  // not running: Turn on starts it
        }
    }

    /// Waits for the helper's first answer after a start (through the UAC prompt and the install, which take a few seconds).
    private async Task<IReadOnlyList<ThermalReading>?> WaitForAnswerAsync()
    {
        for (var attempt = 0; attempt < AnswerTries; attempt++)
        {
            var answer = await _pipe.QueryAsync(CancellationToken.None);
            if (answer is not null)
            {
                return answer;
            }
            await Task.Delay(AnswerEvery);
        }
        return null;
    }

    /// An answer decides: zones make it On; no zones make it Unavailable, which stops the helper and clears the setting.
    private void Settle(IReadOnlyList<ThermalReading> answer)
    {
        if (answer.Count == 0)
        {
            _stop.Signal();
            ClearSettings();
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
