using System.ComponentModel;
using NotchTasks.Core;

namespace NotchTasks.ControlPanel;

/// The panes of the Control Panel, in the Mac's order (Preferences.swift). Temperatures is the Mac's Fans & temperature without the
/// fans: the readings of the optional helper, read only.
public enum PaneId
{
    Overview,
    Modules,
    Permissions,
    Watching,
    Pairing,
    Temperatures,
    Appearance,
    Badges,
    Store,
    Sounds,
    Behavior,
}

public static class PaneIds
{
    /// The English title of a pane; L10n translates it (Preferences.swift Pane.title).
    public static string TitleKey(this PaneId pane) => pane switch
    {
        PaneId.Overview => "Overview",
        PaneId.Modules => "Modules",
        PaneId.Permissions => "Permissions",
        PaneId.Watching => "Watching",
        PaneId.Pairing => "Pairing",
        PaneId.Temperatures => "Temperatures",
        PaneId.Appearance => "Appearance",
        PaneId.Badges => "Icons & lock",
        PaneId.Store => "Icon Library",
        PaneId.Sounds => "Sound",
        PaneId.Behavior => "Behavior",
        _ => throw new ArgumentOutOfRangeException(nameof(pane)),
    };

    /// The Fluent System Icons glyph of a pane.
    public static string Glyph(this PaneId pane) => pane switch
    {
        PaneId.Overview => "",
        PaneId.Modules => "",
        PaneId.Permissions => "",
        PaneId.Watching => "",
        PaneId.Pairing => "",
        PaneId.Temperatures => "\uF78F",  // thermometer, Fluent (the toast's glyph)
        PaneId.Appearance => "",
        PaneId.Badges => "",
        PaneId.Store => "",
        PaneId.Sounds => "",
        PaneId.Behavior => "",  // options: the Behavior pane
        _ => throw new ArgumentOutOfRangeException(nameof(pane)),
    };
}

/// One row of the sidebar. Title reads the current language and raises PropertyChanged when the language changes.
public sealed class PaneItem : INotifyPropertyChanged
{
    /// The rows, one per pane, in order. Made once: each row stays subscribed for the life of the app.
    public static IReadOnlyList<PaneItem> All { get; } = Enum.GetValues<PaneId>().Select(p => new PaneItem(p)).ToList();

    private PaneItem(PaneId pane)
    {
        Pane = pane;
        LocalizedStrings.Instance.PropertyChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
    }

    public PaneId Pane { get; }

    public string Glyph => Pane.Glyph();

    public string Title => L10n.L(Pane.TitleKey());

    public event PropertyChangedEventHandler? PropertyChanged;
}
