using System.Windows.Media;
using NotchTasks.Core;
using NotchTasks.Core.Asks;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Settings;
using NotchTasks.Pill.Sections;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The Devices row: a capsule per listed device, the lowest level in red at 20 % or less, the name when there is no level, and the
/// row is left alone while no device is listed.
public sealed class DevicesCardTests
{
    private static readonly Color LowRed = Color.FromRgb(0xFF, 0x45, 0x3A);

    private static AppState NewState()
    {
        var settings = SettingsStore.Load(AppPaths.SettingsFile);
        return new AppState(settings, new AskStore(AppPaths.AsksDir), Fixture.Inert(settings));
    }

    [Fact]
    public void Card_LowestRedAtOrBelow20()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var state = NewState();
            state.Devices.Update(new[]
            {
                new DeviceFact("{keyboard}", "Keyboard", new[] { "Input.Keyboard", "Bus.Usb" }, 20),
                new DeviceFact("{mouse}", "Mouse", new[] { "Input.Mouse", "Bus.Usb" }, 45),
            });
            var card = new DevicesCard();

            Assert.True(card.Update(state));

            var capsules = card.Capsules;
            Assert.Equal(2, capsules.Count);
            Assert.Equal("20%", capsules[0].Text);
            Assert.Equal(LowRed, ((SolidColorBrush)capsules[0].LabelBrush).Color);
            Assert.Equal("45%", capsules[1].Text);
            Assert.NotEqual(LowRed, ((SolidColorBrush)capsules[1].LabelBrush).Color);
            state.Dispose();
        });
    }

    [Fact]
    public void Card_NameWhenNoBattery()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var state = NewState();
            state.Devices.Update(new[]
            {
                new DeviceFact("{keyboard}", "Logitech Keyboard", new[] { "Input.Keyboard", "Bus.Usb" }, null),
            });
            var card = new DevicesCard();

            Assert.True(card.Update(state));

            var capsule = Assert.Single(card.Capsules);
            Assert.Equal("Logitech Keyboard", capsule.Text);
            Assert.Equal("Logitech Keyboard", capsule.ToolTip);
            state.Dispose();
        });
    }

    [Fact]
    public void Card_NoDevices_LeftAlone()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var state = NewState();
            var card = new DevicesCard();

            Assert.False(card.Update(state));
            Assert.Empty(card.Capsules);
            state.Dispose();
        });
    }
}
