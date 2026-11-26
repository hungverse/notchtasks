using System.IO;
using System.Text.Json;

namespace NotchTasks.AppTests;

/// The Fluent System Icons maps (name to codepoint) of the two bundled fonts, read from the source tree
/// (windows/src/NotchTasks/Resources/Fonts). The fonts ship in the app; their maps are not embedded.
public static class IconFonts
{
    private static readonly string FontsFolder = FindFontsFolder();

    /// The windows/src folder, the root every source scan starts from.
    public static string SourceRoot { get; } = Path.GetFullPath(Path.Combine(FontsFolder, "..", "..", ".."));

    /// The Regular font's map: "ic_fluent_<name>_<size>_regular" to the codepoint.
    public static IReadOnlyDictionary<string, int> Regular { get; } = Load("FluentSystemIcons-Regular.json");

    /// The Filled font's map: "ic_fluent_<name>_<size>_filled" to the codepoint.
    public static IReadOnlyDictionary<string, int> Filled { get; } = Load("FluentSystemIcons-Filled.json");

    /// The glyph a map key draws, as the one-character string the app's Text holds.
    public static string GlyphOf(int codepoint) => char.ConvertFromUtf32(codepoint);

    private static Dictionary<string, int> Load(string file)
    {
        using var stream = File.OpenRead(Path.Combine(FontsFolder, file));
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
    }

    private static string FindFontsFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "windows", "src", "NotchTasks", "Resources", "Fonts");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("windows/src/NotchTasks/Resources/Fonts is not above " + AppContext.BaseDirectory);
    }
}
