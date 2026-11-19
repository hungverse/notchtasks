using NotchTasks.Core.Agents;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

// The real process table, read-only: the snapshot must contain this test process, with its own command line.
public sealed class ProcessReaderTests
{
    [Fact]
    public void ProcessReader_SeesItself()
    {
        var reader = new ProcessReader();
        var snapshot = reader.Snapshot();

        Assert.True(snapshot.ById.TryGetValue(Environment.ProcessId, out var me));
        var commandLine = reader.CommandLine(me);
        Assert.NotNull(commandLine);
        Assert.Contains("testhost", commandLine, StringComparison.OrdinalIgnoreCase);
    }
}
