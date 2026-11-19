using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using NotchTasks.Core.Modules;

namespace NotchTasks.Services.Modules;

/// The network module's feed. Start subscribes to NetworkChange's availability and address events and judges once to set the
/// baseline. The events come on thread-pool threads, so each is marshalled to the dispatcher, and a burst of them judges once
/// (a 300 ms debounce). A one-shot timer runs only while an online change is pending (NetworkModule.PendingDue), then calls
/// Tick. Nothing polls. Stop unsubscribes, stops the timers and forgets the baseline, so the next Start starts again.
public sealed class NetworkSource : IModuleSource
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly NetworkModule _module;
    private readonly Dispatcher _dispatcher;
    private readonly NetworkAvailabilityChangedEventHandler _onAvailability;
    private readonly NetworkAddressChangedEventHandler _onAddress;
    private readonly DispatcherTimer _debounce = new() { Interval = Debounce };
    private readonly DispatcherTimer _settle = new();
    private bool _started;

    /// The source of `module`. It is made on the UI thread, which the events are marshalled to.
    public NetworkSource(NetworkModule module)
    {
        _module = module;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _onAvailability = (_, _) => Post();
        _onAddress = (_, _) => Post();
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Judge();
        };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _module.Tick(DateTimeOffset.Now);
            Reschedule();
        };
    }

    /// Subscribes to the network events and judges once, so the first reading is the baseline.
    public void Start()
    {
        _started = true;
        NetworkChange.NetworkAvailabilityChanged += _onAvailability;
        NetworkChange.NetworkAddressChanged += _onAddress;
        Judge();
    }

    /// Unsubscribes, stops both timers and forgets the baseline and any pending check.
    public void Stop()
    {
        _started = false;
        NetworkChange.NetworkAvailabilityChanged -= _onAvailability;
        NetworkChange.NetworkAddressChanged -= _onAddress;
        _debounce.Stop();
        _settle.Stop();
        _module.Reset();
    }

    /// Called on a thread-pool thread: the judge runs on the UI thread, after the debounce.
    private void Post() => _dispatcher.BeginInvoke(new Action(OnNetworkEvent));

    private void OnNetworkEvent()
    {
        if (_started && !_debounce.IsEnabled)
        {
            _debounce.Start();
        }
    }

    /// Reads the interfaces, feeds the module, and arms the settle timer if a check is pending.
    private void Judge()
    {
        if (!_started)
        {
            return;
        }
        try
        {
            var (online, wifi, vpn) = NetworkJudge.Judge(ReadInterfaces());
            _module.Observe((online, wifi, vpn), DateTimeOffset.Now);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        Reschedule();
    }

    /// Runs the one-shot settle timer for the time left until the pending check, or stops it when none is pending.
    private void Reschedule()
    {
        _settle.Stop();
        if (_started && _module.PendingDue is { } due)
        {
            var wait = due - DateTimeOffset.Now;
            _settle.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
            _settle.Start();
        }
    }

    /// The interfaces as the judge reads them: IPv4 addresses and a non-zero IPv4 gateway.
    private static List<NetIf> ReadInterfaces() =>
        NetworkInterface.GetAllNetworkInterfaces().Select(n =>
        {
            var props = n.GetIPProperties();
            return new NetIf(
                n.Name,
                n.Description,
                n.NetworkInterfaceType,
                n.OperationalStatus == OperationalStatus.Up,
                props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork),
                props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)));
        }).ToList();
}
