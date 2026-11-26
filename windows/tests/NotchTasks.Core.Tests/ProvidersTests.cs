using NotchTasks.Core.Agents;
using Xunit;

namespace NotchTasks.Core.Tests;

// The agent file readers share a file with the agent that writes it: a rollout or status file is open for writing while
// the agent works, so the readers must open it for shared reads and writes too. Temp folders only.
public sealed class ProvidersTests : IDisposable
{
    private readonly string _dir = AsksFixtures.NewTempDir();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Still open elsewhere; the temp cleaner takes it later.
        }
    }

    /// A file opened the way an agent's writer opens it: for writing, sharing reads only.
    private FileStream HeldByWriter(string name, string text)
    {
        var file = Path.Combine(_dir, name);
        var writer = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write(System.Text.Encoding.UTF8.GetBytes(text));
        writer.Flush();
        return writer;
    }

    [Fact]
    public void ReadHead_FileHeldByItsWriter_ReadsTheHead()
    {
        using var writer = HeldByWriter("head.jsonl", "{\"type\":\"session_meta\"}\n");

        Assert.Equal("{\"type\":\"session_meta\"}\n", Providers.ReadHead(Path.Combine(_dir, "head.jsonl")));
    }

    [Fact]
    public void ReadTail_FileHeldByItsWriter_ReadsTheCompleteLines()
    {
        // the last line has no newline yet: a write in progress, left out
        using var writer = HeldByWriter("tail.jsonl", "one\ntwo\npart");

        Assert.Equal("one\ntwo\n", Providers.ReadTail(Path.Combine(_dir, "tail.jsonl")));
    }

    [Fact]
    public void ReadAllTextShared_FileHeldByItsWriter_ReadsTheText()
    {
        using var writer = HeldByWriter("status.json", "{\"pid\":1}");

        Assert.Equal("{\"pid\":1}", Providers.ReadAllTextShared(Path.Combine(_dir, "status.json")));
    }
}
