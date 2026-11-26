namespace NotchTasks.Core.Levels;

/// Which bar the notch shows: the volume or the brightness.
public enum LevelKind
{
    Volume,
    Brightness,
}

/// The bar on the notch while volume or brightness changes: its kind, its value from 0 to 1, and whether the volume is muted.
public readonly record struct LevelHud(LevelKind Kind, double Value, bool Muted = false);

/// The volume and brightness bar (the Mac's sampleLevels and Levels). The source feeds it the readings: the first reading of each kind is
/// a baseline and shows nothing, a move of more than Step shows the bar, and a new output device re-baselines the volume so its jump is
/// not shown. Hide is called when the bar's time is up. This has no timers and no OS calls, so the source decides when a bar ends.
public sealed class LevelsModule
{
    /// The smallest move that shows a bar (the Mac's 0.004).
    public const double Step = 0.004;

    /// How long a bar stays on the notch (the Mac's 1.5 s). The source hides it after this.
    public static readonly TimeSpan Shown = TimeSpan.FromSeconds(1.5);

    private VolumeReading? _volume;
    private double? _brightness;

    /// The bar the notch shows now, or null when none.
    public LevelHud? Hud { get; private set; }

    /// Raised when a bar shows or hides.
    public event Action? Changed;

    /// Takes the volume as the baseline without showing anything: the start, or a new output device (its volume is not a change).
    public void SetVolumeBaseline(VolumeReading? volume) => _volume = volume;

    /// Takes the brightness as the baseline without showing anything.
    public void SetBrightnessBaseline(double? brightness) => _brightness = brightness;

    /// A volume reading. Shows the volume bar when the volume or the mute moved by more than Step; the baseline follows every reading.
    /// True when the bar shows. The first reading is the baseline.
    public bool Volume(VolumeReading volume)
    {
        var last = _volume;
        _volume = volume;
        if (last is not { } previous)
        {
            return false;
        }
        if (Math.Abs(volume.Value - previous.Value) <= Step && volume.Muted == previous.Muted)
        {
            return false;
        }
        Show(new LevelHud(LevelKind.Volume, volume.Value, volume.Muted));
        return true;
    }

    /// A brightness reading. Shows the brightness bar when it moved by more than Step. True when the bar shows.
    public bool Brightness(double brightness)
    {
        var last = _brightness;
        _brightness = brightness;
        if (last is not { } previous || Math.Abs(brightness - previous) <= Step)
        {
            return false;
        }
        Show(new LevelHud(LevelKind.Brightness, brightness));
        return true;
    }

    /// The bar's time is up (or the module turned off): the notch shows its own content again. Raises Changed only when a bar was up.
    public void Hide()
    {
        if (Hud is null)
        {
            return;
        }
        Hud = null;
        Changed?.Invoke();
    }

    /// The module is turned off: no bar, and no baseline, so a later start reads its first reading as a baseline again.
    public void Clear()
    {
        _volume = null;
        _brightness = null;
        Hide();
    }

    /// The SF-symbol names the Mac shows for a bar, as Fluent glyph names (ModuleGlyphs): the volume's speaker by its level (off when
    /// muted or at zero), and the brightness sun by its half.
    public static string SymbolName(LevelHud hud) => hud.Kind switch
    {
        LevelKind.Volume when hud.Muted || hud.Value == 0 => "volume.off",
        LevelKind.Volume when hud.Value < 0.34 => "volume.low",
        LevelKind.Volume when hud.Value < 0.67 => "volume.mid",
        LevelKind.Volume => "volume.high",
        _ => hud.Value < 0.5 ? "brightness.low" : "brightness.high",
    };

    /// Shows a bar and raises Changed, so the source restarts its hide timer.
    private void Show(LevelHud hud)
    {
        Hud = hud;
        Changed?.Invoke();
    }
}
