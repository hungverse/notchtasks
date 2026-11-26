using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using NotchTasks.Pill;

namespace NotchTasks.ControlPanel;

/// The Control Panel's window backdrop, as the Windows 11 Settings app has it: Mica, the wallpaper tinted through the
/// window, on Windows 11 (build 22000 and later). Older builds have no Mica, so the window keeps the theme's solid
/// background. The title bar follows light and dark mode on every build that has the immersive dark-mode attribute.
internal static class Backdrop
{
    /// The first Windows 11 build: Mica and DWMWA_SYSTEMBACKDROP_TYPE exist from it.
    private const int Windows11Build = 22000;

    /// DWMWA_USE_IMMERSIVE_DARK_MODE: 1 makes the title bar dark, 0 light.
    private const int UseImmersiveDarkMode = 20;

    /// DWMWA_SYSTEMBACKDROP_TYPE: the backdrop the window's frame shows.
    private const int SystemBackdropType = 38;

    /// DWMSBT_MAINWINDOW (2): Mica, the backdrop for a long-lived main window.
    private const int MainWindowBackdrop = 2;

    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// Gives `window` its backdrop once its handle exists. Returns true when Mica is shown, so the window's own background
    /// must then be transparent where Mica shows; false keeps the theme's solid background.
    public static bool Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero)
        {
            return false;
        }

        UpdateDarkMode(window);
        if (!IsWindows11() || !TrySetMica(hwnd))
        {
            return false;
        }

        // Mica is drawn behind the client area: the frame is extended over all of it, and WPF's own surface is clear.
        var margins = new NativeMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
        {
            target.BackgroundColor = Colors.Transparent;
        }
        return true;
    }

    /// Re-applies the title bar's dark mode to the system's current light or dark theme. Called when the theme changes.
    public static void UpdateDarkMode(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }
        int dark = SystemUsesDarkTheme() ? 1 : 0;
        // An older build ignores the attribute: its HRESULT is not a failure the window can act on.
        Win32.DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref dark, sizeof(int));
    }

    /// True when the Windows apps' theme is dark (AppsUseLightTheme is 0). A missing value counts as light, as Windows does.
    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Debug.WriteLine("NotchTasks: could not read the theme: " + e);
            return false;
        }
    }

    /// True on Windows 11 (build 22000 and later): the build number, not the marketing name, decides.
    public static bool IsWindows11() => Environment.OSVersion.Version.Build >= Windows11Build;

    private static bool TrySetMica(nint hwnd)
    {
        int backdrop = MainWindowBackdrop;
        return Win32.DwmSetWindowAttribute(hwnd, SystemBackdropType, ref backdrop, sizeof(int)) == 0;
    }
}
