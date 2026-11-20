using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
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
                Assert.Equal(Visibility.Visible, layer.Visibility);
                Assert.Equal(0, layer.Opacity);

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
