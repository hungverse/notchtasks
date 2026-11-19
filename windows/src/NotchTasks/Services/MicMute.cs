using System.Runtime.InteropServices;

namespace NotchTasks.Services;

/// The default microphone's mute, through the Core Audio endpoint volume (classic COM interop: the app is not trimmed or AOT
/// published, so built-in COM is allowed). The capture endpoint is the default for communications, the one calls use.
/// Every call that fails returns null (IsMuted) or false (SetMuted): nothing throws to the caller. The COM objects are
/// released when each call ends.
public static class MicMute
{
    private const int ECapture = 1;         // EDataFlow.eCapture
    private const int ECommunications = 2;  // ERole.eCommunications

    /// Whether the default microphone is muted, or null when it cannot be read.
    public static bool? IsMuted() => WithEndpoint(volume =>
    {
        volume.GetMute(out var muted);
        return muted;
    });

    /// Mutes or unmutes the default microphone. False when the change could not be made.
    public static bool SetMuted(bool muted) => WithEndpoint(volume =>
    {
        var none = Guid.Empty;
        volume.SetMute(muted, ref none);
        return true;
    }) ?? false;

    /// Runs action on the default capture endpoint's volume interface, releasing every COM object it made. Null when the
    /// endpoint cannot be reached or the action fails.
    private static T? WithEndpoint<T>(Func<IAudioEndpointVolume, T> action) where T : struct
    {
        object? enumerator = null;
        IMMDevice? device = null;
        object? volumeObject = null;
        try
        {
            var deviceEnumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorClass();
            enumerator = deviceEnumerator;
            deviceEnumerator.GetDefaultAudioEndpoint(ECapture, ECommunications, out device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out volumeObject);
            return action((IAudioEndpointVolume)volumeObject);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Release(volumeObject);
            Release(device);
            Release(enumerator);
        }
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorClass
    {
    }

    /// IMMDeviceEnumerator: only the methods used are declared, in their vtable order.
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    /// IMMDevice: Activate is the first method after IUnknown.
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    /// IAudioEndpointVolume: the methods up to SetMute and GetMute are declared, in their vtable order. Each HRESULT becomes an
    /// exception (the default for a void method), which MicMute catches.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);

        void UnregisterControlChangeNotify(IntPtr notify);

        void GetChannelCount(out uint channels);

        void SetMasterVolumeLevel(float level, ref Guid context);

        void SetMasterVolumeLevelScalar(float level, ref Guid context);

        void GetMasterVolumeLevel(out float level);

        void GetMasterVolumeLevelScalar(out float level);

        void SetChannelVolumeLevel(uint channel, float level, ref Guid context);

        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

        void GetChannelVolumeLevel(uint channel, out float level);

        void GetChannelVolumeLevelScalar(uint channel, out float level);

        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
