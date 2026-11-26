using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NotchTasks.Core.Agents;
using SQLitePCL;

namespace NotchTasks.Services;

/// Reads an Antigravity conversation database in-process, through Microsoft.Data.Sqlite, opened read-only: `Mode=ReadOnly`,
/// no pooling (the file handle closes as soon as the query is done, so the app that owns the database is never held up), and
/// a 100 ms busy timeout, the same as the Mac (AntigravityScanner.swift: sqlite3_busy_timeout(db, 100)). The rows come back as
/// the JSON array sqlite3 -json printed ("" for no rows), so the parser in AntigravityConversation is unchanged. A missing,
/// locked, busy or corrupt file gives null: that read is skipped, and the reason is logged once per file for the app run.
public sealed class SqliteReader : ISqliteReader
{
    /// The busy timeout in milliseconds, set on the connection's native handle.
    private const int BusyTimeoutMs = 100;

    private static readonly object LogLock = new();
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    /// The rows of a read-only query on dbFile as a JSON array. Null when the file is missing, the query fails, or the database
    /// is locked or corrupt.
    public string? Query(string dbFile, string sql)
    {
        if (!File.Exists(dbFile))
        {
            return null;
        }
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        try
        {
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            // The library's command path keeps retrying a busy database until its command timeout (whole seconds), which would
            // replace the 100 ms wait. The query therefore steps on the same native handle, after the Mac's busy timeout is set.
            var db = connection.Handle!;
            Check(raw.sqlite3_busy_timeout(db, BusyTimeoutMs));
            return RowsJson(db, sql);
        }
        catch (SqliteException e)
        {
            LogOnce(dbFile, e.SqliteErrorCode, e.Message);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogOnce(dbFile, 0, e.Message);
            return null;
        }
    }

    /// The query's rows as a JSON array of objects keyed by column name. "" when the query returned no rows.
    private static string RowsJson(sqlite3 db, string sql)
    {
        var rc = raw.sqlite3_prepare_v2(db, sql, out var stmt);
        try
        {
            Check(rc);
            var columns = raw.sqlite3_column_count(stmt);
            var names = new string[columns];
            for (var i = 0; i < columns; i++)
            {
                names[i] = raw.sqlite3_column_name(stmt, i).utf8_to_string();
            }

            var buffer = new ArrayBufferWriter<byte>();
            var rows = 0;
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                while ((rc = raw.sqlite3_step(stmt)) == raw.SQLITE_ROW)
                {
                    rows++;
                    writer.WriteStartObject();
                    for (var i = 0; i < columns; i++)
                    {
                        writer.WritePropertyName(names[i]);
                        WriteValue(writer, stmt, i);
                    }
                    writer.WriteEndObject();
                }
                Check(rc, raw.SQLITE_DONE);
                writer.WriteEndArray();
            }
            return rows == 0 ? string.Empty : Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            raw.sqlite3_finalize(stmt);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, sqlite3_stmt stmt, int column)
    {
        switch (raw.sqlite3_column_type(stmt, column))
        {
            case raw.SQLITE_INTEGER:
                writer.WriteNumberValue(raw.sqlite3_column_int64(stmt, column));
                break;
            case raw.SQLITE_FLOAT:
                writer.WriteNumberValue(raw.sqlite3_column_double(stmt, column));
                break;
            case raw.SQLITE_TEXT:
                writer.WriteStringValue(raw.sqlite3_column_text(stmt, column).utf8_to_string());
                break;
            case raw.SQLITE_BLOB:
                writer.WriteBase64StringValue(raw.sqlite3_column_blob(stmt, column).ToArray());
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// Throws the SQLite error when a call did not return OK (or the expected code, when one is given).
    private static void Check(int rc, int expected = raw.SQLITE_OK)
    {
        if (rc != expected)
        {
            throw new SqliteException("SQLite error " + rc.ToString(CultureInfo.InvariantCulture), rc);
        }
    }

    /// Notes a skipped read through the app's error log, once per database file for the app run. Only the file name, the SQLite
    /// error code and SQLite's own message go in: never conversation content.
    private static void LogOnce(string dbFile, int code, string message)
    {
        lock (LogLock)
        {
            if (!Logged.Add(dbFile))
            {
                return;
            }
        }
        ErrorLog.Write(new IOException(
            $"NotchTasks: an Antigravity database read was skipped ({Path.GetFileName(dbFile)}, code {code}): {message}"));
    }
}
