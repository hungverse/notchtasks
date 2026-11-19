using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Shapes;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// Every glyph name a Core module puts on a toast draws something: a font glyph, or the drawn path of a name the font lacks.
public sealed class ToastGlyphTests
{
    /// The glyph names the Core modules give their toasts, read from the Core source rather than typed here: every string
    /// literal of a `Glyph: …` or `Glyph = …` assignment (a ternary gives both of its names), and of the first argument of a
    /// `new Toast(…)` (the Battery and Timer toasts pass their glyph that way).
    private static readonly Lazy<string[]> CoreToastGlyphs = new(ScanCoreToastGlyphs);

    private static readonly Regex NamedGlyph = new(@"Glyph\s*[:=]\s*([^,;\r\n]+)", RegexOptions.CultureInvariant);
    private static readonly Regex PositionalGlyph = new(@"new Toast\(\s*([^,]+),", RegexOptions.CultureInvariant);
    private static readonly Regex Literal = new("\"([^\"\\r\\n]+)\"", RegexOptions.CultureInvariant);

    private static string[] ScanCoreToastGlyphs()
    {
        var core = System.IO.Path.Combine(RepoRoot(), "windows", "src", "NotchTasks.Core");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in System.IO.Directory.EnumerateFiles(core, "*.cs", System.IO.SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = System.IO.File.ReadAllText(file);
            foreach (var match in NamedGlyph.Matches(text).Concat(PositionalGlyph.Matches(text)))
            {
                foreach (Match literal in Literal.Matches(match.Groups[1].Value))
                {
                    names.Add(literal.Groups[1].Value);
                }
            }
        }
        return names.ToArray();
    }

    /// The folder that holds mac/ and windows/, found from the test's own folder.
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "mac", "Sources")))
        {
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);
        return dir!;
    }

    [Fact]
    public void CoreToastGlyphs_AreScannedFromTheSource_AndIncludeEveryModuleGlyph()
    {
        // The scan finds what the modules use, including the names given as a ternary and as a positional argument.
        var names = CoreToastGlyphs.Value;
        foreach (var expected in new[] { "check", "gauge", "hourglass", "wifi", "network", "vpnoff", "bolt", "plug", "batterylow", "timer" })
        {
            Assert.Contains(expected, names);
        }
        Assert.True(names.Length >= 19, $"only {names.Length} glyph names were found");
    }

    [Fact]
    public void EveryCoreToastGlyph_ResolvesToAFontGlyphOrAVector_NeverTheFallback()
    {
        foreach (var name in CoreToastGlyphs.Value)
        {
            var isVector = ModuleGlyphs.Vector(name) is not null;
            var isFont = ModuleGlyphs.Get(name) != ModuleGlyphs.Fallback;
            Assert.True(isVector || isFont, name);
        }
    }

    [Fact]
    public void LimitToast_DrawsItsGaugeAsAPath()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            view.Show(new Toast("gauge", "#FF9F0A", "Claude 95%"), "compact", null, "bouncy");
            Assert.IsType<Path>(Fixture.Named<ContentControl>(view, "CompactGlyph").Content);
        });
    }

    [Fact]
    public void FontToast_KeepsItsTextGlyph()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            view.Show(new Toast("check", "#30D158", "Done"), "island", null, "bouncy");
            var glyph = Assert.IsType<TextBlock>(Fixture.Named<ContentControl>(view, "TopGlyph").Content);
            Assert.Equal(ModuleGlyphs.Get("check"), glyph.Text);
        });
    }
}
