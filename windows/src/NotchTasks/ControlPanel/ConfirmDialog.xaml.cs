using System.Windows;

namespace NotchTasks.ControlPanel;

/// A yes-or-no window owned by the Control Panel, in the Fluent style of the other dialogs. ShowDialog gives true when the action
/// button is pressed, and false for Cancel, Esc or the close button.
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string body, string action, string cancel)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        BodyText.Text = body;
        AllowButton.Content = action;
        CancelButton.Content = cancel;
        AllowButton.Click += (_, _) => DialogResult = true;
        CancelButton.Click += (_, _) => DialogResult = false;
    }
}
