using System.Globalization;
using NotchTasks.Core.Modules;
using NotchTasks.Core.Notch;
using Xunit;

namespace NotchTasks.Core.Tests;

/// The health module ports the Mac's checkDisk (Model.swift) and the CPU and memory load of the open pill (NotchView.swift).
/// Pure: the tests feed the free bytes and the CPU times, as the source would read them.
public class HealthModuleTests
{
    private const long Gb = 1_000_000_000;

    /// An announcer that keeps every toast with its notify flag.
    private sealed class RecordingAnnouncer : IAnnouncer
    {
        public List<(Toast Toast, bool Notify)> Calls { get; } = new();

        public void Announce(Toast t, bool notify = true) => Calls.Add((t, notify));
    }

    [Fact]
    public void Disk_Below10GB_WarnsOnce()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HealthModule(announcer);

        module.Disk(9 * Gb);
        module.Disk(8 * Gb);
        module.Disk(9 * Gb + 500_000_000);

        Assert.Single(announcer.Calls);
        var (toast, notify) = announcer.Calls[0];
        Assert.Equal("drive", toast.Glyph);
        Assert.Equal("#FF9F0A", toast.Tint);
        Assert.Equal("Disk almost full", toast.Title);
        Assert.Equal("warning", toast.Event);
        Assert.Equal("ms-settings:storagesense", toast.Url);
        Assert.Equal("Free up space", toast.Action);
        Assert.Equal("health", toast.Module);
        Assert.True(notify);
    }

    [Fact]
    public void Disk_WarnsAgain_OnlyAfterAbove15GB()
    {
        var announcer = new RecordingAnnouncer();
        var module = new HealthModule(announcer);

        module.Disk(9 * Gb);
        module.Disk(12 * Gb);
        module.Disk(15 * Gb);  // not above 15 GB: still warned
        Assert.Single(announcer.Calls);

        module.Disk(16 * Gb);  // above 15 GB: re-armed, no toast
        Assert.Single(announcer.Calls);

        module.Disk(9 * Gb);
        Assert.Equal(2, announcer.Calls.Count);
    }

    [Fact]
    public void Disk_Subtitle_IsFormattedInTheCurrentCulture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("vi-VN");
            var announcer = new RecordingAnnouncer();
            var module = new HealthModule(announcer);

            module.Disk(9_500_000_000);

            Assert.Equal("9,5 GB free", Assert.Single(announcer.Calls).Toast.Subtitle);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Load_FirstSample_IsNull()
    {
        var module = new HealthModule(new RecordingAnnouncer());

        module.Sample(new CpuTimes(50, 70, 30), 0.4);

        Assert.Null(module.Load);
    }

    [Fact]
    public void Load_FromTwoSamples()
    {
        var module = new HealthModule(new RecordingAnnouncer());

        // GetSystemTimes: the kernel time includes the idle time. Between the samples: idle 50, kernel 70 (with the idle
        // 50), user 30, so the total is 100 and the CPU is 50 % busy.
        module.Sample(new CpuTimes(0, 0, 0), 0.25);
        module.Sample(new CpuTimes(50, 70, 30), 0.25);

        Assert.NotNull(module.Load);
        Assert.Equal(0.5, module.Load!.Cpu, 6);
        Assert.Equal(0.25, module.Load.Memory, 6);
    }

    [Fact]
    public void Load_NoElapsedTime_KeepsThePreviousLoad()
    {
        var module = new HealthModule(new RecordingAnnouncer());
        module.Sample(new CpuTimes(0, 0, 0), 0.25);
        module.Sample(new CpuTimes(50, 70, 30), 0.25);

        // The kernel and user times did not move: the total is 0, so the previous reading stays.
        module.Sample(new CpuTimes(50, 70, 30), 0.25);

        Assert.Equal(0.5, module.Load!.Cpu, 6);
    }

    [Fact]
    public void ResetLoad_KeepsTheLoad_AndRestartsTheBaseline()
    {
        var module = new HealthModule(new RecordingAnnouncer());
        module.Sample(new CpuTimes(0, 0, 0), 0.25);
        module.Sample(new CpuTimes(50, 70, 30), 0.25);
        Assert.Equal(0.5, module.Load!.Cpu, 6);

        // Closing the pill forgets only the baseline: the shown load stays for the next open.
        module.ResetLoad();
        Assert.Equal(0.5, module.Load!.Cpu, 6);

        // The first sample after the reset is a new baseline: the shown load is kept, not cleared.
        module.Sample(new CpuTimes(60, 90, 50), 0.25);
        Assert.Equal(0.5, module.Load!.Cpu, 6);

        // The next one gives a new reading: idle 10 of a total of 40 is 75 % busy.
        module.Sample(new CpuTimes(70, 110, 70), 0.25);
        Assert.Equal(0.75, module.Load!.Cpu, 6);
    }

    [Fact]
    public void ClearLoad_ClearsTheLoad_AndSaysWhetherItWasSet()
    {
        var module = new HealthModule(new RecordingAnnouncer());
        module.Sample(new CpuTimes(0, 0, 0), 0.25);
        module.Sample(new CpuTimes(50, 70, 30), 0.25);

        Assert.True(module.ClearLoad());
        Assert.Null(module.Load);
        Assert.False(module.ClearLoad());

        // The baseline is gone too: the next sample only stores the times.
        module.Sample(new CpuTimes(60, 90, 50), 0.25);
        Assert.Null(module.Load);
    }

    [Theory]
    [InlineData(0.695, 70)]
    [InlineData(0.694, 69)]
    [InlineData(0.0, 0)]
    [InlineData(1.2, 100)]
    public void LoadPercent_RoundsHalfUp_AndClamps(double fraction, int expected) =>
        Assert.Equal(expected, HealthModule.LoadPercent(fraction));
}
