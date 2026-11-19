using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Core.Modules;
using NotchTasks.Services;

namespace NotchTasks.Pill.Sections;

/// The privacy row in the open pill (Mac SensorRow): the apps using the mic and whether the camera is on. It shows only with no
/// call on, as the closed pill's sensor chip does. One element kept for the pill's life.
public partial class SensorRow : UserControl
{
    public SensorRow()
    {
        InitializeComponent();
    }

    /// Refreshes the row and returns true. Returns false, and leaves the row alone, when a call is on or nothing is in use.
    public bool Update(AppState state)
    {
        var sensors = state.Sensors.Sensors;
        if (state.Sensors.Call is not null || !(sensors.Mic || sensors.Camera))
        {
            return false;
        }
        MicGlyph.Text = ModuleGlyphs.Get("mic");
        MicGlyph.Visibility = sensors.Mic ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        MicText.Text = sensors.Mic ? L10n.L("Micro: ") + string.Join(", ", sensors.MicApps.Select(CallApps.Name)) : string.Empty;
        MicText.Visibility = MicGlyph.Visibility;
        CameraGlyph.Text = ModuleGlyphs.Get("camera");
        CameraGlyph.Visibility = sensors.Camera ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        CameraText.Text = sensors.Camera ? L10n.L("Camera on") : string.Empty;
        CameraText.Visibility = CameraGlyph.Visibility;
        return true;
    }
}
