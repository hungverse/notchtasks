using Xunit;

namespace NotchTasksSensors.Tests;

/// The exit code of each helper stage, the event it reports, and that a failing sink does not change the code. Fakes only.
public sealed class HelperExitTests
{
    private sealed class FakeSink : IFailureSink
    {
        public List<(Exception Error, int EventId)> Reports { get; } = new();
        public bool Throws { get; set; }

        public void Report(Exception error, int eventId)
        {
            if (Throws)
            {
                throw new InvalidOperationException("the event log is not there");
            }
            Reports.Add((error, eventId));
        }
    }

    [Fact]
    public void Codes_AreTheDocumentedOnes()
    {
        Assert.Equal(10, HelperExit.CodeOf(HelperStage.Arguments));
        Assert.Equal(11, HelperExit.CodeOf(HelperStage.Reader));
        Assert.Equal(12, HelperExit.CodeOf(HelperStage.Serve));
        Assert.Equal(13, HelperExit.CodeOf(HelperStage.Install));
        Assert.Equal(1, HelperExit.CodeOf(HelperStage.Other));
    }

    [Fact]
    public void ThrowingArguments_Returns10() => CheckThrowingStage(HelperStage.Arguments, 10);

    [Fact]
    public void ThrowingReader_Returns11() => CheckThrowingStage(HelperStage.Reader, 11);

    [Fact]
    public void ThrowingServe_Returns12() => CheckThrowingStage(HelperStage.Serve, 12);

    [Fact]
    public void ThrowingInstall_Returns13() => CheckThrowingStage(HelperStage.Install, 13);

    [Fact]
    public void ThrowingOther_Returns1() => CheckThrowingStage(HelperStage.Other, 1);

    private static void CheckThrowingStage(HelperStage stage, int code)
    {
        var sink = new FakeSink();
        var error = new InvalidOperationException("boom");

        var result = HelperExit.Run(stage, () => throw error, sink);

        Assert.Equal(code, result);
        var report = Assert.Single(sink.Reports);
        Assert.Same(error, report.Error);
        Assert.Equal(HelperExit.StageEvent, report.EventId);
    }

    [Fact]
    public void StageResult_PassesThroughWhenNothingThrows()
    {
        var sink = new FakeSink();

        Assert.Equal(0, HelperExit.Run(HelperStage.Reader, () => 0, sink));
        Assert.Equal(7, HelperExit.Run(HelperStage.Serve, () => 7, sink));
        Assert.Empty(sink.Reports);
    }

    [Fact]
    public async Task AsyncStage_MapsTheSameWay()
    {
        var sink = new FakeSink();
        var error = new IOException("pipe");

        var result = await HelperExit.RunAsync(HelperStage.Serve, () => Task.FromException<int>(error), sink);

        Assert.Equal(12, result);
        Assert.Same(error, Assert.Single(sink.Reports).Error);
    }

    [Fact]
    public async Task AsyncStage_PassesTheResultThrough()
    {
        Assert.Equal(0, await HelperExit.RunAsync(HelperStage.Install, () => Task.FromResult(0), new FakeSink()));
    }

    [Fact]
    public void FailingSink_DoesNotChangeTheCode()
    {
        var sink = new FakeSink { Throws = true };

        Assert.Equal(11, HelperExit.Run(HelperStage.Reader, () => throw new DllNotFoundException("LHM"), sink));
    }

    [Fact]
    public void OutOfMemory_IsNotAStageFailure()
    {
        var sink = new FakeSink();

        Assert.Throws<OutOfMemoryException>(() => HelperExit.Run(HelperStage.Serve, () => throw new OutOfMemoryException(), sink));
        Assert.Empty(sink.Reports);
    }

    [Fact]
    public void FailureText_NamesEveryExceptionInTheChain()
    {
        var inner = new FileNotFoundException("LibreHardwareMonitorLib.dll missing");
        Exception outer;
        try
        {
            throw new TypeInitializationException("NotchTasksSensors.LibreHardwareSource", inner);
        }
        catch (Exception e)
        {
            outer = e;
        }

        var text = FailureText.Describe(outer);

        Assert.Contains(typeof(TypeInitializationException).FullName!, text);
        Assert.Contains("Inner: " + typeof(FileNotFoundException).FullName + ": LibreHardwareMonitorLib.dll missing", text);
        Assert.Contains("HResult: 0x", text);
        Assert.Contains("FailureText_NamesEveryExceptionInTheChain", text);  // the stack trace of the throw site
    }

    [Fact]
    public void FailureText_IsCutToTheEventSize()
    {
        var text = FailureText.Describe(new InvalidOperationException(new string('x', FailureText.MaxLength * 2)));

        Assert.True(text.Length <= FailureText.MaxLength + 32);
        Assert.Contains("(cut)", text);
    }
}
