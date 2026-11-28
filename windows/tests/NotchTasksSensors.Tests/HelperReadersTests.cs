using NotchTasks.Core.Modules;
using NotchTasks.Core.Sensors;
using Xunit;

namespace NotchTasksSensors.Tests;

/// The reader stage: an ordinary failure to open the hardware falls back to the zones alone; a missing, unloadable or broken assembly
/// is rethrown (so the stage reports it, with code 11). Fakes only.
public sealed class HelperReadersTests
{
    private sealed class EmptyFallback : IThermalReader
    {
        public IReadOnlyList<ThermalReading> Read() => Array.Empty<ThermalReading>();
    }

    private sealed class FakeHardware : IHardwareSource
    {
        public int Disposed { get; private set; }

        public IReadOnlyList<HardwareSnapshot> Update() => Array.Empty<HardwareSnapshot>();

        public void Dispose() => Disposed++;
    }

    [Fact]
    public void OrdinaryOpenFailure_RunsWithNoHardware()
    {
        var reader = HelperReaders.CreateReader(() => throw new InvalidOperationException("no sensors"), new EmptyFallback());

        Assert.NotNull(reader);
        Assert.Empty(reader.Read());  // the fallback's zones alone
        reader.Dispose();
    }

    [Fact]
    public void DllNotFound_IsOrdinary_RunsWithNoHardware()
    {
        var reader = HelperReaders.CreateReader(() => throw new DllNotFoundException("PawnIO native"), new EmptyFallback());

        Assert.NotNull(reader);
        reader.Dispose();
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(FileLoadException))]
    [InlineData(typeof(TypeLoadException))]
    [InlineData(typeof(BadImageFormatException))]
    public void MissingOrBrokenAssembly_IsRethrown_ForTheReaderStage(Type failure)
    {
        var error = (Exception)Activator.CreateInstance(failure, "LibreHardwareMonitorLib")!;

        var thrown = Assert.ThrowsAny<Exception>(() => HelperReaders.CreateReader(() => throw error, new EmptyFallback()));

        Assert.Same(error, thrown);
        var code = HelperExit.Run(HelperStage.Reader, () =>
        {
            HelperReaders.CreateReader(() => throw error, new EmptyFallback());
            return 0;
        }, new SinkNone());
        Assert.Equal(11, code);
    }

    [Fact]
    public void OpenedHardware_IsUsed_AndClosedWithTheReader()
    {
        var hardware = new FakeHardware();

        var reader = HelperReaders.CreateReader(() => hardware, new EmptyFallback());
        reader.Dispose();

        Assert.Equal(1, hardware.Disposed);
    }

    /// A sink that records nothing: the reader stage's code is what the test checks.
    private sealed class SinkNone : IFailureSink
    {
        public void Report(Exception error, int eventId)
        {
        }
    }
}
