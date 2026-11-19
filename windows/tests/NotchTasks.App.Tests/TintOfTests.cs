using System.Text.Json.Nodes;
using System.Windows.Media;
using NotchTasks.Core.Notch;
using NotchTasks.Pill;
using NotchTasks.Pill.Sections;
using Xunit;

namespace NotchTasks.AppTests;

public sealed class TintOfTests
{
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color Chosen = Color.FromRgb(0x11, 0x22, 0x33);
    private static readonly Color Green = Color.FromRgb(0x00, 0xFF, 0x00);
    private static readonly Color CallDefault = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly IReadOnlyDictionary<string, string> NoColors = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, string> TimerGreen = new Dictionary<string, string> { ["timer"] = "#00FF00" };
    private static readonly Chip TimerChip = new(ChipKind.Timer, "timer", "timer");

    [Fact]
    public void TimerChip_AutoAccent_IsOrange()
    {
        Assert.Equal(Orange, ChipView.TintOf(TimerChip, null, NoColors));
    }

    [Fact]
    public void TimerChip_ChosenAccent_IsTheAccent()
    {
        Assert.Equal(Chosen, ChipView.TintOf(TimerChip, Chosen, NoColors));
    }

    [Fact]
    public void TimerChip_ModuleColor_WinsOverAccent()
    {
        Assert.Equal(Green, ChipView.TintOf(TimerChip, Chosen, TimerGreen));
    }

    [Fact]
    public void CallChip_AutoAccent_KeepsItsOwnDefault()
    {
        Assert.Equal(CallDefault, ChipView.TintOf(new Chip(ChipKind.Call, "calls", "call"), null, NoColors));
    }

    [Fact]
    public void TimerCard_AutoAccent_IsOrange()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            Assert.Equal(Orange, TimerCard.TintOf(state, null));
        });
    }

    [Fact]
    public void TimerCard_ChosenAccent_IsTheAccent()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            Assert.Equal(Chosen, TimerCard.TintOf(state, Chosen));
        });
    }

    [Fact]
    public void TimerCard_ModuleColor_WinsOverAccent()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            using var state = Fixture.NewState();
            state.Settings.Set("moduleColors", JsonNode.Parse("{\"timer\":\"#00FF00\"}"));
            Assert.Equal(Green, TimerCard.TintOf(state, Chosen));
        });
    }

    [Fact]
    public void CallTime_NoModuleColor_IsGreen_AndRedWhenMuted()
    {
        Assert.Equal(CallDefault, ChipView.CallTimeTint(false, NoColors));
        Assert.Equal(Color.FromRgb(0xFF, 0x45, 0x3A), ChipView.CallTimeTint(true, NoColors));
    }

    [Fact]
    public void CallTime_ModuleColor_WinsWhenNotMuted()
    {
        var blue = new Dictionary<string, string> { ["calls"] = "#0A84FF" };
        Assert.Equal(Color.FromRgb(0x0A, 0x84, 0xFF), ChipView.CallTimeTint(false, blue));
        // Muted stays red, as the Mac's `c.muted ? .red : color(for: .calls) ?? .green`.
        Assert.Equal(Color.FromRgb(0xFF, 0x45, 0x3A), ChipView.CallTimeTint(true, blue));
    }
}
