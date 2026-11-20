using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// A publish that leaves the chips as they are does not take a kept chip out of its wing: the element stays, and it gets no Unloaded
/// (a loop in it, such as the media bars, would otherwise restart on every publish).
public sealed class ChipViewReconcileTests
{
    [Fact]
    public void KeptChip_NotReloadedOnPublish()
    {
        Sta.Run(() =>
        {
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var host = new StackPanel { Orientation = Orientation.Horizontal };
            host.Children.Add(left);
            host.Children.Add(right);
            var window = new Window { Content = host, Width = 400, Height = 60 };
            Fixture.ShowOffScreen(window);

            var now = DateTimeOffset.Now;
            var call = new CallInfo("Teams", "Team Meeting", now.AddMinutes(-1), false, false);
            var chips = new[]
            {
                new Chip(ChipKind.Call, "calls", "call") { Call = call },
                new Chip(ChipKind.Alerts, "ci", "alerts") { AlertCount = 1 },
            };
            var placements = new Dictionary<string, string>(StringComparer.Ordinal);
            var colors = new Dictionary<string, string>(StringComparer.Ordinal);

            ChipView.Fill(left, right, chips, placements, colors, null, now, null, animationsOn: false);
            var kept = Assert.Single(left.Children.OfType<FrameworkElement>(), e => e.Tag is "chip");
            var unloads = 0;
            kept.Unloaded += (_, _) => unloads++;

            ChipView.Fill(left, right, chips, placements, colors, null, now.AddSeconds(1), null, animationsOn: false);

            Assert.Same(kept, left.Children.OfType<FrameworkElement>().Single(e => e.Tag is "chip"));
            Assert.Equal(0, unloads);
            window.Close();
        });
    }

    /// Two wings side by side, and a fill that pins every chip to one of them. Returns the wing's chip panels, in order.
    private static (StackPanel Wing, Func<IReadOnlyList<Chip>, IReadOnlyList<FrameworkElement>> Fill, Window Window) Pinned(string side)
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        var host = new StackPanel { Orientation = Orientation.Horizontal };
        host.Children.Add(left);
        host.Children.Add(right);
        var window = new Window { Content = host, Width = 600, Height = 60 };
        Fixture.ShowOffScreen(window);
        var placements = new Dictionary<string, string>(StringComparer.Ordinal) { ["calls"] = side, ["ci"] = side };
        var colors = new Dictionary<string, string>(StringComparer.Ordinal);
        var now = DateTimeOffset.Now;
        var wing = side == "left" ? left : right;
        IReadOnlyList<FrameworkElement> Fill(IReadOnlyList<Chip> chips)
        {
            ChipView.Fill(left, right, chips, placements, colors, null, now, null, animationsOn: false);
            return wing.Children.OfType<FrameworkElement>().Where(e => e.Tag is "chip").ToList();
        }
        return (wing, Fill, window);
    }

    private static Chip Call() => new(ChipKind.Call, "calls", "call")
    {
        Call = new CallInfo("Teams", "Team Meeting", DateTimeOffset.Now.AddMinutes(-1), false, false),
    };

    private static Chip Alert() => new(ChipKind.Alerts, "ci", "alerts") { AlertCount = 1 };

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public void NewChipBeforeKept_InsertedInOrder(string side)
    {
        Sta.Run(() =>
        {
            var (_, fill, window) = Pinned(side);
            var kept = fill(new[] { Call() })[0];

            // the new alert comes before the kept call in the chip order: it must land before it in the wing, not after
            var chips = fill(new[] { Alert(), Call() });

            Assert.Equal(2, chips.Count);
            Assert.NotSame(kept, chips[0]);  // the new alert is first
            Assert.Same(kept, chips[1]);     // the kept call is after it, and did not move
            window.Close();
        });
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public void NewChipBeforeKept_NextPublish_KeptNotReloaded(string side)
    {
        Sta.Run(() =>
        {
            var (_, fill, window) = Pinned(side);
            var kept = fill(new[] { Call() })[0];
            var unloads = 0;
            kept.Unloaded += (_, _) => unloads++;
            var first = fill(new[] { Alert(), Call() });
            var alertPanel = first[0];

            var second = fill(new[] { Alert(), Call() });

            Assert.Same(alertPanel, second[0]);
            Assert.Same(kept, second[1]);
            Assert.Equal(0, unloads);
            window.Close();
        });
    }
}
