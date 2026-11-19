using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class PillWindowTests
{
    [Fact]
    public void PillConstructsAndLaysOut()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            // Not shown: Show places the pill on the real top edge of the screen. The content is measured and arranged
            // directly, which is the layout pass the XAML and its bindings need.
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

    [Fact]
    public void ToastView_Style_FollowsSettingAtShow()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            var toast = new Toast("hand", "#FF9F0A", "Agent wants to run", "echo hi");

            view.Show(toast, "compact", null);
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "CompactRow").Visibility);
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "WideBody").Visibility);

            view.Show(toast, "banner", null);
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "CompactRow").Visibility);
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "BannerBar").Visibility);
            Assert.Equal(520.0, view.Width);

            view.Show(toast, "island", null);
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "BannerBar").Visibility);
            Assert.Equal(340.0, view.Width);
        });
    }

    [Fact]
    public void ToastView_AskShowsDenyAllow_QuestionShowsReply()
    {
        Sta.Run(() =>
        {
            var view = new ToastView();
            var approval = new AgentAsk("a1", 1, "work", "Bash", "rm -rf build", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude");
            var question = new AgentAsk("q1", 1, "work", "AskUserQuestion", "Which one?",
                new[] { new AskQuestion("Pick one", null, new[] { "A", "B" }, false) }, DateTimeOffset.Now, "claude");

            view.Show(new Toast("hand", "#FF9F0A", "Run", "rm", AskId: "a1", Module: "agents"), "island", approval);
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "ApprovalButtons").Visibility);
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "ReplyButton").Visibility);

            view.Show(new Toast("question", "#FF9F0A", "Ask", "Which one?", AskId: "q1", Module: "agents"), "island", question);
            Assert.Equal(Visibility.Collapsed, Fixture.Named<UIElement>(view, "ApprovalButtons").Visibility);
            Assert.Equal(Visibility.Visible, Fixture.Named<UIElement>(view, "ReplyButton").Visibility);
        });
    }

    [Fact]
    public void Pill_ChipsFollowSnapshot()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                state.Asks.Add(new AgentAsk("c1", 1, "work", "Bash", "ls", Array.Empty<AskQuestion>(), DateTimeOffset.Now, "claude"));
                state.Publish();

                Assert.Equal(1, state.Snapshot.AskCount);
                Assert.True(HasText(Fixture.Named<UIElement>(pill, "TopRow"), "Waiting for you"));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void ChipView_TimerTrailShowsRemaining()
    {
        Sta.Run(() =>
        {
            var now = DateTimeOffset.Now;
            // The timer ends 4:59 after `now`, so 4:59 is left at `now`.
            var chip = new Chip(ChipKind.Timer, "timer", "timer")
            {
                Timer = new TimerState(now + TimeSpan.FromSeconds(299), TimeSpan.FromMinutes(5)),
            };
            var trail = Assert.IsType<TextBlock>(ChipView.Trail(chip, 18, Colors.Orange, new Dictionary<string, string>(), now));
            Assert.Equal("4:59", trail.Text);
        });
    }

    [Fact]
    public void Pill_EmptyOpen_ShowsNothingRunning()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            try
            {
                var placeholder = Fixture.Named<TextBlock>(pill, "CardText");
                Assert.Equal("Nothing running", placeholder.Text);
                Assert.Equal(Visibility.Visible, placeholder.Visibility);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    /// True when a text block under `node` shows `text`.
    private static bool HasText(DependencyObject node, string text)
    {
        if (node is TextBlock block && block.Text == text)
        {
            return true;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (HasText(VisualTreeHelper.GetChild(node, i), text))
            {
                return true;
            }
        }
        return false;
    }
}
