using System.Windows.Controls;

namespace NotchTasks.Pill;

/// The open pill's quick bar: the timer pills at the left, and the Control Panel and Quit buttons at the right. Its buttons
/// never take the focus: the pill window does not activate.
public partial class QuickBar : UserControl
{
    public QuickBar()
    {
        InitializeComponent();
        ControlPanelButton.Click += (_, _) => ControlPanelRequested?.Invoke();
        QuitButton.Click += (_, _) => QuitRequested?.Invoke();
    }

    /// The gear: open the Control Panel.
    public event Action? ControlPanelRequested;

    /// The power button: quit the app.
    public event Action? QuitRequested;
}
