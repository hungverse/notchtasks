using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel;

/// The icon editor of a build tool (opened from the Build & CI box) or of a watched item (opened from the Watching pane): the Mac's
/// BadgeEditor. Each edit is saved at once through save; a refused badge shows its reason, and the controls take the last saved
/// values back. Reset removes the icon (the default comes back) and closes the dialog.
public partial class BadgeEditorDialog : Window
{
    private readonly Color _accent;
    private readonly Func<JsonObject, string?> _save;
    private readonly Action _reset;
    private readonly Action<string?> _discard;

    /// The badge shown: the badge as the last save accepted it.
    private JsonObject _badge;

    /// The editor of any badge. accent is what a badge without a colour fills with. save stores the badge and returns the refusal
    /// (null when saved); reset removes the icon. discard deletes a picture copy the badge no longer uses (null: keep it).
    public BadgeEditorDialog(string title, string subtitle, JsonObject badge, Color accent, Func<JsonObject, string?> save, Action reset,
        Action<string?>? discard = null)
    {
        _accent = accent;
        _save = save;
        _reset = reset;
        _discard = discard ?? (_ => { });
        _badge = (JsonObject)badge.DeepClone();
        InitializeComponent();

        Title = title;
        SubtitleText.Text = subtitle;
        Body.Edit += OnEdit;
        DoneButton.Click += (_, _) => Close();
        ResetButton.Click += (_, _) => ResetIcon();
        Render();
    }

    /// The editor of one build tool's icon: the tool's icon is saved in the store and removed by Reset.
    public BadgeEditorDialog(SettingsStore store, string tool, JsonObject badge)
        : this(L10n.L("Symbol"), tool, badge, BadgeGlyph.AccentOf(store),
            next => store.SetToolIcon(tool, next), () => store.SetToolIcon(tool, null), picture => BadgeImages.Discard(picture, store))
    {
    }

    /// Applies one change to a copy of the badge and saves it. A refused badge is not saved: its reason shows. A picture the badge
    /// no longer shows is discarded once the save has gone through.
    private void OnEdit(Action<JsonObject> change)
    {
        var before = BadgeEdits.ImageOf(_badge);
        var next = (JsonObject)_badge.DeepClone();
        change(next);
        var refusal = _save(next);
        if (refusal is null)
        {
            _badge = next;
            if (BadgeEdits.ImageOf(next) != before)
            {
                _discard(before);
            }
        }
        ShowError(refusal);
        Render();
    }

    /// Removes the icon, discards its picture copy, and closes the dialog.
    private void ResetIcon()
    {
        var before = BadgeEdits.ImageOf(_badge);
        _reset();
        _discard(before);
        Close();
    }

    private void Render()
    {
        var preview = BadgeGlyph.Create(_badge, 40, _accent);
        preview.HorizontalAlignment = HorizontalAlignment.Center;
        preview.VerticalAlignment = VerticalAlignment.Center;
        Preview.Child = preview;
        Body.Show(_badge, _accent);
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = message ?? string.Empty;
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
