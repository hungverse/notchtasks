using System.Runtime.InteropServices;
using NotchTasks.Services.Levels;
using Xunit;

namespace NotchTasks.AppTests;

/// The COM identities the volume feed uses, pinned to the Windows SDK values (mmdeviceapi.h and endpointvolume.h). The fakes cannot see
/// a wrong IID or CLSID: Core Audio would refuse the callback with no error in our code, so the values are checked here instead.
public sealed class CoreAudioInteropTests
{
    [Theory]
    [InlineData(typeof(IMMDeviceEnumerator), "A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InlineData(typeof(IMMDevice), "D666063F-1587-4E43-81F1-B948E807363F")]
    [InlineData(typeof(IMMNotificationClient), "7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InlineData(typeof(IAudioEndpointVolume), "5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InlineData(typeof(IAudioEndpointVolumeCallback), "657804FA-D6AD-4496-8A60-352752AF4F89")]
    public void ComInterface_HasTheSdkIid(Type type, string sdkIid) =>
        Assert.Equal(new Guid(sdkIid), type.GUID);

    [Theory]
    [InlineData(typeof(IMMDeviceEnumerator))]
    [InlineData(typeof(IMMDevice))]
    [InlineData(typeof(IMMNotificationClient))]
    [InlineData(typeof(IAudioEndpointVolume))]
    [InlineData(typeof(IAudioEndpointVolumeCallback))]
    public void ComType_IsMarkedComImport(Type type) =>
        Assert.NotNull(type.GetCustomAttributes(typeof(ComImportAttribute), inherit: false).FirstOrDefault());

    [Fact]
    public void MMDeviceEnumerator_HasTheSdkClsid() =>
        Assert.Equal(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), CoreAudioOutputs.MMDeviceEnumeratorClsid);

    /// The production factory (CoreAudioOutputs.CreateEnumerator) gives the enumerator, which reads the default render endpoint for the
    /// multimedia role and activates its IAudioEndpointVolume. Only GetMasterVolumeLevelScalar and GetMute are called: nothing sets the
    /// volume or the mute. A machine with no audio device has no default endpoint, and the test returns early there.
    [Fact]
    public void DefaultRenderEndpoint_ReadsTheVolumeThroughTheProductionFactory()
    {
        const int render = 0;       // EDataFlow.eRender
        const int multimedia = 1;   // ERole.eMultimedia
        const int clsctxAll = 0x17; // CLSCTX_ALL

        var enumerator = CoreAudioOutputs.CreateEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(render, multimedia, out var device) != 0 || device is null)
            {
                return;  // no audio device
            }
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (device.Activate(ref iid, clsctxAll, IntPtr.Zero, out var volumeObject) != 0
                    || volumeObject is not IAudioEndpointVolume volume)
                {
                    return;  // the endpoint has no volume interface
                }
                try
                {
                    Assert.Equal(0, volume.GetMasterVolumeLevelScalar(out var level));
                    Assert.InRange(level, 0f, 1f);
                    Assert.Equal(0, volume.GetMute(out _));
                }
                finally
                {
                    Marshal.ReleaseComObject(volume);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }
}
