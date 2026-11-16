using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using NotchTasks.Core;

namespace NotchTasks.Services;

/// The tray icon and its menu: Control Panel, Show guide, a separator, Quit. A left double-click opens the Control Panel.
public sealed class Tray : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _panel;
    private readonly MenuItem _guide;
    private readonly MenuItem _quit;

    public Tray(Action openControlPanel, Action showGuide, Action onQuit)
    {
        _panel = new MenuItem();
        _panel.Click += (_, _) => openControlPanel();
        _guide = new MenuItem();
        _guide.Click += (_, _) => showGuide();
        _quit = new MenuItem();
        _quit.Click += (_, _) => onQuit();

        var menu = new ContextMenu();
        menu.Items.Add(_panel);
        menu.Items.Add(_guide);
        menu.Items.Add(new Separator());
        menu.Items.Add(_quit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "NotchTasks",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/NotchTasks.ico", UriKind.Absolute)),
            ContextMenu = menu,
        };
        _icon.TrayMouseDoubleClick += (_, _) => openControlPanel();
        _icon.ForceCreate();

        L10n.Changed += ApplyTexts;
        ApplyTexts();
    }

    private void ApplyTexts()
    {
        _panel.Header = L10n.L("Control Panel");
        _guide.Header = L10n.L("Show guide");
        _quit.Header = L10n.L("Quit");
    }

    /// Whether the tray icon is shown (the menuBarIcon setting). Hidden, it takes no room on the taskbar.
    public bool Visible
    {
        get => _icon.Visibility == Visibility.Visible;
        set => _icon.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Dispose()
    {
        L10n.Changed -= ApplyTexts;
        _icon.Dispose();
    }
}
