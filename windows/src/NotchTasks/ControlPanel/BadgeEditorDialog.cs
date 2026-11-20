using System.Text.Json.Nodes;
using System.Windows;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel;

/// The icon editor of one build tool, opened from the Build & CI box (the Mac's BadgeEditor for a tool). Each edit saves the tool's
/// icon at once (SettingsStore.SetToolIcon); a refused badge shows its reason, and the controls take the saved values back. Reset
/// removes the tool's icon, so the box shows the tool's default again.
public partial class BadgeEditorDialog : Window
{
    private readonly SettingsStore _store;
    private readonly string _tool;

    /// The badge shown: the tool's saved icon, as the store last accepted it.
    private JsonObject _badge;

    public BadgeEditorDialog(SettingsStore store, string tool, JsonObject badge)
    {
        _store = store;
        _tool = tool;
        _badge = (JsonObject)badge.DeepClone();
        InitializeComponent();

        Title = L10n.L("Symbol");
        SubtitleText.Text = tool;
        Body.Edit += OnEdit;
        DoneButton.Click += (_, _) => Close();
        ResetButton.Click += (_, _) => ResetTool();
        Render();
    }

    /// Applies one change to a copy of the badge and saves it. A refused badge is not saved: its reason shows.
    private void OnEdit(Action<JsonObject> change)
    {
        var next = (JsonObject)_badge.DeepClone();
        change(next);
        var refusal = _store.SetToolIcon(_tool, next);
        if (refusal is null)
        {
            _badge = _store.ToolIcons.TryGetValue(_tool, out var saved) ? saved : next;
        }
        ShowError(refusal);
        Render();
    }

    /// Removes the tool's icon and closes the dialog.
    private void ResetTool()
    {
        _store.SetToolIcon(_tool, null);
        Close();
    }

    private void Render()
    {
        var accent = BadgeGlyph.AccentOf(_store);
        var preview = BadgeGlyph.Create(_badge, 40, accent);
        preview.HorizontalAlignment = HorizontalAlignment.Center;
        preview.VerticalAlignment = VerticalAlignment.Center;
        Preview.Child = preview;
        Body.Show(_badge, accent);
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
