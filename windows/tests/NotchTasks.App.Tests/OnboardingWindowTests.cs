using System.Windows;
using System.Windows.Controls;
using NotchTasks.Core;
using NotchTasks.Onboarding;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class OnboardingWindowTests
{
    [Fact]
    public void GuideClosedByQuit_IsNotFinished_AndShowsNoPill()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var closedRan = false;
            var window = Fixture.Create<OnboardingWindow>(state, new Action(() => closedRan = true));
            Fixture.ShowOffScreen(window);

            // The quit sets App.Quitting before the windows close: the guide stays unfinished, and the pill is not shown.
            App.Quitting = true;
            try
            {
                window.Close();
            }
            finally
            {
                App.Quitting = false;
            }

            Assert.False(state.Settings.OnboardingDone);
            Assert.False(closedRan);
        });
    }

    [Fact]
    public void GuideClosedNormally_IsFinished_AndShowsThePill()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var closedRan = false;
            var window = Fixture.Create<OnboardingWindow>(state, new Action(() => closedRan = true));
            Fixture.ShowOffScreen(window);

            window.Close();

            Assert.True(state.Settings.OnboardingDone);
            Assert.True(closedRan);
        });
    }

    [Fact]
    public void PagesOneToSixBuild()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<OnboardingWindow>(state, null);
            try
            {
                Fixture.ShowOffScreen(window);
                var host = Fixture.Named<ContentControl>(window, "PageHost");

                // Page 1 (index 0) is built by the constructor; the rest are shown in turn.
                for (var page = 0; page < 6; page++)
                {
                    Fixture.Call(window, "ShowPage", page);
                    window.UpdateLayout();
                    Assert.NotNull(host.Content);
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Onboarding_PhonePage_BeforeReady()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var window = Fixture.Create<OnboardingWindow>(state, null);
            try
            {
                Fixture.ShowOffScreen(window);
                var host = Fixture.Named<ContentControl>(window, "PageHost");

                // Page 5 (index 4) is the phone page; the last page (index 5) is the ready page.
                Fixture.Call(window, "ShowPage", 4);
                window.UpdateLayout();
                Assert.Contains(L10n.L("Open Pairing"), PairingVisuals.Texts((DependencyObject)host.Content));
                Assert.Contains(L10n.L("Phone (optional)"), PairingVisuals.Texts((DependencyObject)host.Content));

                // The heading is visible: the guide's pages have no window title to carry it.
                var heading = PairingVisuals.Find<TextBlock>((DependencyObject)host.Content, text => text.Text == L10n.L("Phone (optional)"));
                Assert.Equal(Visibility.Visible, heading.Visibility);

                Fixture.Call(window, "ShowPage", 5);
                window.UpdateLayout();
                Assert.Equal(L10n.L("Show me"), Fixture.Named<Button>(window, "NextButton").Content);
                Assert.Equal(L10n.L("Step {0}/{1}", 6, 6), Fixture.Named<TextBlock>(window, "StepText").Text);
            }
            finally
            {
                window.Close();
            }
        });
    }
}
