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
}
