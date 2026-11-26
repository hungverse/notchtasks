using NotchTasks.Core.Notch;
using static NotchTasks.Core.L10n;

namespace NotchTasks.Core.Modules;

/// The heat toast (Mac: thermalChanged in Model.swift). The hottest reading decides the level: serious above 85 C, critical above
/// 95 C. Each level fires once per crossing, and re-arms only after the hottest reading has fallen 5 C below its threshold (serious
/// at 80 C, critical at 90 C). Critical supersedes serious: a crossing that reaches critical announces only critical, and serious
/// stays quiet until the reading falls back to 80 C. The levels live here, so a source that stops and starts does not warn twice.
/// Pure: the source reads the helper and calls Judge; the toast goes to the announcer.
public sealed class HeatModule
{
    /// The serious level: above this, the computer is warm.
    public const double SeriousAbove = 85;

    /// The critical level: above this, the computer is very hot.
    public const double CriticalAbove = 95;

    /// How far a reading must fall below a level's threshold before that level is armed again.
    public const double RearmBelow = 5;

    private const string Orange = "#FF9F0A";
    private const string Red = "#FF453A";

    private readonly IAnnouncer _announce;
    private bool _seriousFired;   // the serious crossing has warned, and the reading has not yet fallen to 80 C
    private bool _criticalFired;  // the critical crossing has warned, and the reading has not yet fallen to 90 C

    /// The toasts go to announce.
    public HeatModule(IAnnouncer announce)
    {
        _announce = announce;
    }

    /// Judges the hottest reading (null: the helper gave none, so nothing changes). tool is the working tool's name for the
    /// subtitle, or null when none is working.
    public void Judge(ThermalReading? hottest, string? tool)
    {
        if (hottest is not { } reading)
        {
            return;
        }
        var celsius = reading.Celsius;
        if (celsius <= SeriousAbove - RearmBelow)
        {
            _seriousFired = false;
        }
        if (celsius <= CriticalAbove - RearmBelow)
        {
            _criticalFired = false;
        }

        if (celsius > CriticalAbove)
        {
            if (!_criticalFired)
            {
                _criticalFired = true;
                _seriousFired = true;  // critical supersedes serious for this crossing
                Announce(L("Computer is very hot"), Red, tool);
            }
        }
        else if (celsius > SeriousAbove && !_seriousFired)
        {
            _seriousFired = true;
            Announce(L("Computer is warm"), Orange, tool);
        }
    }

    private void Announce(string title, string tint, string? tool) =>
        _announce.Announce(new Toast(
            Glyph: "thermometer",
            Tint: tint,
            Title: title,
            Subtitle: tool is null ? L("Consider closing heavy apps") : L("{0} is working hard", tool),
            Event: "warning",
            Url: ToastLinks.TaskManager,
            Action: L("See apps"),
            Module: "health",
            ToPhone: true),
            notify: true);
}
