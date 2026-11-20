using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using NotchTasks.Core.Motion;
using NotchTasks.Pill;
using Xunit;

namespace NotchTasks.AppTests;

/// The open pill's first frames: the card layer is warmed while the pointer is near (at zero opacity), an open fades the card in
/// without a blur, and a publish during an open takes effect once the open has finished.
public sealed class PillOpenWorkTests
{
    private static (PillWindow Pill, Grid CardLayer) Make(AppState state)
    {
        var pill = new PillWindow(state);
        return (pill, Fixture.Named<Grid>(pill, "CardLayer"));
    }

    [Fact]
    public void NearThePill_TheCardLayerIsWarmedAtZeroOpacity_AndReturnsWhenLeft()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var (pill, layer) = Make(state);
            try
            {
                Fixture.Call(pill, "SetPollRate", true);
                Assert.Equal(Visibility.Hidden, layer.Visibility);  // warmed by layout only: the card itself stays hidden

                Fixture.Call(pill, "SetPollRate", false);
                Assert.Equal(Visibility.Hidden, layer.Visibility);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void Open_DoesNotAttachTheCardBlur()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var (pill, layer) = Make(state);
            try
            {
                Fixture.Call(pill, "OpenPill");
                Fixture.Field<System.Windows.Threading.DispatcherTimer>(pill, "_closeTimer").Stop();  // the test pointer is not over the pill

                Assert.Null(layer.Effect);  // the card fades in on opacity alone
                Assert.IsNotType<BlurEffect>(layer.Effect);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void PublishDuringOpen_IsAppliedOnceAfterTheOpenFinishes()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            var (pill, _) = Make(state);
            try
            {
                Fixture.Call(pill, "OpenPill");
                Fixture.Field<System.Windows.Threading.DispatcherTimer>(pill, "_closeTimer").Stop();  // the test pointer is not over the pill
                var token = Fixture.Field<int>(pill, "_animation");

                // A timer starts while the open runs: its section changes the height, but the open is not retargeted yet.
                state.Timer.Start(5, DateTimeOffset.Now);
                state.Publish();
                Assert.True(Fixture.Field<bool>(pill, "_animating"));
                Assert.Equal(token, Fixture.Field<int>(pill, "_animation"));

                // The open finishes; the held change is applied once, as one more animation.
                Fixture.PumpUntil(() => Fixture.Field<int>(pill, "_animation") > token);
                Assert.Equal(token + 1, Fixture.Field<int>(pill, "_animation"));  // one retarget, not one per publish
            }
            finally
            {
                pill.Close();
            }
        });
    }
}

/// Follow-up: warming lays the card out but keeps it hidden; a publish with unchanged sections does not measure the open height again; a
/// section that grows while the pill is open retargets its height.
public sealed class PillOpenHeightTests
{
    [Fact]
    public void Warm_LayoutOnly_CardStaysHidden()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            var pill = new PillWindow(state);
            var layer = Fixture.Named<Grid>(pill, "CardLayer");
            try
            {
                Fixture.Call(pill, "SetPollRate", true);

                Assert.Equal(Visibility.Hidden, layer.Visibility);
                Assert.False(layer.IsVisible);  // nothing on the card runs while the pill is closed
                Assert.Equal(0, layer.Opacity);
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void PublishWithUnchangedSections_DoesNotMeasureAgain()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            state.Timer.Start(5, DateTimeOffset.Now);
            var pill = new PillWindow(state);
            Fixture.ShowOffScreen(pill);
            try
            {
                state.Publish();
                Fixture.Call(pill, "OpenPill");
                Fixture.Field<System.Windows.Threading.DispatcherTimer>(pill, "_closeTimer").Stop();  // the test pointer is not over the pill
                Fixture.PumpUntil(() => !Fixture.Field<bool>(pill, "_animating"));
                pill.UpdateLayout();
                Fixture.PumpUntil(() => false, 100);  // let the layout pass and its queued follow settle first
                var measured = Fixture.Field<int>(pill, "_openHeightMeasures");

                state.Publish();
                state.Publish();

                Assert.Equal(measured, Fixture.Field<int>(pill, "_openHeightMeasures"));
            }
            finally
            {
                pill.Close();
            }
        });
    }

    [Fact]
    public void SectionGrowsWhileOpen_RetargetsTheHeight()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("modules", JsonNode.Parse("[\"timer\"]"));
            state.Settings.Set("disabled", JsonNode.Parse("[]"));
            state.Timer.Start(5, DateTimeOffset.Now);
            var pill = new PillWindow(state);
            Fixture.ShowOffScreen(pill);
            try
            {
                state.Publish();
                Fixture.Call(pill, "OpenPill");
                Fixture.Field<System.Windows.Threading.DispatcherTimer>(pill, "_closeTimer").Stop();  // the test pointer is not over the pill
                Fixture.PumpUntil(() => !Fixture.Field<bool>(pill, "_animating"));
                pill.UpdateLayout();
                var before = Fixture.Field<double>(pill, "_targetHeight");

                // The section's content grows while the pill is open (a line of text in it, say).
                var list = Fixture.Named<StackPanel>(pill, "SectionList");
                list.Children.Add(new Border { Height = 60 });
                pill.UpdateLayout();

                Fixture.PumpUntil(() => Fixture.Field<double>(pill, "_targetHeight") > before, 5000);
                Assert.True(Fixture.Field<double>(pill, "_targetHeight") > before);
            }
            finally
            {
                pill.Close();
            }
        });
    }
}
