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

    [Theory]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"C:\dir\", @"C:\dir")]
    [InlineData(@"\\server\share\dir\", @"\\server\share\dir")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    public void ProcessReader_TrimFolder_KeepsTheDriveRootOnly(string path, string expected) =>
        Assert.Equal(expected, ProcessReader.TrimFolder(path));

    [Fact]
    public void ProcessReader_CwdOfItself()
    {
        var reader = new ProcessReader();
        var me = reader.Snapshot().ById[Environment.ProcessId];

        var cwd = reader.Cwd(me);

        Assert.NotNull(cwd);
        Assert.Equal(Environment.CurrentDirectory.TrimEnd('\\'), cwd.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);
    }
}
