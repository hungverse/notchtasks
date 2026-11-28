using System.Windows;
using NotchTasks.Core;
using NotchTasks.ControlPanel;

namespace NotchTasks.ControlPanel.Panes;

/// The Turn on confirmation of the CPU temperature: the helper's UAC prompt follows it, so the user is told first what runs (the Claude
/// limits confirmation's dialog). Cancel starts nothing.
public static class TemperatureConsent
{
    /// The confirmation, owned by the window of owner. True when the user allows it.
    public static bool Ask(DependencyObject owner)
    {
        var dialog = new ConfirmDialog(
            L10n.L("Show the real CPU temperature?"),
            L10n.L("NotchTasks will install the PawnIO driver (signed, from pawnio.eu) and run a small helper with administrator rights to read the CPU and GPU temperatures. Windows will ask once."),
            L10n.L("Allow"),
            L10n.L("Cancel"))
        {
            Owner = Window.GetWindow(owner),
        };
        return dialog.ShowDialog() == true;
    }
}
