using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NotchTasks.ControlPanel;

/// The Mac's status colours and the badge built from them, so the panes share one look. The Mac's system colours in
/// dark mode (SettingsView.swift, Preferences.swift).
public static class ControlPanelLook
{
    /// The green of "granted" and of the Permissions stat.
    public static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);

    /// The orange of "needed" and of the sparkle in the Overview's sample.
    public static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);

    /// The red of "denied" and of CI errors.
    public static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);

    /// The grey of "not checked" and of an off module's tile.
    public static readonly Color Grey = Color.FromRgb(0x8E, 0x8E, 0x93);

    /// A status pill: the text in `color`, the fill in `color` at 15%. The Border takes the StatusBadge style.
    public static Border Badge(string text, Color color)
    {
        var badge = new Border
        {
            Style = (Style)Application.Current.FindResource("StatusBadge"),
            Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
            Child = new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.FindResource("BadgeText"),
                Foreground = new SolidColorBrush(color),
            },
        };
        return badge;
    }
}
