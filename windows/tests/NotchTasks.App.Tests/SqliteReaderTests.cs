using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using NotchTasks.Core.Agents;
using NotchTasks.Services;
using Xunit;

namespace NotchTasks.AppTests;

/// The in-process Antigravity database reader: a normal read, a missing file, a locked (or open-for-write) file and a corrupt
/// file. Each test builds its own small conversation database in a temp folder with the schema the parser expects.
public sealed class SqliteReaderTests : IDisposable
{
    private const string LastStep = "select step_type, status from steps order by idx desc limit 1";

    private readonly TempRoot _root = new();

    public void Dispose() => _root.Dispose();

    /// A conversation database: steps (idx, step_type, status) and trajectory_metadata_blob (data), with the last step given.
    private string MakeConversation(int stepType, int status, string project = "NotchTasks")
    {
        var path = Path.Combine(_root.Folder, Guid.NewGuid().ToString("N") + ".db");
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        Exec(connection, "create table steps (idx integer primary key, step_type integer, status integer)");
        Exec(connection, "create table trajectory_metadata_blob (data blob)");
        Exec(connection, $"insert into steps values (1, 14, 3), (2, {stepType}, {status})");
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "insert into trajectory_metadata_blob values ($data)";
            insert.Parameters.AddWithValue("$data", Metadata(project));
            insert.ExecuteNonQuery();
        }
        return path;
    }

    /// The metadata blob the parser reads: a varint length byte and a file:/// workspace uri, as the conversation writes it.
    private static byte[] Metadata(string project)
    {
        var uri = Encoding.UTF8.GetBytes("file:///Users/x/" + project);
        return new byte[] { 0x0a, (byte)uri.Length }.Concat(uri).ToArray();
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void NormalRead_ReturnsTheRowsAsJson_AndTheConversationParses()
    {
        var db = MakeConversation(stepType: 15, status: 8);
        var reader = new SqliteReader();

        Assert.Equal("[{\"step_type\":15,\"status\":8}]", reader.Query(db, LastStep));

        var state = AntigravityConversation.Read(db, reader);
        Assert.NotNull(state);
        Assert.True(state!.Active);
        Assert.Equal("NotchTasks", state.Title);
    }

    [Fact]
    public void NormalRead_FinishedTurn_IsNotActive()
    {
        var db = MakeConversation(stepType: 15, status: 3);

        var state = AntigravityConversation.Read(db, new SqliteReader());

        Assert.NotNull(state);
        Assert.False(state!.Active);
    }

    [Fact]
    public void MissingFile_ReturnsNull_AndCreatesNothing()
    {
        var db = Path.Combine(_root.Folder, "missing.db");

        Assert.Null(new SqliteReader().Query(db, LastStep));
        Assert.False(File.Exists(db));
        Assert.Null(AntigravityConversation.Read(db, new SqliteReader()));
    }

    [Fact]
    public void LockedFile_ReturnsNullQuickly_AndTheWriterIsUnharmed()
    {
        var db = MakeConversation(stepType: 15, status: 8);
        using var writer = new SqliteConnection("Data Source=" + db);
        writer.Open();
        Exec(writer, "begin exclusive");
        var reader = new SqliteReader();
        var clock = Stopwatch.StartNew();

        var rows = reader.Query(db, LastStep);

        Assert.Null(rows);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the read waited {clock.Elapsed}");
        Exec(writer, "rollback");
        Assert.Equal("[{\"step_type\":15,\"status\":8}]", reader.Query(db, LastStep));
    }

    [Fact]
    public void OpenForWriteFile_StillReads()
    {
        // A writer holding only a reserved lock does not block readers in the default rollback-journal mode.
        var db = MakeConversation(stepType: 15, status: 8);
        using var writer = new SqliteConnection("Data Source=" + db);
        writer.Open();
        Exec(writer, "begin immediate");

        var rows = new SqliteReader().Query(db, LastStep);

        Assert.Equal("[{\"step_type\":15,\"status\":8}]", rows);
        Exec(writer, "rollback");
    }

    [Fact]
    public void CorruptFile_ReturnsNull_AndIsNotAConversation()
    {
        var db = Path.Combine(_root.Folder, "corrupt.db");
        File.WriteAllBytes(db, Encoding.ASCII.GetBytes(new string('x', 4096)));

        Assert.Null(new SqliteReader().Query(db, LastStep));
        Assert.Null(AntigravityConversation.Read(db, new SqliteReader()));
    }
}
