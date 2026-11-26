namespace NotchTasks.ControlPanel;

/// The icon the Icons pane has selected: the Icon Library assigns its animations to it. It is a badge id, or none.
/// Cleared when the Control Panel closes.
public static class IconSelection
{
    public static string? BadgeId { get; private set; }

    public static void Select(string id) => BadgeId = id;

    public static void Clear() => BadgeId = null;
}
