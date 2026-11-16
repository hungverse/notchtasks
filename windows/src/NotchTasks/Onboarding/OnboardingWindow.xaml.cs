using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.ControlPanel;
using NotchTasks.ControlPanel.Panes;
using NotchTasks.Core;
using NotchTasks.Core.Settings;
using NotchTasks.Services;

namespace NotchTasks.Onboarding;

/// The first-run guide (Onboarding.swift OnboardingView) as a window of five pages: welcome, modules, agents, permissions and
/// ready. The phone page comes with pairing (Phase 4). The modules, agents and permissions pages are the Control Panel's own
/// panes, so their rows are not built twice. Closing the window by any means (Skip, Show me, the close button) sets
/// onboardingDone, once. There is one guide: Show brings the open one to the front.
public partial class OnboardingWindow : Window
{
    private const int LastPage = 4;

    private static OnboardingWindow? _window;

    private readonly AppState _state;
    private readonly Action? _closed;
    private readonly Dictionary<int, UserControl> _panes = new();
    private int _page;
    private bool _finished;

    private OnboardingWindow(AppState state, Action? closed)
    {
        _state = state;
        _closed = closed;
        InitializeComponent();

        SkipButton.Click += (_, _) => Close();
        BackButton.Click += (_, _) =>
        {
            if (_page > 0)
            {
                ShowPage(_page - 1);
            }
        };
        NextButton.Click += (_, _) =>
        {
            if (_page == LastPage)
            {
                Close();
            }
            else
            {
                ShowPage(_page + 1);
            }
        };
        L10n.Changed += OnLanguageChanged;
        Closed += OnClosed;
        ShowPage(0);
    }

    /// Opens the guide, or brings the open one to the front: the user asked for it. `closed` runs once, when the guide closes
    /// for the first time (the first run shows the pill then). A guide that is already open ignores it.
    public static void Show(AppState state, Action? closed = null)
    {
        if (_window is null)
        {
            _window = new OnboardingWindow(state, closed);
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }
        _window.Activate();
        if (!_window.IsActive)
        {
            // Windows may refuse the foreground to a window that was not started by the user's click (the first run).
            // A topmost flip brings it forward once; it is not left topmost.
            _window.Topmost = true;
            _window.Activate();
            _window.Topmost = false;
        }
    }

    private SettingsStore Settings => _state.Settings;

    private void ShowPage(int page)
    {
        _page = page;
        PageHost.Content = PageFor(page);
        UpdateFooter();
    }

    private UIElement PageFor(int page) => page switch
    {
        0 => Welcome(),
        1 => Pane(page, () => new ModulesPane(Settings)),
        2 => Pane(page, () => new WatchingPane(Settings)),
        3 => Pane(page, () => new PermissionsPane(Settings)),
        _ => Ready(),
    };

    /// A pane page. Its pane is made once and kept, so going back and forth keeps its state.
    private UserControl Pane(int page, Func<UserControl> create)
    {
        if (!_panes.TryGetValue(page, out var pane))
        {
            pane = create();
            _panes[page] = pane;
        }
        return pane;
    }

    private UIElement Welcome()
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 40, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("PaneTitle"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Text = L10n.L("Welcome to NotchTasks"),
        });
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("PaneNote"),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 24),
            Text = L10n.L("The pill shows what's running, and stays quiet when nothing is."),
        });
        panel.Children.Add(Card(L10n.L("Look at the top edge of the screen: that is your pill."), centered: true));        return panel;
    }

    /// The last page: the three things to remember about the pill.
    private UIElement Ready()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Style = (Style)FindResource("PaneTitle"), Text = L10n.L("You're all set") });
        panel.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("PaneNote"),
            Margin = new Thickness(0, 0, 0, 12),
            Text = L10n.L("Three things to remember:"),
        });
        panel.Children.Add(Step([""], L10n.L("Opening the pill"), L10n.L("Point at the pill at the top of the screen.")));
        panel.Children.Add(Step(["", ""], L10n.L("Control Panel"),
            L10n.L("The Control Panel and Quit buttons are at the bottom right of the open pill.")));
        panel.Children.Add(Step([""], L10n.L("Tray icon"), L10n.L("The tray icon also opens the Control Panel.")));
        return panel;
    }

    /// A card with one line of text.
    private static Border Card(string text, bool centered = false) => new()
    {
        Style = (Style)Application.Current.FindResource("Card"),
        Padding = new Thickness(16, 12, 16, 12),
        Margin = new Thickness(0, 0, 0, 8),
        HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left,
        },
    };

    /// One how-to step of the last page: its icons in accent tiles, then its title and text, in a card.
    private Border Step(string[] glyphs, string title, string text)
    {
        var icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var glyph in glyphs)
        {
            var tile = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(16),
                Margin = new Thickness(0, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 15,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            tile.SetResourceReference(Border.BackgroundProperty, "AccentFillColorDefaultBrush");
            icons.Children.Add(tile);
        }

        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        words.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        words.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("CardNote"),
            Margin = new Thickness(0, 2, 0, 0),
            Text = text,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icons);
        row.Children.Add(words);
        return new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = row,
        };
    }

    /// The step dots, "Step 1/5", and the button texts. Back is hidden on the first page; Next reads Show me on the last.
    private void UpdateFooter()
    {
        Dots.Children.Clear();
        for (var i = 0; i <= LastPage; i++)
        {
            var dot = new Border
            {
                Height = 7,
                Width = i == _page ? 18 : 7,
                CornerRadius = new CornerRadius(3.5),
                Margin = new Thickness(0, 0, 6, 0),
            };
            dot.SetResourceReference(Border.BackgroundProperty,
                i == _page ? "AccentFillColorDefaultBrush" : "ControlStrongFillColorDefaultBrush");
            Dots.Children.Add(dot);
        }

        StepText.Text = L10n.L("Step {0}/{1}", _page + 1, LastPage + 1);
        SkipButton.Content = L10n.L("Skip");
        BackButton.Content = L10n.L("Back");
        BackButton.Visibility = _page > 0 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = _page == LastPage ? L10n.L("Show me") : L10n.L("Next");
    }

    /// The pages' texts follow the language at once: the page is drawn again (the panes follow it themselves).
    private void OnLanguageChanged()
    {
        if (Dispatcher.CheckAccess())
        {
            RedrawIfOpen();
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(RedrawIfOpen));
        }
    }

    private void RedrawIfOpen()
    {
        if (IsLoaded)
        {
            ShowPage(_page);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        L10n.Changed -= OnLanguageChanged;
        _window = null;
        if (_finished)
        {
            return;
        }
        _finished = true;

        try
        {
            Settings.Set("onboardingDone", JsonValue.Create(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write(new IOException("NotchTasks: could not save onboardingDone", ex));
        }
        _closed?.Invoke();
    }
}
