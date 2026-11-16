using System.Windows;
using System.Windows.Controls;
using NotchTasks.Onboarding;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class OnboardingWindowTests
{
    [Fact]
    public void PagesOneToFiveBuild()
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
                for (var page = 0; page < 5; page++)
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
}
