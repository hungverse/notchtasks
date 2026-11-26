using System.Runtime.InteropServices;
using NotchTasks.Core.Levels;

namespace NotchTasks.Services.Levels;

/// The default output through Core Audio (Mac: Levels.output and its volume). The MMDevice enumerator gives the default render endpoint
/// for the multimedia role; its IAudioEndpointVolume reports the volume and mute, and calls IAudioEndpointVolumeCallback when either
/// changes, so nothing polls. An IMMNotificationClient tells of a new default device. The COM callbacks arrive on COM's own threads:
/// each raises its event as it is, and the source marshals it to the UI thread. Dispose gives every COM object back.
public sealed class CoreAudioOutputs : IOutputDevices
{
    /// EDataFlow.eRender: the output side.
    private const int Render = 0;

    /// ERole.eMultimedia: the role the Windows volume flyout follows.
    private const int Multimedia = 1;

    /// CLSCTX_ALL, for Activate.
    private const int ClsctxAll = 0x17;

    private static readonly Guid EndpointVolumeIid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly DefaultDeviceClient _client;

    /// Makes the enumerator and registers for default-device changes. Throws when Core Audio is not there (the source logs it and runs
    /// without the volume).
    public CoreAudioOutputs()
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        _client = new DefaultDeviceClient(() => DefaultChanged?.Invoke());
        var hr = _enumerator.RegisterEndpointNotificationCallback(_client);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    /// Raised on a COM thread when the default render device for the multimedia role changes.
    public event Action? DefaultChanged;

    /// The default output, bound now: its volume interface and its change callback. Null when there is no output or it cannot be bound.
    public IOutputEndpoint? OpenDefault()
    {
        if (_enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out var device) != 0 || device is null)
        {
            return null;
        }

        var iid = EndpointVolumeIid;
        if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var volumeObject) != 0 || volumeObject is not IAudioEndpointVolume volume)
        {
            Marshal.ReleaseComObject(device);
            return null;
        }

        var notifier = new VolumeNotifier();
        if (volume.RegisterControlChangeNotify(notifier) != 0)
        {
            Marshal.ReleaseComObject(volume);
            Marshal.ReleaseComObject(device);
            return null;
        }

        device.GetId(out var id);
        return new Endpoint(device, volume, notifier, id ?? string.Empty);
    }

    /// Unregisters the default-device client and releases the enumerator.
    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_client);
        Marshal.ReleaseComObject(_enumerator);
    }

    /// One bound output: its volume and mute, and its change. Dispose unregisters the callback and releases the COM objects.
    private sealed class Endpoint : IOutputEndpoint
    {
        private readonly IMMDevice _device;
        private readonly IAudioEndpointVolume _volume;
        private readonly VolumeNotifier _notifier;
        private bool _disposed;

        public Endpoint(IMMDevice device, IAudioEndpointVolume volume, VolumeNotifier notifier, string id)
        {
            _device = device;
            _volume = volume;
            _notifier = notifier;
            Id = id;
        }

        public string Id { get; }

        public event Action? Changed
        {
            add => _notifier.Changed += value;
            remove => _notifier.Changed -= value;
        }

        /// The master volume (one value for all channels, as the Mac's slider) and the mute.
        public VolumeReading? Read()
        {
            if (_disposed
                || _volume.GetMasterVolumeLevelScalar(out var level) != 0
                || _volume.GetMute(out var muted) != 0)
            {
                return null;
            }
            return new VolumeReading(Math.Clamp(level, 0f, 1f), muted);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _volume.UnregisterControlChangeNotify(_notifier);
            Marshal.ReleaseComObject(_volume);
            Marshal.ReleaseComObject(_device);
        }
    }

    /// The volume callback: Core Audio calls OnNotify on its thread when the volume or the mute changes. It raises Changed as it is.
    [ComVisible(true)]
    private sealed class VolumeNotifier : IAudioEndpointVolumeCallback
    {
        public event Action? Changed;

        public int OnNotify(IntPtr notification)
        {
            Changed?.Invoke();
            return 0;
        }
    }

    /// The device notifications: only the default render device for the multimedia role matters here.
    [ComVisible(true)]
    private sealed class DefaultDeviceClient : IMMNotificationClient
    {
        private readonly Action _onDefaultChanged;

        public DefaultDeviceClient(Action onDefaultChanged) => _onDefaultChanged = onDefaultChanged;

        public int OnDeviceStateChanged(string deviceId, int newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
        {
            if (flow == Render && role == Multimedia)
            {
                _onDefaultChanged();
            }
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}

/// The MMDevice enumerator's class (its CLSID is the Windows one).
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

/// IMMDeviceEnumerator, with only the methods used given names; the others keep their slots (the vtable order of mmdeviceapi.h).
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    void NotImplEnumAudioEndpoints();

    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

    void NotImplGetDevice();

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IMMNotificationClient client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

/// IMMDevice: its activation (the volume interface) and its ID. The other slots are kept, in the header's order.
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);

    void NotImplOpenPropertyStore();

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    void NotImplGetState();
}

/// IMMNotificationClient: the device notifications. The slots are in the header's order.
[ComImport]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
}

/// IAudioEndpointVolume: the master volume and the mute, and the change callback. Slots the source never calls are kept by name, in the
/// header's order (endpointvolume.h): the volume's 15 methods before GetMute, with GetMute at slot 15.
[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);

    void NotImplGetChannelCount();

    void NotImplSetMasterVolumeLevel();

    void NotImplSetMasterVolumeLevelScalar();

    void NotImplGetMasterVolumeLevel();

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    void NotImplSetChannelVolumeLevel();

    void NotImplSetChannelVolumeLevelScalar();

    void NotImplGetChannelVolumeLevel();

    void NotImplGetChannelVolumeLevelScalar();

    void NotImplSetMute();

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
}

/// IAudioEndpointVolumeCallback: Core Audio calls OnNotify when the endpoint's volume or mute changes.
[ComImport]
[Guid("657804FA-5D96-4CE9-B9C0-0A4B4B6EC1FB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notification);
}

/// PROPERTYKEY: a property's format ID and its ID (the device-property notifications pass it, unused here).
[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}
