using System.Net.NetworkInformation;
using NotchTasks.Core.Notch;

namespace NotchTasks.Core.Modules;

/// One network interface as the app reads it: its name and description, its type, whether it is up, and whether it has an
/// IPv4 address and an IPv4 gateway.
public readonly record struct NetIf(
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool Up,
    bool HasIPv4,
    bool HasGateway);

/// Decides, from the interfaces, whether the PC is online, on Wi-Fi, and on a VPN (the Windows form of the Mac's utun, ipsec,
/// ppp, tun and wg rule). Pure: the source reads the interfaces.
public static class NetworkJudge
{
    private static readonly string[] VpnNames =
    {
        "VPN", "WireGuard", "Wintun", "TAP-Windows", "OpenVPN", "Tailscale", "ZeroTier", "Cisco AnyConnect", "FortiClient",
    };

    /// Online: an interface that is up, not loopback and not a tunnel, with an IPv4 address and a gateway. Wifi: one of
    /// those is Wireless80211. Vpn: an interface that is up with IPv4 is PPP or has a VPN adapter's name.
    public static (bool Online, bool Wifi, bool Vpn) Judge(IEnumerable<NetIf> list)
    {
        var online = false;
        var wifi = false;
        var vpn = false;
        foreach (var n in list)
        {
            if (n.Up && n.HasIPv4 && n.HasGateway && n.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            {
                online = true;
                wifi |= n.Type == NetworkInterfaceType.Wireless80211;
            }
            if (n.Up && n.HasIPv4 && (n.Type == NetworkInterfaceType.Ppp || IsVpnName(n.Description)))
            {
                vpn = true;
            }
        }
        return (online, wifi, vpn);
    }

    private static bool IsVpnName(string description) =>
        VpnNames.Any(word => description.Contains(word, StringComparison.OrdinalIgnoreCase));
}

/// The network module (Mac: networkChanged in Model.swift). The first reading is the baseline, with no toast. An online change
/// is confirmed only if it still holds 2 s later: a shorter blip (switching networks) is no toast. A VPN switch while the
/// online state is unchanged is a quiet compact toast. No timers and no OS calls: the source feeds the readings and calls Tick
/// when PendingDue comes.
public sealed class NetworkModule
{
    /// How long an online change must hold before it is announced.
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(2);

    private readonly IAnnouncer _announce;
    private bool _baselined;
    private bool _confirmedOnline;
    private bool _latestOnline;
    private bool _lastVpn;
    private Pending? _pending;

    /// An online change awaiting confirmation: the value it changed to, the Wi-Fi reading of the observation that started it,
    /// and when it is checked.
    private readonly record struct Pending(bool Online, bool Wifi, DateTimeOffset Due);

    /// The toasts go to the announcer.
    public NetworkModule(IAnnouncer announce)
    {
        _announce = announce;
    }

    /// When the pending online change is checked, or null when none is pending. The source runs its one-shot timer for it.
    public DateTimeOffset? PendingDue => _pending?.Due;

    /// Records a reading. The first one after a Reset is the baseline. A change of the online state starts a pending check
    /// (Settle later) unless one is already pending; a VPN switch while the online state is unchanged is announced at once.
    public void Observe((bool Online, bool Wifi, bool Vpn) now, DateTimeOffset at)
    {
        if (!_baselined)
        {
            _baselined = true;
            _confirmedOnline = now.Online;
            _latestOnline = now.Online;
            _lastVpn = now.Vpn;
            return;
        }

        ResolveDue(at);
        var vpnWas = _lastVpn;
        _lastVpn = now.Vpn;
        _latestOnline = now.Online;
        if (now.Online != _confirmedOnline)
        {
            _pending ??= new Pending(now.Online, now.Wifi, at + Settle);
        }
        else if (now.Vpn != vpnWas)
        {
            AnnounceVpn(now.Vpn);
        }
    }

    /// Checks the pending online change once its time has come: it is confirmed (and announced) only if the latest reading
    /// still has the new value. Does nothing before PendingDue or with nothing pending.
    public void Tick(DateTimeOffset at) => ResolveDue(at);

    /// Forgets the baseline and any pending check, without a toast: the module is off. The next reading is a baseline again.
    public void Reset()
    {
        _baselined = false;
        _pending = null;
    }

    private void ResolveDue(DateTimeOffset at)
    {
        if (_pending is not { } p || at < p.Due)
        {
            return;
        }
        _pending = null;
        if (_latestOnline != p.Online)
        {
            return;
        }
        _confirmedOnline = p.Online;
        if (p.Online)
        {
            AnnounceBackOnline(p.Wifi);
        }
        else
        {
            AnnounceOffline();
        }
    }

    private void AnnounceBackOnline(bool wifi) =>
        _announce.Announce(
            new Toast(
                Glyph: wifi ? "wifi" : "network",
                Tint: "#30D158",
                Title: L10n.L("Back online"),
                Subtitle: L10n.L(wifi ? "Wi‑Fi connected" : "Connected"),
                Event: "netUp",
                Compact: true,
                Module: "network"),
            notify: false);

    private void AnnounceOffline() =>
        _announce.Announce(
            new Toast(
                Glyph: "wifioff",
                Tint: "#FF453A",
                Title: L10n.L("Network disconnected"),
                Subtitle: L10n.L("Check Wi‑Fi or the network cable"),
                Event: "netDown",
                Module: "network"),
            notify: true);

    private void AnnounceVpn(bool on)
    {
        var title = L10n.L(on ? "VPN on" : "VPN off");
        _announce.Announce(
            new Toast(
                Glyph: on ? "vpn" : "vpnoff",
                Tint: on ? "#0A84FF" : "#8E8E93",
                Title: title,
                Subtitle: title,
                Event: "netUp",
                Compact: true,
                Module: "network"),
            notify: false);
    }
}
