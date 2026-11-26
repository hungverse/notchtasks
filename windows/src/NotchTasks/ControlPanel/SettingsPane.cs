using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Services;

namespace NotchTasks.ControlPanel;

/// The base of the Settings panes (Appearance, Sound, Behavior). The controls show the store's values and write each
/// change to it. While the pane is shown it follows the store, so an edit made outside the app shows at once, and a
/// language change re-reads the labels. Applying a stored value never writes it back: Apply runs with writes off.
public abstract class SettingsPane : UserControl
{
    private readonly List<DelayedWrite> _delayed = new();
    private bool _shown;
    private bool _applying;
    private Window? _window;

    protected SettingsPane(SettingsStore store)
    {
        Store = store;
        Loaded += OnShown;
        Unloaded += OnHidden;
    }

    protected SettingsStore Store { get; }

    /// True while Apply gives the controls their stored values. Handlers that only follow a control's own change check it.
    protected bool Applying => _applying;

    /// Gives the controls the stored values. Runs on the UI thread with writes off.
    protected abstract void Apply();

    /// Runs while the pane is shown and its window is activated: the user may have come back from Settings. Default: nothing.
    protected virtual void OnWindowActivated()
    {
    }

    /// A write that waits for a slider to settle. Pane is left: its pending writes run at once.
    protected DelayedWrite Delayed()
    {
        var delayed = new DelayedWrite();
        _delayed.Add(delayed);
        return delayed;
    }

    /// Saves one key, unless a stored value is being applied. A failed save is logged, and the controls take the stored value back.
    protected void Write(string key, JsonNode? value)
    {
        if (_applying)
        {
            return;
        }
        try
        {
            Store.Set(key, value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException($"NotchTasks: could not save {key}", e));
            ApplyStored();
        }
    }

    /// Saves several keys in one write, so a theme or an imported profile reaches every open pane at once. Writes nothing while a
    /// stored value is being applied; a failed save is logged, and the controls take the stored values back.
    protected void WriteMany(IReadOnlyDictionary<string, JsonNode?> values)
    {
        if (_applying)
        {
            return;
        }
        try
        {
            Store.SetMany(values);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException("NotchTasks: could not save the settings", e));
            ApplyStored();
        }
    }

    /// Saves the value of the picker's selected item under `key`.
    protected void WriteChoice(ComboBox picker, string key)
    {
        if (picker.SelectedItem is Choice choice)
        {
            Write(key, JsonValue.Create(choice.Value));
        }
    }

    /// Selects the item whose value is `value`, when there is one.
    protected static void Select(ComboBox picker, IEnumerable<Choice> items, string value)
    {
        picker.SelectedItem = items.FirstOrDefault(item => item.Value == value);
    }

    /// Applies the stored values to the controls, with writes off.
    protected void ApplyStored()
    {
        _applying = true;
        try
        {
            Apply();
        }
        finally
        {
            _applying = false;
        }
    }

    private void OnShown(object sender, RoutedEventArgs e)
    {
        if (_shown)
        {
            return;
        }
        _shown = true;
        Store.Changed += OnStoreChanged;
        L10n.Changed += OnLanguageChanged;
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.Activated += OnWindowActivatedEvent;
        }
        ApplyStored();
    }

    private void OnHidden(object sender, RoutedEventArgs e)
    {
        if (!_shown)
        {
            return;
        }
        _shown = false;
        Store.Changed -= OnStoreChanged;
        L10n.Changed -= OnLanguageChanged;
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivatedEvent;
            _window = null;
        }
        foreach (var delayed in _delayed)
        {
            delayed.Flush();
        }
    }

    // The store raises Changed on the thread that made the change (a settings file edit comes from a thread-pool thread).
    private void OnStoreChanged(IReadOnlyCollection<string> keys) => OnUi(ApplyStored);

    private void OnLanguageChanged() => OnUi(ApplyStored);

    private void OnWindowActivatedEvent(object? sender, EventArgs e) => OnUi(OnWindowActivated);

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            if (_shown)
            {
                action();
            }
            return;
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_shown)
            {
                action();
            }
        }));
    }
}
