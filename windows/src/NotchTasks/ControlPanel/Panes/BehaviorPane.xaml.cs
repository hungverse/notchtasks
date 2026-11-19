using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Core.Settings;

namespace NotchTasks.ControlPanel.Panes;

/// The Behavior pane (Preferences.swift behavior): how the notch opens, the notifications and limit warning, the language,
/// the tray icon and the usage statistics. Haptic feedback is not shown (the Mac's trackpad only).
public partial class BehaviorPane : SettingsPane
{
    private readonly DelayedWrite _delay;
    private readonly DelayedWrite _done;
    private readonly DelayedWrite _limit;

    public BehaviorPane(SettingsStore store) : base(store)
    {
        InitializeComponent();
        _delay = Delayed();
        _done = Delayed();
        _limit = Delayed();

        ExpandBox.ItemsSource = ChoiceItems.ExpandModes;
        LanguageBox.ItemsSource = ChoiceItems.Languages;

        ExpandBox.SelectionChanged += (_, _) => WriteChoice(ExpandBox, "expandMode");
        LanguageBox.SelectionChanged += (_, _) => WriteChoice(LanguageBox, "language");
        SystemLoadBox.Click += (_, _) => Write("systemLoad", JsonValue.Create(SystemLoadBox.IsChecked == true));
        NotificationsBox.Click += (_, _) => Write("systemNotifications", JsonValue.Create(NotificationsBox.IsChecked == true));
        MenuBarBox.Click += (_, _) => Write("menuBarIcon", JsonValue.Create(MenuBarBox.IsChecked == true));
        AnalyticsBox.Click += (_, _) => Write("analytics.off", JsonValue.Create(AnalyticsBox.IsChecked != true));

        DelaySlider.ValueChanged += (_, _) =>
        {
            if (Applying)
            {
                return;
            }
            ShowDelay(DelaySlider.Value);
            _delay.Schedule(() => Write("hoverDelay", JsonValue.Create(Math.Round(DelaySlider.Value, 2))));
        };
        DoneSlider.ValueChanged += (_, _) =>
        {
            if (Applying)
            {
                return;
            }
            ShowDone(DoneSlider.Value);
            _done.Schedule(() => Write("doneAfter", JsonValue.Create(Math.Round(DoneSlider.Value))));
        };
        LimitSlider.ValueChanged += (_, _) =>
        {
            if (Applying)
            {
                return;
            }
            ShowLimit(LimitSlider.Value);
            _limit.Schedule(() => Write("limitWarn", JsonValue.Create(Math.Round(LimitSlider.Value))));
        };
    }

    protected override void Apply()
    {
        Select(ExpandBox, ChoiceItems.ExpandModes, Store.ExpandMode);
        DelayRow.Visibility = Store.ExpandMode == "hover" ? Visibility.Visible : Visibility.Collapsed;
        if (!_delay.Pending)
        {
            DelaySlider.Value = Store.HoverDelay;
        }
        ShowDelay(DelaySlider.Value);

        SystemLoadBox.IsChecked = Store.SystemLoad;
        NotificationsBox.IsChecked = Store.SystemNotifications;
        if (!_done.Pending)
        {
            DoneSlider.Value = Store.DoneAfter;
        }
        ShowDone(DoneSlider.Value);
        if (!_limit.Pending)
        {
            LimitSlider.Value = Store.LimitWarn;
        }
        ShowLimit(LimitSlider.Value);

        Select(LanguageBox, ChoiceItems.Languages, Store.Language);
        MenuBarBox.IsChecked = Store.MenuBarIcon;
        AnalyticsBox.IsChecked = !Store.AnalyticsOff;
    }

    /// "Delay N ms": the hover delay in milliseconds, from the seconds of the slider.
    private void ShowDelay(double seconds) =>
        DelayLabel.Text = L10n.L("Delay {0} ms", (int)Math.Round(seconds * 1000));

    /// "Say done when it ran N sec or more" (Preferences.swift): the seconds a task must run before its completion is announced.
    private void ShowDone(double seconds) =>
        DoneLabel.Text = L10n.L("Say done when it ran {0} sec or more", (int)Math.Round(seconds));

    /// "Warn about limits at N%".
    private void ShowLimit(double percent) =>
        LimitLabel.Text = L10n.L("Warn about limits at {0}%", (int)Math.Round(percent));
}
