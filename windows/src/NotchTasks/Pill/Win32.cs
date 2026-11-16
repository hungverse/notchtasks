using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NotchTasks.Pill;

/// The Windows calls the pill needs. LibraryImport generates the marshalling (AllowUnsafeBlocks is on for the app).
internal static partial class Win32
{
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_NCHITTEST = 0x0084;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int WM_DPICHANGED = 0x02E0;

    public const int HTCLIENT = 1;
    public const int HTTRANSPARENT = -1;
    public const int MA_NOACTIVATE = 3;

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint SPI_GETWORKAREA = 0x0030;
    public const int SPI_SETWORKAREA = 0x002F;

    public const int WS_EX_TRANSPARENT = 0x00000020;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    /// HWND_TOPMOST: the window stays above the others.
    public static readonly nint HwndTopmost = new(-1);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ScreenToClient(nint hwnd, ref NativePoint point);

    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);


    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, out NativeRect area, uint winIni);
}

/// POINT in device pixels.
[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

/// RECT in device pixels.
[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// The Windows accent colour, which "auto" uses for the pill's accent.
internal static class SystemAccent
{
    /// "#RRGGBB" from HKCU\Software\Microsoft\Windows\DWM\AccentColor (stored as ABGR), or null when it cannot be read.
    public static string? Hex()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                return $"#{abgr & 0xFF:X2}{(abgr >> 8) & 0xFF:X2}{(abgr >> 16) & 0xFF:X2}";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Debug.WriteLine("NotchTasks: could not read the accent colour: " + e);
        }
        return null;
    }
}
