using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using NotchTasks.Core;

namespace NotchTasks.Services;

/// The tray icon and its menu. For now the menu has Quit only.
public sealed class Tray : IDisposable
{
    private readonly TaskbarIcon _icon;

    public Tray(Action onQuit)
    {
        var quit = new MenuItem { Header = L10n.L("Quit") };
        quit.Click += (_, _) => onQuit();

        var menu = new ContextMenu();
        menu.Items.Add(quit);

        _icon = new TaskbarIcon
        {
            ToolTipText = "NotchTasks",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/NotchTasks.ico", UriKind.Absolute)),
            ContextMenu = menu,
        };
        _icon.ForceCreate();
    }

    public void Dispose() => _icon.Dispose();
}
