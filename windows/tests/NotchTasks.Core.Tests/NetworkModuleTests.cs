using System.Net.NetworkInformation;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The network module ports the Mac's networkChanged (Model.swift): the first reading is the baseline, an online change
/// is confirmed after 2 s, and a VPN switch is a quiet compact toast. The judge reads the interfaces as the Windows form.
public class NetworkModuleTests
{
    /// An announcer that keeps every toast with its notify flag.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<(Toast Toast, bool Notify)> Calls { get; } = new();

        public void Announce(Toast t, bool notify = true) => Calls.Add((t, notify));
    }

    /// The Mac's key uses a non-breaking hyphen (U+2011) in "Wi-Fi".
    private const string NbHyphen = "‑";

    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static (bool Online, bool Wifi, bool Vpn) Net(bool online, bool wifi = false, bool vpn = false) =>
        (online, wifi, vpn);

    [Fact]
    public void FirstReading_IsBaseline_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new NetworkModule(announcer);

        module.Observe(Net(true, wifi: true, vpn: true), T0);

        Assert.Empty(announcer.Calls);
        Assert.Null(module.PendingDue);
    }

    [Fact]
    public void Offline_ConfirmedAfter2s_AnnouncesDown()
    {
        var announcer = new RecordingAnnouncer();
        var module = new NetworkModule(announcer);
        module.Observe(Net(true), T0);

        module.Observe(Net(false), T0.AddSeconds(1));
        Assert.Equal(T0.AddSeconds(3), module.PendingDue);
        module.Tick(T0.AddSeconds(2));
        Assert.Empty(announcer.Calls);

        module.Tick(T0.AddSeconds(3));

        var (toast, notify) = Assert.Single(announcer.Calls);
        Assert.True(notify);
        Assert.Equal("wifioff", toast.Glyph);
        Assert.Equal("#FF453A", toast.Tint);
        Assert.Equal("Network disconnected", toast.Title);
        Assert.Equal("Check Wi" + NbHyphen + "Fi or the network cable", toast.Subtitle);
        Assert.Equal("netDown", toast.Event);
        Assert.False(toast.Compact);
        Assert.Equal("network", toast.Module);
        Assert.Null(module.PendingDue);
    }

    [Fact]
    public void Blip_Under2s_NoToast()
    {
        var announcer = new RecordingAnnouncer();
        var module = new NetworkModule(announcer);
        module.Observe(Net(true), T0);

        module.Observe(Net(false), T0);
        module.Observe(Net(true), T0.AddSeconds(1));
        module.Tick(T0.AddSeconds(1.5));
        module.Tick(T0.AddSeconds(2));

        Assert.Empty(announcer.Calls);
        Assert.Null(module.PendingDue);
    }

    [Theory]
    [InlineData(true, "wifi", "Wi" + NbHyphen + "Fi connected")]
    [InlineData(false, "network", "Connected")]
    public void BackOnline_AnnouncesCompact(bool wifi, string glyph, string subtitle)
    {
        var announcer = new RecordingAnnouncer();
        var module = new NetworkModule(announcer);
        module.Observe(Net(false), T0);

        module.Observe(Net(true, wifi), T0.AddSeconds(1));
        module.Tick(T0.AddSeconds(3));

        var (toast, notify) = Assert.Single(announcer.Calls);
        Assert.False(notify);
        Assert.Equal(glyph, toast.Glyph);
        Assert.Equal("#30D158", toast.Tint);
        Assert.Equal("Back online", toast.Title);
        Assert.Equal(subtitle, toast.Subtitle);
        Assert.Equal("netUp", toast.Event);
        Assert.True(toast.Compact);
        Assert.Equal("network", toast.Module);
    }

    [Fact]
    public void VpnSwitch_AnnouncesQuietCompact()
    {
        var announcer = new RecordingAnnouncer();
        var module = new NetworkModule(announcer);
        module.Observe(Net(true, wifi: true, vpn: false), T0);

        module.Observe(Net(true, wifi: true, vpn: true), T0.AddSeconds(1));

        var (on, onNotify) = Assert.Single(announcer.Calls);
        Assert.False(onNotify);
        Assert.Equal("vpn", on.Glyph);
        Assert.Equal("#0A84FF", on.Tint);
        Assert.Equal("VPN on", on.Title);
        Assert.Equal("VPN on", on.Subtitle);
        Assert.Equal("netUp", on.Event);
        Assert.True(on.Compact);
        Assert.Equal("network", on.Module);
        Assert.Null(module.PendingDue);

        module.Observe(Net(true, wifi: true, vpn: false), T0.AddSeconds(2));

        Assert.Equal(2, announcer.Calls.Count);
        var off = announcer.Calls[1].Toast;
        Assert.Equal("vpnoff", off.Glyph);
        Assert.Equal("#8E8E93", off.Tint);
        Assert.Equal("VPN off", off.Title);
        Assert.Equal("VPN off", off.Subtitle);
        Assert.True(off.Compact);
    }

    [Fact]
    public void VpnSwitch_WhileOnlineUnchanged_SchedulesNoCheck()
    {
        var announcer = new RecordingAnnouncer();
        var module = new NetworkModule(announcer);
        module.Observe(Net(true), T0);

        module.Observe(Net(true, vpn: true), T0.AddSeconds(1));

        Assert.Single(announcer.Calls);
        Assert.Null(module.PendingDue);
    }

    [Fact]
    public void Reset_ForgetsBaseline_NextReadingIsBaseline()
    {
        var announcer = new RecordingAnnouncer();
        var module = new NetworkModule(announcer);
        module.Observe(Net(true), T0);
        module.Observe(Net(false), T0.AddSeconds(1));

        module.Reset();
        Assert.Null(module.PendingDue);
        module.Tick(T0.AddSeconds(5));
        module.Observe(Net(false), T0.AddSeconds(6));

        Assert.Empty(announcer.Calls);
        Assert.Null(module.PendingDue);
    }

    [Fact]
    public void Judge_IgnoresLoopbackAndTunnelForOnline_CountsWintunForVpn()
    {
        var loopbackAndTeredo = new[]
        {
            new NetIf("Loopback", "Software Loopback Interface 1", NetworkInterfaceType.Loopback, true, true, true),
            new NetIf("Teredo", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel, true, true, true),
        };
        var wintun = new NetIf("Wintun", "Wintun Userspace Tunnel", NetworkInterfaceType.Unknown, true, true, false);

        Assert.Equal((false, false, false), NetworkJudge.Judge(loopbackAndTeredo));
        Assert.Equal((false, false, true), NetworkJudge.Judge(loopbackAndTeredo.Append(wintun)));

        var wired = new NetIf("Ethernet", "Realtek Gaming GbE", NetworkInterfaceType.Ethernet, true, true, true);
        Assert.Equal((true, false, true), NetworkJudge.Judge(loopbackAndTeredo.Append(wintun).Append(wired)));
    }

    [Fact]
    public void Judge_WifiNeedsOnlineWirelessInterface()
    {
        var wifi = new NetIf("Wi-Fi", "Intel Wi-Fi 6 AX201", NetworkInterfaceType.Wireless80211, true, true, true);
        var noGateway = new NetIf("Wi-Fi 2", "Intel Wi-Fi 6 AX201 #2", NetworkInterfaceType.Wireless80211, true, true, false);
        var down = new NetIf("Ethernet", "Realtek", NetworkInterfaceType.Ethernet, false, true, true);

        Assert.Equal((true, true, false), NetworkJudge.Judge(new[] { wifi }));
        Assert.Equal((false, false, false), NetworkJudge.Judge(new[] { noGateway, down }));
    }
}
