using NotchTasks.Core.Agents;

namespace NotchTasks.Core.Notch;

/// A toast the pill shows: the Mac's Toast (Model.swift). Tint is a #RRGGBB hex. Module is the owning module id,
/// used to drop its toasts. AskId ties a toast to an agent ask, so it goes when that ask is answered. Icon is the agent or app
/// the toast is about: its icon is drawn in place of the glyph, and the glyph moves to the right. It is the WatchItem itself,
/// not a WPF image, so Core stays free of WPF.
public sealed record Toast(
    string Glyph,
    string Tint,
    string Title,
    string Subtitle = "",
    string? Event = "taskDone",
    string? Url = null,
    bool Compact = false,
    ToastArt? Art = null,
    bool Sticky = false,
    string? Action = null,
    string? Pane = null,
    string? AskId = null,
    string? Module = null,
    bool ToPhone = false,
    WatchItem? Icon = null)
{
    /// Set at construction, so each toast is distinct even when its texts are equal.
    public Guid Id { get; } = Guid.NewGuid();

    /// The button label: null when the toast has nothing to open; otherwise Action, or "Open".
    public string? ActionTitle => Url is null && Pane is null ? null : Action ?? L10n.L("Open");
}

/// Art drawn inside a toast, in place of the glyph.
public abstract record ToastArt;

/// The battery toast art: the charge level and whether the charger is plugged in.
public sealed record ChargeArt(int Percent, bool Plugged) : ToastArt;

/// The device toast art: the accessory's symbol and whether it connected (false: it went away).
public sealed record DeviceArt(string Symbol, bool Connected) : ToastArt;

/// The links a toast may carry that are not files or web pages: the names the app opens itself (the heat toast's "See apps").
public static class ToastLinks
{
    /// Opens Task Manager (the Mac's Activity Monitor link).
    public const string TaskManager = "taskmgr";
}
