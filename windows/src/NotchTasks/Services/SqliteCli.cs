using NotchTasks.Core.Agents;
using NotchTasks.Core.Asks;

namespace NotchTasks.Services;

/// Runs sqlite3 for the Antigravity scanner: `sqlite3 -readonly -json <file> <sql>`, with no shell and no window, and a 2 s limit.
/// sqlite3 is found by CliLauncher.ResolveDirect: a real .exe only, never a .cmd, .bat or .ps1 (ruling P3b-R4: with no sqlite3
/// there are no Antigravity jobs). The database path and the SQL are user-derived text: they go in as arguments, never a shell.
public sealed class SqliteCli : ISqliteReader
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    private readonly Lazy<CliCommand?> _command;

    /// home is the user's profile folder, searched for sqlite3 after PATH.
    public SqliteCli(string home)
    {
        _command = new Lazy<CliCommand?>(() => CliLauncher.ResolveDirect("sqlite3", home));
    }

    /// The rows as sqlite3 -json prints them ("" for no rows). Null when there is no sqlite3, the query fails or times out.
    public string? Query(string dbFile, string sql)
    {
        var command = _command.Value;
        return command is null
            ? null
            : CliLauncher.Run(command, new[] { "-readonly", "-json", dbFile, sql }, Limit, CancellationToken.None, requireZeroExit: true);
    }
}
