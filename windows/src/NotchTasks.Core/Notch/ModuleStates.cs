namespace NotchTasks.Core.Notch;

/// A running timer: it ends at End, and was started for Total.
public sealed record TimerState(DateTimeOffset End, TimeSpan Total)
{
    /// The timer chip's text: m:ss, or h:mm:ss from one hour. Rounds UP to the whole second, so a timer
    /// with 0.2 s left still reads 0:01 and 4:59.2 reads 5:00. Zero or negative reads 0:00.
    public static string Mmss(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "0:00";
        var secs = (long)Math.Ceiling(t.TotalSeconds);
        var h = secs / 3600;
        var m = secs % 3600 / 60;
        var s = secs % 60;
        return h > 0 ? $"{h}:{m:D2}:{s:D2}" : $"{m}:{s:D2}";
    }
}

/// What is using the mic and camera right now. MicApps are the app names recording; MicMuted is the default input's mute.
public sealed record SensorState(IReadOnlyList<string> MicApps, bool Camera, bool MicMuted)
{
    public bool Mic => MicApps.Count > 0;
}

/// A live call: the app, the caller's name, when it started, and whether it is video and muted.
public sealed record CallInfo(string App, string Name, DateTimeOffset Since, bool Video, bool Muted);

/// The battery level, whether it is charging, and whether the charger is plugged in.
public sealed record BatteryState(int Percent, bool Charging, bool Plugged);

/// CPU and memory use, each a fraction 0..1.
public sealed record SystemLoad(double Cpu, double Memory);
