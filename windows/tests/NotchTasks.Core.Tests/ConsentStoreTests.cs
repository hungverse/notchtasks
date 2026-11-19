using NotchTasks.Core.Modules;
using Xunit;

namespace NotchTasks.Core.Tests;

/// ConsentStore reads which apps hold the mic or camera now (the Windows consent store under HKCU).
public class ConsentStoreTests
{
    private const string Mic = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
    private const string Cam = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";
    private const string OwnExe = @"C:\Program Files\NotchTasks\NotchTasks.exe";

    /// A consent entry with its last-used times: Stop 0 means the app is using the device now.
    private static void Entry(FakeRegistry r, string key, long start, long stop)
    {
        r.SetLong(key, "LastUsedTimeStart", start);
        r.SetLong(key, "LastUsedTimeStop", stop);
    }

    [Fact]
    public void ConsentStore_InUse_WhenStopIsZero_NonPackagedAndPackaged()
    {
        var r = new FakeRegistry();
        Entry(r, Mic + @"\MSTeams_8wekyb3d8bbwe", 100, 0);                       // packaged, in use
        Entry(r, Mic + @"\Old_pkg", 100, 200);                                    // packaged, stopped
        Entry(r, Mic + @"\NonPackaged\C:#Program Files#Zoom#bin#Zoom.exe", 100, 0);  // desktop, in use
        Entry(r, Mic + @"\NonPackaged\C:#Tools#Old.exe", 100, 300);                // desktop, stopped
        Entry(r, Cam + @"\NonPackaged\C:#Apps#Cam.EXE", 100, 0);                   // camera, in use

        var (mic, camera) = ConsentStore.InUse(r, OwnExe);

        Assert.Equal(new[] { "msteams_8wekyb3d8bbwe", "zoom.exe" }, mic);
        Assert.Equal(new[] { "cam.exe" }, camera);
    }

    [Fact]
    public void ConsentStore_NoStartTime_IsNotInUse()
    {
        var r = new FakeRegistry();
        r.SetLong(Mic + @"\Idle_pkg", "LastUsedTimeStop", 0);

        var (mic, _) = ConsentStore.InUse(r, OwnExe);

        Assert.Empty(mic);
    }

    [Fact]
    public void ConsentStore_MissingKeys_Empty()
    {
        var (mic, camera) = ConsentStore.InUse(new FakeRegistry(), OwnExe);

        Assert.Empty(mic);
        Assert.Empty(camera);
    }

    [Fact]
    public void ConsentStore_SkipsOwnExe()
    {
        var r = new FakeRegistry();
        Entry(r, Mic + @"\NonPackaged\C:#Program Files#NotchTasks#NOTCHTASKS.exe", 100, 0);
        Entry(r, Mic + @"\NonPackaged\C:#Tools#Other.exe", 100, 0);

        var (mic, _) = ConsentStore.InUse(r, OwnExe);

        Assert.Equal(new[] { "other.exe" }, mic);
    }
}
