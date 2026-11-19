using System.Globalization;
using System.Windows;
using NotchTasks.Core;
using NotchTasks.Core.Agents;

namespace NotchTasks.ControlPanel;

/// Adds or edits one watch item (the Mac's WatchItemEditor): name, kind (process or app), the process or program, the command
/// line it contains, the CPU threshold, child tasks and Build & CI. Save validates as settings.js watchItem does. An item that is
/// not valid is not saved: the window stays open with its error, and Result stays null. Otherwise Result is the edited item (its
/// unknown fields kept) and the window closes.
public partial class WatchItemEditor : Window
{
    /// The threshold range in percent (settings.js watchItem threshold).
    private const double MinThreshold = 0.5;
    private const double MaxThreshold = 100;

    private readonly WatchItem _item;

    /// The edited item, once it is saved. Null while the window is open or when it was cancelled.
    public WatchItem? Result { get; private set; }

    /// The editor for an item. A new item (isNew) is added on Save; an existing one is replaced.
    public WatchItemEditor(WatchItem item, bool isNew)
    {
        _item = item;
        InitializeComponent();

        TitleText.Text = isNew
            ? L10n.L("Add item to watch")
            : item.Name.Length == 0 ? L10n.L("Watched item") : item.Name;
        SaveButton.Content = L10n.L(isNew ? "Add" : "Save");
        ThresholdHint.Text = L10n.L("Raise if it shows while idle, lower if it misses work.");
        ChildTasksBox.Content = L10n.L("Each subprocess (runner, worker)");
        BuildBox.Content = L10n.L("Build & CI");

        NameBox.Text = item.Name;
        KindBox.SelectedIndex = item.Kind == WatchKind.App ? 1 : 0;
        ProgramBox.Text = (item.Kind == WatchKind.App ? item.BundleID : item.Process) ?? string.Empty;
        ArgsBox.Text = item.Args ?? string.Empty;
        ThresholdBox.Text = item.Threshold is { } threshold ? threshold.ToString("0.##", CultureInfo.CurrentCulture) : string.Empty;
        ChildTasksBox.IsChecked = item.ChildTasks;
        BuildBox.IsChecked = item.Build;

        NameBox.TextChanged += (_, _) => UpdateState();
        ProgramBox.TextChanged += (_, _) => UpdateState();
        ArgsBox.TextChanged += (_, _) => UpdateState();
        ThresholdBox.TextChanged += (_, _) => UpdateState();
        KindBox.SelectionChanged += (_, _) => UpdateState();
        SaveButton.Click += (_, _) => Save();
        CancelButton.Click += (_, _) => Close();

        UpdateState();
    }

    private bool IsApp => KindBox.SelectedIndex == 1;

    /// The labels and the error line follow the kind and the threshold; Save is enabled only for a complete, valid item.
    private void UpdateState()
    {
        ProgramLabel.Text = IsApp ? L10n.L("Program name") : L10n.L("Process name");
        ProgramHint.Text = IsApp
            ? L10n.L("Program name, as in Task Manager.")
            : L10n.L("Process name, as in Task Manager.");
        ArgsRow.Visibility = IsApp ? Visibility.Collapsed : Visibility.Visible;
        ChildTasksBox.Visibility = IsApp ? Visibility.Collapsed : Visibility.Visible;

        var thresholdOk = TryThreshold(out _);
        ErrorText.Text = thresholdOk ? string.Empty : L10n.L("Enter a number from 0.5 to 100, or leave it empty.");
        ErrorText.Visibility = thresholdOk ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = thresholdOk && Complete();
    }

    /// A name, and a process or a program. A process item may give a command line instead of a process name.
    private bool Complete()
    {
        if (NameBox.Text.Trim().Length == 0)
        {
            return false;
        }
        if (IsApp)
        {
            return ProgramBox.Text.Trim().Length > 0;
        }
        return ProgramBox.Text.Trim().Length > 0 || ArgsBox.Text.Trim().Length > 0;
    }

    /// The threshold the box holds: blank means the kind's default (null); a number from 0.5 to 100 is the threshold.
    private bool TryThreshold(out double? threshold)
    {
        var text = ThresholdBox.Text.Trim();
        if (text.Length == 0)
        {
            threshold = null;
            return true;
        }
        var parsed = double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (parsed && value >= MinThreshold && value <= MaxThreshold)
        {
            threshold = value;
            return true;
        }
        threshold = null;
        return false;
    }

    /// Saves the item when it is valid: Result is set and the window closes (when it is shown).
    private void Save()
    {
        if (!TryBuild(out var item))
        {
            UpdateState();
            return;
        }
        Result = item;
        if (IsVisible)
        {
            Close();
        }
    }

    /// The edited item, built on the original so the fields the editor does not show (and unknown fields) are kept.
    private bool TryBuild(out WatchItem item)
    {
        item = _item;
        if (!Complete() || !TryThreshold(out var threshold))
        {
            return false;
        }

        var program = ProgramBox.Text.Trim();
        var args = ArgsBox.Text.Trim();
        var app = IsApp;
        item = _item with
        {
            Name = NameBox.Text.Trim(),
            Kind = app ? WatchKind.App : WatchKind.Process,
            Process = app ? _item.Process : program,
            BundleID = app ? program : _item.BundleID,
            Args = app ? _item.Args : (args.Length == 0 ? null : args),
            Threshold = threshold,
            ChildTasks = !app && ChildTasksBox.IsChecked == true,
            Build = BuildBox.IsChecked == true,
        };
        return true;
    }
}
