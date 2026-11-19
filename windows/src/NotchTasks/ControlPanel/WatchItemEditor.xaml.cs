using System.Globalization;
using System.IO;
using System.Windows;
using NotchTasks.Core;
using NotchTasks.Core.Agents;

namespace NotchTasks.ControlPanel;

/// Adds or edits one watch item (the Mac's WatchItemEditor). A process or app item gets all its fields: name, kind (process or app),
/// the process or program, the command line it contains, the CPU threshold, child tasks and Build & CI. Claude and Codex items are
/// tracked per session, so only their name and Build & CI are edited (the Mac's footer note). Save validates as settings.js watchItem
/// does. An item that is not valid is not saved: the window stays open with its error, and Result stays null. Otherwise Result is
/// the edited item (its unknown fields kept) and the window closes.
public partial class WatchItemEditor : Window
{
    /// The threshold range in percent (settings.js watchItem threshold).
    private const double MinThreshold = 0.5;
    private const double MaxThreshold = 100;

    private readonly WatchItem _item;
    private readonly bool _limited;

    /// The edited item, once it is saved. Null while the window is open or when it was cancelled.
    public WatchItem? Result { get; private set; }

    /// The editor for an item. A new item (isNew) is added on Save; an existing one is replaced.
    public WatchItemEditor(WatchItem item, bool isNew)
    {
        _item = item;
        _limited = item.Kind is WatchKind.Claude or WatchKind.Codex or WatchKind.Dev;
        InitializeComponent();

        TitleText.Text = isNew
            ? L10n.L("Add item to watch")
            : item.Name.Length == 0 ? L10n.L("Watched item") : item.Name;
        SaveButton.Content = L10n.L(isNew ? "Add" : "Save");
        ThresholdHint.Text = L10n.L("Raise if it shows while idle, lower if it misses work.");
        ChildTasksBox.Content = L10n.L("Each subprocess (runner, worker)");
        BuildBox.Content = L10n.L("Build & CI");
        LimitedNote.Text = L10n.L("{0} is tracked per work session, nothing else to set.",
            item.Kind == WatchKind.Codex ? "Codex" : "Claude");

        NameBox.Text = item.Name;
        KindBox.SelectedIndex = item.Kind == WatchKind.App ? 1 : 0;
        // An app shows its program as the watch list finds it (its exe), so what is saved is what is read back.
        ProgramBox.Text = (item.Kind == WatchKind.App ? WatchItems.AppExe(item) : item.Process) ?? string.Empty;
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

    /// The fields follow the kind and the threshold; Save is enabled only for a complete, valid item.
    private void UpdateState()
    {
        ProgramBox.MaxLength = WatchItemsJson.ProgramLimit(IsApp, ProgramBox.Text.Trim());
        var detailed = !_limited;
        KindRow.Visibility = Show(detailed);
        ProgramRow.Visibility = Show(detailed);
        ProgramHint.Visibility = Show(detailed);
        ThresholdRow.Visibility = Show(detailed);
        ThresholdHint.Visibility = Show(detailed);
        ArgsRow.Visibility = Show(detailed && !IsApp);
        ChildTasksBox.Visibility = Show(detailed && !IsApp);
        LimitedNote.Visibility = Show(_limited);

        ProgramLabel.Text = IsApp ? L10n.L("Program name") : L10n.L("Process name");
        ProgramHint.Text = IsApp
            ? L10n.L("Program name, as in Task Manager.")
            : ProcessHint();

        var thresholdOk = _limited || TryThreshold(out _);
        // a field past its limit (a name the list would drop) is not saved: the error says so
        var fits = !TryBuild(out var built) || WatchItemsJson.Keeps(built);
        var error = !thresholdOk ? L10n.L("Enter a number from 0.5 to 100, or leave it empty.")
            : !fits ? L10n.L("A field is too long for the watch list.")
            : string.Empty;
        ErrorText.Text = error;
        ErrorText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = thresholdOk && fits && Complete();
    }

    /// "Process name, as in Task Manager." is a Windows key on purpose: Activity Monitor (the Mac's hint) does not exist on Windows.
    private static string ProcessHint() => L10n.L("Process name, as in Task Manager.");

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// A name, and for a process or an app a program. A process item may give a command line instead of a process name.
    private bool Complete()
    {
        if (NameBox.Text.Trim().Length == 0)
        {
            return false;
        }
        if (_limited)
        {
            return true;
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
        if (!TryBuild(out var item) || !WatchItemsJson.Keeps(item))
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
        double? threshold = _item.Threshold;
        if (!Complete() || (!_limited && !TryThreshold(out threshold)))
        {
            return false;
        }

        var name = NameBox.Text.Trim();
        var build = BuildBox.IsChecked == true;
        if (_limited)
        {
            item = _item with { Name = name, Build = build };
            return true;
        }

        var program = ProgramBox.Text.Trim();
        if (IsApp)
        {
            item = WithProgram(_item with
            {
                Name = name,
                Kind = WatchKind.App,
                ChildTasks = false,
                Threshold = threshold,
                Build = build,
            }, program);
            return true;
        }

        var args = ArgsBox.Text.Trim();
        item = _item with
        {
            Name = name,
            Kind = WatchKind.Process,
            Process = program,
            Args = args.Length == 0 ? null : args,
            Threshold = threshold,
            ChildTasks = ChildTasksBox.IsChecked == true,
            Build = build,
        };
        return true;
    }

    /// Sets an app's program. The program the item already shows (its AppExe) keeps the item's fields. An absolute path is saved as
    /// Exe. A name is saved as BundleID with ".exe", the form the running apps use, and is read back through AppExe.
    private WatchItem WithProgram(WatchItem item, string program)
    {
        if (string.Equals(program, WatchItems.AppExe(_item), StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }
        if (Path.IsPathRooted(program) && program.Length <= WatchItemsJson.MaxExePath)
        {
            return item with { Exe = program };
        }
        var bundle = program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? program : program + ".exe";
        return item with { Exe = null, BundleID = bundle };
    }
}
