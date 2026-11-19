using NotchTasks.Core.Notch;
using static NotchTasks.Core.L10n;

namespace NotchTasks.Core.Modules;

/// The calls and the privacy sensors (Mac: Model.swift applySensors, toggleMute). A call is a call app holding the mic; it
/// ends only after its app has been gone for 8 s. An app back inside that grace (the call still exists) keeps the call and
/// its Since; an app that appears when there is no call is always a new call. A mic or camera that comes on outside a call is
/// announced once. Pure: the source reads the sensors and calls Apply, and calls Tick when EndDue comes; the toasts go to the
/// announcer.
public sealed class SensorsModule
{
    /// How long a call's app may be gone before the call ends, and how soon a returning app counts as the same call.
    public static readonly TimeSpan EndGrace = TimeSpan.FromSeconds(8);

    private const string Green = "#30D158";
    private const string Orange = "#FF9F0A";
    private const string Red = "#FF453A";

    private readonly IAnnouncer _announce;
    private readonly Func<bool> _privacyOn;
    private readonly Func<bool> _callsOn;
    private DateTimeOffset? _callEnded;      // the end mark: when the call's app first left, while the call is ending

    /// The toasts go to announce. privacyOn and callsOn say whether those modules are on (read at each change).
    public SensorsModule(IAnnouncer announce, Func<bool> privacyOn, Func<bool> callsOn)
    {
        _announce = announce;
        _privacyOn = privacyOn;
        _callsOn = callsOn;
    }

    /// What is using the mic and camera now, as of the last Apply.
    public SensorState Sensors { get; private set; } = new(Array.Empty<string>(), false, false);

    /// The live call, or null when none is on.
    public CallInfo? Call { get; private set; }

    /// When the live call ends, or null when no call is ending. A call is ending while it has no app holding the mic. The
    /// source's one-shot timer waits for this time, then calls Tick.
    public DateTimeOffset? EndDue => Call is not null && _callEnded is { } ended ? ended + EndGrace : null;

    /// Records a new reading of the sensors and makes the toasts it causes.
    public void Apply(SensorState next, DateTimeOffset now)
    {
        var prev = Sensors;
        Sensors = next;
        Evaluate(prev, now);
    }

    /// Runs the end logic again on the current sensors, so a call ends 8 s after its app left even with no new reading.
    public void Tick(DateTimeOffset now) => Evaluate(Sensors, now);

    /// The mute button. setMuted sets the default mic's mute and says whether it worked. On success the call and the sensors
    /// take the new value and the result is true; on failure a toast says to use the call app, and the result is false.
    /// With no call there is nothing to mute: the result is false and setMuted is not called.
    public bool ToggleMute(Func<bool, bool> setMuted)
    {
        if (Call is not { } call)
        {
            return false;
        }
        var want = !call.Muted;
        if (setMuted(want))
        {
            Call = call with { Muted = want };
            Sensors = Sensors with { MicMuted = want };
            return true;
        }
        _announce.Announce(
            new Toast(
                Glyph: "micoff",
                Tint: Orange,
                Title: L("Couldn't mute"),
                Subtitle: L("Use the button in {0}", call.Name),
                Event: null,
                Compact: true,
                Module: "calls"),
            notify: false);
        return false;
    }

    /// Forgets the calls and the sensors, without a toast: the modules are off. The next reading starts again.
    public void Reset()
    {
        Sensors = new SensorState(Array.Empty<string>(), false, false);
        Call = null;
        _callEnded = null;
    }

    private void Evaluate(SensorState prev, DateTimeOffset now)
    {
        var next = Sensors;
        var app = _callsOn() ? CallApps.CallApp(next.MicApps) : null;
        if (app is not null)
        {
            if (Call is null)
            {
                // no call is on (none, or the last one has already ended): this app starts a new call
                Call = new CallInfo(app, CallApps.Name(app), now, next.Camera, next.MicMuted);
                Announce(new Toast(
                    Glyph: "phone",
                    Tint: Green,
                    Title: L("On a call"),
                    Subtitle: CallApps.Name(app),
                    Event: "callStart",
                    Compact: true,
                    Module: "calls"));
            }
            else
            {
                Call = Call with { App = app, Name = CallApps.Name(app), Video = next.Camera, Muted = next.MicMuted };
            }
            _callEnded = null;
        }
        else if (Call is { } call)
        {
            _callEnded ??= now;
            var ended = _callEnded.Value;
            if (now - ended >= EndGrace)
            {
                Call = null;
                _callEnded = null;
                Announce(new Toast(
                    Glyph: "phonedown",
                    Tint: Red,
                    Title: L("Ended"),
                    Subtitle: TimerState.Mmss(ended - call.Since),
                    Event: "callEnd",
                    Compact: true,
                    Module: "calls"));
            }
        }

        // outside a call: say once when something starts recording or filming
        if (!_privacyOn() || Call is not null || app is not null)
        {
            return;
        }
        if (next.MicApps.FirstOrDefault(id => !prev.MicApps.Contains(id)) is { } newMic)
        {
            Announce(new Toast(
                Glyph: "mic",
                Tint: Orange,
                Title: L("Microphone on"),
                Subtitle: CallApps.Name(newMic),
                Event: "sensorOn",
                Compact: true,
                Module: "privacy"));
        }
        else if (next.Camera && !prev.Camera)
        {
            Announce(new Toast(
                Glyph: "camera",
                Tint: Green,
                Title: L("Camera on"),
                Subtitle: next.MicApps.Count > 0 ? CallApps.Name(next.MicApps[0]) : "Camera",
                Event: "sensorOn",
                Compact: true,
                Module: "privacy"));
        }
    }

    /// A call or privacy toast: not a system notification, so notify is off.
    private void Announce(Toast toast) => _announce.Announce(toast, notify: false);
}
