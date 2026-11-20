using System.Windows.Threading;
using NotchTasks.Core;
using NotchTasks.Core.Devices;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using NotchTasks.Services.Devices;
using NotchTasks.Services.Modules;
using Xunit;

namespace NotchTasks.AppTests;

/// A device reader the tests drive: the containers it lists, the Changed subscribers and the starts and stops it had. Fail makes
/// every read fail (null), as a failed container listing does.
internal sealed class FakeDeviceReader : IDeviceReader
{
    private Action? _changed;

    public List<DeviceFact> Devices { get; set; } = new();

    public bool Fail { get; set; }

    public int Reads { get; private set; }

    public int BatteryReads { get; private set; }

    public int Starts { get; private set; }

    public int Stops { get; private set; }

    public int Subscribers { get; private set; }

    public event Action Changed
    {
        add
        {
            _changed += value;
            Subscribers++;
        }
        remove
        {
            _changed -= value;
            Subscribers--;
        }
    }

    /// Raises Changed, as the watcher does when a container came, went or changed.
    public void Raise() => _changed?.Invoke();

    public void Start() => Starts++;

    public void Stop() => Stops++;

    public Task<IReadOnlyList<DeviceFact>?> Read()
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<DeviceFact>?>(Fail ? null : Devices.ToList());
    }

    public Task<IReadOnlyList<DeviceFact>?> ReadBatteries()
    {
        BatteryReads++;
        return Task.FromResult<IReadOnlyList<DeviceFact>?>(Fail ? null : Devices.ToList());
    }
}

/// A toast sink that keeps what it was given (the devices module announces through it).
internal sealed class RecordingAnnouncer : IAnnouncer
{
    public List<Toast> Toasts { get; } = new();

    public void Announce(Toast t, bool notify = true) => Toasts.Add(t);
}

/// The devices source (module "devices"): a burst of watcher events makes one read after 500 ms, the 60 s battery timer runs only
/// while a listed device has a level, stopping stops the reader and the timers and clears the module, and a failed read changes
/// nothing (no toasts, the list kept), whether it is the first read or a later one.
public sealed class DevicesSourceTests
{
    private static DeviceFact Keyboard(int? battery = null) => new(
        "{container-1}", "Keyboard", new[] { "Input.Keyboard", "Bus.Usb" }, battery);

    private static DeviceFact Mouse() => new(
        "{container-2}", "Mouse", new[] { "Input.Mouse", "Bus.Usb" }, null);

    private static (DevicesSource Source, DevicesModule Module, FakeDeviceReader Reader, RecordingAnnouncer Toasts) Make(
        FakeDeviceReader reader)
    {
        var announcer = new RecordingAnnouncer();
        var module = new DevicesModule(announcer, () => null);
        var source = new DevicesSource(module, reader, () => { });
        return (source, module, reader, announcer);
    }

    [Fact]
    public void Burst_CoalescedTo500ms()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeDeviceReader { Devices = { Keyboard() } };
            var (source, module, _, _) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => reader.Reads >= 1);
            var before = reader.Reads;

            for (var i = 0; i < 10; i++)
            {
                reader.Raise();
            }
            Fixture.PumpUntil(() => reader.Reads > before);
            Fixture.PumpUntil(() => false, 700);  // a later burst would show here as another read

            Assert.Equal(before + 1, reader.Reads);
            Assert.Single(module.Devices);
            source.Stop();
        });
    }

    [Fact]
    public void BatteryTimer_OnlyWithLevels()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeDeviceReader { Devices = { Keyboard() } };
            var (source, _, _, _) = Make(reader);
            var battery = Fixture.Field<DispatcherTimer>(source, "_battery");

            source.Start();
            Fixture.PumpUntil(() => reader.Reads >= 1);
            Fixture.PumpUntil(() => false, 50);
            Assert.False(battery.IsEnabled);  // a listed device with no level: no battery timer

            reader.Devices = new List<DeviceFact> { Keyboard(battery: 50) };
            reader.Raise();
            Fixture.PumpUntil(() => battery.IsEnabled);
            Assert.True(battery.IsEnabled);

            reader.Devices = new List<DeviceFact>();
            reader.Raise();
            Fixture.PumpUntil(() => !battery.IsEnabled);
            Assert.False(battery.IsEnabled);
            source.Stop();
        });
    }

    [Fact]
    public void Stop_StopsReaderTimerAndClears()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeDeviceReader { Devices = { Keyboard(battery: 50) } };
            var (source, module, _, _) = Make(reader);
            var battery = Fixture.Field<DispatcherTimer>(source, "_battery");
            var coalesce = Fixture.Field<DispatcherTimer>(source, "_coalesce");

            source.Start();
            Fixture.PumpUntil(() => module.Devices.Count == 1);
            Assert.True(battery.IsEnabled);

            source.Stop();

            Assert.Equal(1, reader.Stops);
            Assert.Equal(0, reader.Subscribers);
            Assert.False(battery.IsEnabled);
            Assert.False(coalesce.IsEnabled);
            Assert.Empty(module.Devices);
        });
    }

    [Fact]
    public void FailedFirstRead_NoToastsAndNoBaseline()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeDeviceReader { Fail = true, Devices = { Keyboard() } };
            var (source, module, _, toasts) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => reader.Reads >= 1);
            Fixture.PumpUntil(() => false, 50);
            Assert.Empty(module.Devices);
            Assert.Empty(toasts.Toasts);

            // The first good read is the baseline: the keyboard that was there all along toasts nothing.
            reader.Fail = false;
            reader.Raise();
            Fixture.PumpUntil(() => module.Devices.Count == 1);
            Assert.Empty(toasts.Toasts);

            // A device that comes after the baseline does toast.
            reader.Devices.Add(Mouse());
            reader.Raise();
            Fixture.PumpUntil(() => module.Devices.Count == 2);
            Assert.Single(toasts.Toasts);
            Assert.Equal("deviceOn", toasts.Toasts[0].Event);
            source.Stop();
        });
    }

    [Fact]
    public void FailedLaterRead_KeepsListAndNoToasts()
    {
        Sta.Run(() =>
        {
            using var root = new TempRoot();
            var reader = new FakeDeviceReader { Devices = { Keyboard(), Mouse() } };
            var (source, module, _, toasts) = Make(reader);

            source.Start();
            Fixture.PumpUntil(() => module.Devices.Count == 2);
            Assert.Empty(toasts.Toasts);  // the baseline

            // A failed read is not "all devices gone": the list stays, and no disconnect toasts.
            reader.Fail = true;
            var before = reader.Reads;
            reader.Raise();
            Fixture.PumpUntil(() => reader.Reads > before);
            Fixture.PumpUntil(() => false, 50);
            Assert.Equal(2, module.Devices.Count);
            Assert.Empty(toasts.Toasts);

            // The next good read shows the same list: no connect toasts for devices that never left.
            reader.Fail = false;
            before = reader.Reads;
            reader.Raise();
            Fixture.PumpUntil(() => reader.Reads > before);
            Fixture.PumpUntil(() => false, 50);
            Assert.Equal(2, module.Devices.Count);
            Assert.Empty(toasts.Toasts);
            source.Stop();
        });
    }
}
