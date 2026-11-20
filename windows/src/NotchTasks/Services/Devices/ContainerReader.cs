using NotchTasks.Core.Devices;
using Windows.Devices.Enumeration;

namespace NotchTasks.Services.Devices;

/// What the devices source reads and listens to (AppStateOptions.Devices). Changed may be raised on any thread. Every call is safe
/// after Stop: a reader that has stopped answers with nothing. A test passes a fake; production passes ContainerReader.
public interface IDeviceReader
{
    /// Raised when a container came, went or changed (the watcher's events, after its first enumeration), and once when the first
    /// enumeration completes.
    event Action Changed;

    /// Starts the device container watcher.
    void Start();

    /// Stops the watcher. Its events after this are ignored.
    void Stop();

    /// The connected containers this PC lists now, each with its categories and battery level.
    Task<IReadOnlyList<DeviceFact>> Read();
}

/// The device reader (P3d-R3, amended): one DeviceWatcher over the device containers, which covers Bluetooth and USB accessories as the
/// Settings app's Devices page does. A container's kind comes from its device nodes (DeviceKinds.Categories: the class GUIDs and the bus),
/// and its battery is the highest level its nodes report in the Bluetooth battery property. There is no GATT connection. A read lists
/// the connected containers in one pass; a failed container is left out of the read and logged once per kind.
public sealed class ContainerReader : IDeviceReader
{
    private const string ContainerQuery = "System.Devices.LocalMachine:=System.StructuredQueryType.Boolean#False";
    private const string ConnectedKey = "System.Devices.Connected";
    private const string NameKey = "System.ItemNameDisplay";
    private const string ClassKey = "System.Devices.ClassGuid";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private static readonly string[] ContainerProperties = [ConnectedKey, NameKey];
    private static readonly string[] NodeProperties = [ClassKey, BatteryKey];

    private readonly object _gate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);
    private DeviceWatcher? _watcher;
    private bool _enumerated;
    private Action? _changed;

    public event Action Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
            }
        }
    }

    /// Starts the watcher. A second Start while one runs does nothing.
    public void Start()
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                return;
            }
            _enumerated = false;
        }
        try
        {
            var watcher = DeviceInformation.CreateWatcher(ContainerQuery, ContainerProperties, DeviceInformationKind.DeviceContainer);
            watcher.Added += (w, _) => Notify(w);
            watcher.Updated += (w, _) => Notify(w);
            watcher.Removed += (w, _) => Notify(w);
            watcher.EnumerationCompleted += (w, _) =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(w, _watcher))
                    {
                        _enumerated = true;
                    }
                }
                Notify(w);
            };
            lock (_gate)
            {
                _watcher = watcher;
            }
            watcher.Start();
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                _watcher = null;
            }
            Log("watch", e);
        }
    }

    /// Stops the watcher and forgets it: its events after this are ignored.
    public void Stop()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            _enumerated = false;
        }
        if (watcher is null)
        {
            return;
        }
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch (Exception e)
        {
            Log("stop", e);
        }
    }

    /// Lists the connected containers. Each one's device nodes are read once, for the kind and the battery. A container that fails
    /// is left out; the others still come.
    public async Task<IReadOnlyList<DeviceFact>> Read()
    {
        var facts = new List<DeviceFact>();
        try
        {
            var containers = await DeviceInformation.FindAllAsync(ContainerQuery, ContainerProperties, DeviceInformationKind.DeviceContainer);
            foreach (var container in containers)
            {
                if (container.Properties.TryGetValue(ConnectedKey, out var connected) && connected is true)
                {
                    try
                    {
                        facts.Add(await ReadContainer(container));
                    }
                    catch (Exception e)
                    {
                        Log("container", e);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log("read", e);
        }
        return facts;
    }

    private static async Task<DeviceFact> ReadContainer(DeviceInformation container)
    {
        var id = container.Id.Trim('{', '}');
        var name = container.Name;
        var nodes = await DeviceInformation.FindAllAsync(
            $"System.Devices.ContainerId:=\"{{{id}}}\"", NodeProperties, DeviceInformationKind.Device);

        var classes = nodes.Select(n => (n.Id, n.Properties.TryGetValue(ClassKey, out var c) ? c as string : null)).ToList();
        var categories = DeviceKinds.Categories(name, classes);

        int? battery = null;
        foreach (var node in nodes)
        {
            if (BatteryLevel(node.Properties.TryGetValue(BatteryKey, out var value) ? value : null) is { } level)
            {
                battery = Math.Max(battery ?? 0, level);
            }
        }
        return new DeviceFact(container.Id, name, categories, battery);
    }

    /// A battery level from the property's value: a byte (or an int) from 0 to 100, else null.
    private static int? BatteryLevel(object? value) => value switch
    {
        byte b => b,
        int i when i is >= 0 and <= 100 => i,
        _ => null,
    };

    /// Any thread: the watcher's event raises Changed once enumeration is done, and only for the current watcher.
    private void Notify(DeviceWatcher sender)
    {
        Action? changed;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher) || !_enumerated)
            {
                return;
            }
            changed = _changed;
        }
        changed?.Invoke();
    }

    /// Logs a failure of one kind the first time it happens, and never again (a container that fails every minute would flood the log).
    private void Log(string kind, Exception e)
    {
        lock (_gate)
        {
            if (!_logged.Add(kind))
            {
                return;
            }
        }
        ErrorLog.Write(e);
    }
}
