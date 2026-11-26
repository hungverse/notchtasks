using System.Windows.Media;

namespace NotchTasks;

/// The Microsoft Fluent System Icons fonts the app bundles (Resources/Fonts, MIT). The app draws every icon glyph from these, not
/// from the Segoe fonts of Windows, so an icon looks the same on every Windows build and the Mac's symbol has one Windows match.
public static class FluentIcons
{
    /// The outline icons: the font for every glyph unless it is named in ModuleGlyphs.FamilyOf as filled.
    public static readonly FontFamily Regular = new(FontsFolder(), "./#FluentSystemIcons-Regular");

    /// The filled icons: play and pause, which the Mac draws filled (ModuleGlyphs.FamilyOf picks this font for them).
    public static readonly FontFamily Filled = new(FontsFolder(), "./#FluentSystemIcons-Filled");

    /// The Resources/Fonts folder inside the app's own pack. Reading PackUriHelper first registers the pack:// scheme, so the
    /// Uri parses in a process where WPF has not yet started (the tests).
    private static Uri FontsFolder()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        return new Uri("pack://application:,,,/NotchTasks;component/Resources/Fonts/", UriKind.Absolute);
    }
}
