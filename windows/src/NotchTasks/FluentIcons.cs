using System.Windows.Media;

namespace NotchTasks;

/// The Microsoft Fluent System Icons fonts the app bundles (Resources/Fonts, MIT). The app draws every icon glyph from these, not
/// from the Segoe fonts of Windows, so an icon looks the same on every Windows build and the Mac's symbol has one Windows match.
public static class FluentIcons
{
    /// The outline icons: the font for every glyph that the Mac draws as an outline.
    public static readonly FontFamily Regular = new(FontsFolder(), "./#FluentSystemIcons-Regular");

    /// The filled icons: the glyphs the Mac draws filled (ModuleGlyphs.Get names the family of each).
    public static readonly FontFamily Filled = new(FontsFolder(), "./#FluentSystemIcons-Filled");

    /// The Resources/Fonts folder inside the app's own pack. Reading PackUriHelper first registers the pack:// scheme, so the
    /// Uri parses in a process where WPF has not yet started (the tests).
    private static Uri FontsFolder()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        return new Uri("pack://application:,,,/NotchTasks;component/Resources/Fonts/", UriKind.Absolute);
    }
}
