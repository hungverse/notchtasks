using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NotchTasks.Core.Agents;

namespace NotchTasks.Services;

/// Reads an Antigravity conversation database in-process, through Microsoft.Data.Sqlite, opened read-only: `Mode=ReadOnly`,
/// no pooling (the file handle closes as soon as the query is done, so the app that owns the database is never held up), and
/// a 1 s busy timeout. The rows come back as the JSON array sqlite3 -json printed ("" for no rows), so the parser in
/// AntigravityConversation is unchanged. A missing, locked, busy or corrupt file gives null: that read is skipped, and the
/// reason is logged once per file and kind, not on every poll (Mac: AntigravityScanner.swift reads through libsqlite3 with a
/// 100 ms busy timeout).
public sealed class SqliteReader : ISqliteReader
{
    private const int BusyTimeoutSeconds = 1;

    private static readonly object LogLock = new();
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);

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
            DefaultTimeout = BusyTimeoutSeconds,
        };
        try
        {
            using var connection = new SqliteConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            return ToJson(reader);
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

    /// The reader's rows as a JSON array of objects keyed by column name. "" when the query returned no rows.
    private static string ToJson(SqliteDataReader reader)
    {
        if (!reader.HasRows)
        {
            return string.Empty;
        }
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            while (reader.Read())
            {
                writer.WriteStartObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    writer.WritePropertyName(reader.GetName(i));
                    WriteValue(writer, reader.GetValue(i));
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case DBNull:
                writer.WriteNullValue();
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double real:
                writer.WriteNumberValue(real);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case byte[] blob:
                writer.WriteBase64StringValue(blob);
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    /// Notes a skipped read in the debug output, once for each file and error code.
    private static void LogOnce(string dbFile, int code, string message)
    {
        var key = dbFile + "|" + code;
        lock (LogLock)
        {
            if (!Logged.Add(key))
            {
                return;
            }
        }
        Debug.WriteLine($"NotchTasks: Antigravity database read skipped ({Path.GetFileName(dbFile)}, code {code}): {message}");
    }
}
