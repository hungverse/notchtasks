using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Media;
using NotchTasks.Core.Settings;
using NotchTasks.Pill;
using NotchTasks.Services;
using Xunit;
using Xunit.Abstractions;

namespace NotchTasks.AppTests;

/// The open pill's rows sit inside the open shape: 16 DIP from each side of the shape, and the quick bar 12 DIP above its
/// bottom, whatever sections are open (the Mac's expandedView padding). The shape's rounded corners cut anything closer.
public sealed class PillOpenInsetTests
{
    private const double SideInset = 16;
    private const double BarBottomInset = 12;
    private const double Epsilon = 0.5;
    private readonly ITestOutputHelper _output;

    public PillOpenInsetTests(ITestOutputHelper output) => _output = output;

    private static AppState NewState(FakeDeviceReader devices, params string[] modules)
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        var state = new AppState(settings, new AskStore(AppPaths.AsksDir),
            Fixture.Inert(settings) with { Media = Reader(), Devices = devices });
        state.Settings.Set("modules", JsonNode.Parse("[" + string.Join(",", modules.Select(m => "\"" + m + "\"")) + "]"));
        state.Settings.Set("disabled", JsonNode.Parse("[]"));
        return state;
    }

    /// A reader that lists one playing session, so the Now Playing card shows.
    private static FakeMediaReader Reader() => new()
    {
        Sessions =
        {
            new MediaSessionFact("spotify.exe", "Song", "Artist", MediaStatus.Playing, 30, 200, 0, true, true, true, true, "key", IsCurrent: true),
        },
    };

    private static DeviceFact Keyboard() => new("{keyboard}", "Keyboard", new[] { "Input.Keyboard", "Bus.Usb" }, 20);

    /// Opens the pill and lays it out as it settles, with the shape in its open rectangle.
    private static void OpenAndSettle(PillWindow pill)
    {
        Fixture.Call(pill, "OpenPill");
        Fixture.Field<System.Windows.Threading.DispatcherTimer>(pill, "_closeTimer").Stop();  // the test pointer is not over the pill
        Settle(pill);
        Arrange(pill);
    }

    /// The element's box in the pill shape's own space (the origin is the shape layer's top-left corner).
    private static Rect BoxInShape(PillWindow pill, FrameworkElement element)
    {
        var shape = Fixture.Named<UIElement>(pill, "PillShape");
        var origin = element.TransformToAncestor(shape).Transform(new Point(0, 0));
        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static Rect OpenShape(PillWindow pill) => Fixture.Field<RectangleGeometry>(pill, "_shape").Rect;

    private void AssertInside(Rect shape, Rect box, string name)
    {
        var left = box.Left - shape.Left;
        var right = shape.Right - box.Right;
        _output.WriteLine($"{name}: left {left:F1}, right {right:F1}, top {box.Top - shape.Top:F1}, bottom {shape.Bottom - box.Bottom:F1}");
        Assert.True(left >= SideInset - Epsilon, $"{name} is {left:F1} DIP from the shape's left edge");
        Assert.True(right >= SideInset - Epsilon, $"{name} is {right:F1} DIP from the shape's right edge");
        Assert.True(box.Top >= shape.Top - Epsilon, $"{name} is above the shape");
        Assert.True(box.Bottom <= shape.Bottom + Epsilon, $"{name} is below the shape");
    }

    [Fact]
    public void OpenPill_EveryRowIsInsideTheShape()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var devices = new FakeDeviceReader { Devices = { Keyboard() } };
            using var state = NewState(devices, "media", "devices", "agents", "mirror");
            var pill = new PillWindow(state);
            try
            {
                state.Asks.Add(new AgentAsk("c1", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude"));
                state.Publish();
                devices.Raise();
                Fixture.PumpUntil(() => state.Media.Current is not null && state.Devices.Devices.Count > 0);
                state.Publish();
                ShowAway(pill);
                OpenAndSettle(pill);

                var shape = OpenShape(pill);
                var header = Fixture.Named<FrameworkElement>(pill, "HeaderSummary");
                var meters = Fixture.Named<FrameworkElement>(pill, "LoadMeters");
                var bar = Fixture.Named<FrameworkElement>(pill, "Bar");
                var mirror = Fixture.Named<FrameworkElement>(bar, "MirrorButton");
                var gear = Fixture.Named<FrameworkElement>(bar, "ControlPanelButton");
                Assert.Equal(Visibility.Visible, mirror.Visibility);

                AssertInside(shape, BoxInShape(pill, header), "HeaderSummary");
                meters.Visibility = Visibility.Visible;  // the CPU and RAM meters, shown while SystemLoad is on
                Arrange(pill);
                AssertInside(shape, BoxInShape(pill, meters), "LoadMeters");
                var sections = Fixture.Named<StackPanel>(pill, "SectionList");
                Assert.True(sections.Children.Count >= 3,
                    $"the media card, the devices row and the ask card are open ({sections.Children.Count} sections)");
                for (var i = 0; i < sections.Children.Count; i++)
                {
                    AssertInside(shape, BoxInShape(pill, (FrameworkElement)sections.Children[i]), $"section {i}");
                }

                AssertInside(shape, BoxInShape(pill, bar), "Bar");
                AssertInside(shape, BoxInShape(pill, mirror), "Mirror chip");
                AssertInside(shape, BoxInShape(pill, gear), "Control Panel button");
                var barBottom = shape.Bottom - BoxInShape(pill, bar).Bottom;
                Assert.True(barBottom >= BarBottomInset - Epsilon, $"the Bar is {barBottom:F1} DIP above the shape's bottom");
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void OpenPill_SectionAddedWhileOpen_TheShapeGrowsToHoldTheBar()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var devices = new FakeDeviceReader();
            using var state = NewState(devices, "media", "devices");
            var pill = new PillWindow(state);
            try
            {
                state.Publish();
                Fixture.PumpUntil(() => state.Media.Current is not null);
                state.Publish();
                ShowAway(pill);
                OpenAndSettle(pill);
                var bar = Fixture.Named<FrameworkElement>(pill, "Bar");
                var before = OpenShape(pill).Bottom;
                Assert.Equal(before - BarBottomInset, BoxInShape(pill, bar).Bottom, 0.5);

                devices.Devices.Add(Keyboard());  // the Devices row arrives while the pill is open
                devices.Raise();
                Fixture.PumpUntil(() => state.Devices.Devices.Count > 0);
                state.Publish();
                Fixture.PumpUntil(() => OpenShape(pill).Bottom > before + 1);
                Settle(pill);
                Arrange(pill);

                var shape = OpenShape(pill);
                var gap = shape.Bottom - BoxInShape(pill, bar).Bottom;
                _output.WriteLine($"after the devices row: shape bottom {shape.Bottom:F1}, Bar gap {gap:F1}");
                Assert.Equal(BarBottomInset, gap, 0.5);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    private static void ShowAway(PillWindow pill)
    {
        Fixture.ShowOffScreen(pill);
        pill.Left = -10000;
        pill.Top = -10000;
    }

    private static void Settle(PillWindow pill) => Fixture.PumpUntil(() => !Fixture.Field<bool>(pill, "_animating"));

    private static void Arrange(PillWindow pill)
    {
        var content = Assert.IsAssignableFrom<UIElement>(pill.Content);
        content.Measure(new Size(1200, 600));
        content.Arrange(new Rect(0, 0, 1200, 600));
        content.UpdateLayout();
    }
}
