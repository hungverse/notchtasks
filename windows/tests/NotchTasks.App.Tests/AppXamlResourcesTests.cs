using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

/// The resources of the real App.xaml, as the app has them at start-up. The shared host loads App.xaml itself (WpfHost), so these
/// tests see what the app sees: a broken App.xaml fails them, and a window that reads a resource App.xaml does not define fails too.
public sealed class AppXamlResourcesTests
{
    [Fact]
    public void AppXaml_DefinesTheFluentFamiliesAsTheFontFamiliesOfFluentIcons()
    {
        Sta.Run(() =>
        {
            var resources = Application.Current.Resources;
            var regular = Assert.IsType<FontFamily>(resources["FluentRegular"]);
            var filled = Assert.IsType<FontFamily>(resources["FluentFilled"]);
            Assert.Same(FluentIcons.Regular, regular);
            Assert.Same(FluentIcons.Filled, filled);
        });
    }

    [Fact]
    public void TimerCard_ConstructsWithTheAppResources_AndTheGlyphUsesTheRegularFamily()
    {
        Sta.Run(() =>
        {
            var card = Fixture.Create<TimerCard>();
            var glyph = Fixture.Named<FluentGlyph>(card, "Glyph");
            Assert.False(glyph.Filled);  // the timer glyph is the Regular font
        });
    }

    [Fact]
    public void PillWindow_ConstructsWithTheAppResources()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // Not shown: the content is measured and arranged, which is the layout pass the XAML and its bindings need.
            var pill = new PillWindow(state);
            try
            {
                var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
                content.Measure(new Size(1200, 600));
                content.Arrange(new Rect(0, 0, 1200, 600));
                Assert.True(content.IsArrangeValid);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
