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

    /// Stops the watcher and forgets what it read. Its events after this are ignored.
    void Stop();

    /// The connected containers this PC lists now, each with its categories and battery level. Null when the read failed: the caller
    /// keeps the list it has, and makes no change from a failed read.
    Task<IReadOnlyList<DeviceFact>?> Read();

    /// The battery levels only, re-read for the containers that had a level in the last read. The other containers are returned as
    /// they were. Null when the read failed.
    Task<IReadOnlyList<DeviceFact>?> ReadBatteries();
}

/// The device reader (P3d-R3, amended): one DeviceWatcher over the device containers, which covers Bluetooth and USB accessories as the
/// Settings app's Devices page does. A container's kind comes from its device nodes (DeviceKinds.Categories: the class GUIDs and the bus),
/// and its battery is the highest level its nodes report in the Bluetooth battery property. There is no GATT connection.
///
/// The last full read is kept per container: a container that is read again keeps its kind while its name holds, so a full read
/// queries the nodes of a new container only. A container whose node query fails keeps its last-known fact; a container that is new
/// and fails fails the whole read, so the caller keeps its list rather than see the container as gone. A failed container listing
/// fails the read too.
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
    private IReadOnlyList<DeviceFact> _known = Array.Empty<DeviceFact>();  // the last full read's facts, in its order
    private int _epoch;  // every Stop: a read that started under an older one stores nothing

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
            // Each closure holds its own watcher: an event from a watcher that Stop has already dropped is ignored.
            watcher.Added += (_, _) => Notify(watcher);
            watcher.Updated += (_, _) => Notify(watcher);
            watcher.Removed += (_, _) => Notify(watcher);
            watcher.EnumerationCompleted += (_, _) =>
            {
                lock (_gate)
                {
                    if (ReferenceEquals(watcher, _watcher))
                    {
                        _enumerated = true;
                    }
                }
                Notify(watcher);
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

    /// Stops the watcher and forgets the last read: its events after this are ignored, and a read in flight stores nothing.
    public void Stop()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            _enumerated = false;
            _known = Array.Empty<DeviceFact>();
            _epoch++;
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

    /// Lists the connected containers. A container already known keeps its kind while its name holds, and its battery is read again
    /// (none for a container that is not an accessory). A new container's nodes are read for its kind and battery. Null when the
    /// container listing fails, or a new container's nodes cannot be read.
    public async Task<IReadOnlyList<DeviceFact>?> Read()
    {
        int epoch;
        IReadOnlyList<DeviceFact> known;
        lock (_gate)
        {
            epoch = _epoch;
            known = _known;
        }
        try
        {
            var containers = await DeviceInformation.FindAllAsync(ContainerQuery, ContainerProperties, DeviceInformationKind.DeviceContainer);
            var facts = new List<DeviceFact>();
            foreach (var container in containers)
            {
                if (!container.Properties.TryGetValue(ConnectedKey, out var connected) || connected is not true)
                {
                    continue;
                }
                var name = container.Name;
                var old = known.FirstOrDefault(f => f.Id == container.Id && f.Name == name);
                if (old is null)
                {
                    facts.Add(await Classify(container));
                    continue;
                }
                if (old.Categories.Count == 0)
                {
                    facts.Add(old);  // not an accessory: nothing to read again
                    continue;
                }
                try
                {
                    facts.Add(old with { Battery = await BatteryOf(container.Id) });
                }
                catch (Exception e)
                {
                    Log("battery", e);
                    facts.Add(old);  // a failed node query keeps the last-known fact
                }
            }
            Store(epoch, facts);
            return facts;
        }
        catch (Exception e)
        {
            Log("read", e);
            return null;
        }
    }

    /// Reads the battery of each known container that had a level. A failed node query fails the read. Null when it fails.
    public async Task<IReadOnlyList<DeviceFact>?> ReadBatteries()
    {
        int epoch;
        IReadOnlyList<DeviceFact> known;
        lock (_gate)
        {
            epoch = _epoch;
            known = _known;
        }
        try
        {
            var facts = new List<DeviceFact>();
            foreach (var fact in known)
            {
                facts.Add(fact.Battery is null ? fact : fact with { Battery = await BatteryOf(fact.Id) });
            }
            Store(epoch, facts);
            return facts;
        }
        catch (Exception e)
        {
            Log("batteries", e);
            return null;
        }
    }

    /// A container's kind and battery from its device nodes.
    private static async Task<DeviceFact> Classify(DeviceInformation container)
    {
        var nodes = await NodesOf(container.Id);
        var classes = nodes.Select(n => (n.Id, ClassOf(n))).ToList();
        return new DeviceFact(container.Id, container.Name, DeviceKinds.Categories(container.Name, classes), LevelOf(nodes));
    }

    /// A container's battery from its device nodes (the only query a battery re-read makes).
    private static async Task<int?> BatteryOf(string containerId) => LevelOf(await NodesOf(containerId));

    private static async Task<IReadOnlyList<DeviceInformation>> NodesOf(string containerId)
    {
        var id = containerId.Trim('{', '}');
        return await DeviceInformation.FindAllAsync(
            $"System.Devices.ContainerId:=\"{{{id}}}\"", NodeProperties, DeviceInformationKind.Device);
    }

    /// The class GUID of a node: the property is a System.Guid (probed on this PC), or its text. Braces are optional in DeviceKinds.
    private static string? ClassOf(DeviceInformation node) =>
        node.Properties.TryGetValue(ClassKey, out var value) ? value switch
        {
            string s => s,
            Guid g => g.ToString("B"),
            _ => null,
        } : null;

    /// The highest battery level of a container's nodes, or null when none reports one.
    private static int? LevelOf(IEnumerable<DeviceInformation> nodes)
    {
        int? battery = null;
        foreach (var node in nodes)
        {
            if (BatteryLevel(node.Properties.TryGetValue(BatteryKey, out var value) ? value : null) is { } level)
            {
                battery = Math.Max(battery ?? 0, level);
            }
        }
        return battery;
    }

    /// A battery level from the property's value: a byte (or an int) from 0 to 100, else null.
    private static int? BatteryLevel(object? value) => value switch
    {
        byte b => b,
        int i when i is >= 0 and <= 100 => i,
        _ => null,
    };

    /// Keeps a read's facts as the last full read, when no Stop came between the read's start and now.
    private void Store(int epoch, IReadOnlyList<DeviceFact> facts)
    {
        lock (_gate)
        {
            if (epoch == _epoch)
            {
                _known = facts;
            }
        }
    }

    /// Any thread: the watcher's event raises Changed once enumeration is done, and only for the current watcher.
    private void Notify(DeviceWatcher watcher)
    {
        Action? changed;
        lock (_gate)
        {
            if (!ReferenceEquals(watcher, _watcher) || !_enumerated)
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
