using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// A toast's measured height (the pill's toast shape) must hold its content: a subtitle keeps the ask toast's text-to-edge distance.
public sealed class ToastLayoutTests
{
    /// The text-to-edge distance of the ask toast (its text row's bottom margin, 10 DIP). Every toast with a subtitle keeps at least this.
    private const double TextToEdge = 10;

    /// The measured height of a toast in its view, and the bottom of its subtitle inside the view (DIP).
    private static (double Height, double SubtitleBottom) Layout(ToastView view)
    {
        view.Measure(new Size(view.Width, double.PositiveInfinity));
        var height = view.DesiredSize.Height;
        view.Arrange(new Rect(0, 0, view.Width, height));
        view.UpdateLayout();
        var subtitle = Fixture.Named<TextBlock>(view, "SubtitleText");
        var bottom = subtitle.TransformToAncestor(view).TransformBounds(new Rect(new Point(0, 0), subtitle.RenderSize)).Bottom;
        return (height, bottom);
    }

    [Fact]
    public void EveryToastWithASubtitle_HoldsItsTextToTheEdge_WhetherOrNotAnotherToastWasShownBefore()
    {
        Sta.Run(() =>
        {
            var ask = new AgentAsk("ask-1", 1, "web-shop", "Bash", "git push origin main", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude");
            var askToast = new Toast("hand", "#FF9F0A", "Claude needs your approval", "web-shop · Run command: git push origin main",
                Sticky: true, AskId: ask.Id);
            var done = new Toast("check", "#30D158", "Pushed to origin/main · web-shop");
            var device = new Toast("headphones", "#30D158", "AirPods Pro connected", "L 100% · R 98% · Case 62%");
            var deviceArt = new Toast("headphones", "#30D158", "AirPods Pro · 80%", "Connected", Art: new DeviceArt("airpodspro", true));
            var charge = new Toast("battery.100percent.bolt", "#30D158", "Charging · 64%", "Plugged in");
            var kinds = new (Toast Toast, string Style, AgentAsk? Ask)[]
            {
                (askToast, "island", ask),
                (device, "island", null),
                (deviceArt, "island", null),
                (charge, "island", null),
                (device, "card", null),
                (device, "banner", null),
            };

            foreach (var previous in kinds)
            {
                foreach (var next in kinds)
                {
                    var reused = new ToastView();
                    reused.Show(previous.Toast, previous.Style, previous.Ask, "bouncy");
                    reused.Measure(new Size(reused.Width, double.PositiveInfinity));
                    reused.Show(next.Toast, next.Style, next.Ask, "bouncy");

                    var (height, subtitleBottom) = Layout(reused);
                    var (freshHeight, _) = Layout(FreshView(next.Toast, next.Style, next.Ask));

                    Assert.True(height - subtitleBottom >= TextToEdge - 0.01,
                        $"{next.Toast.Title} ({next.Style}) after {previous.Toast.Title} ({previous.Style}): "
                        + $"the subtitle is {height - subtitleBottom:0.0} DIP from the bottom");
                    Assert.Equal(freshHeight, height, 0.01);
                }
            }
        });
    }

    private static ToastView FreshView(Toast toast, string style, AgentAsk? ask)
    {
        var view = new ToastView();
        view.Show(toast, style, ask, "bouncy");
        return view;
    }
}
