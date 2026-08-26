using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Zakira.Retrace.Core.Storage;

/// <summary>
/// Small helpers over <see cref="SqliteCommand"/> and <see cref="DbDataReader"/> that remove the
/// repetitive null and type handling from every source implementation.
/// </summary>
public static class SqliteExtensions
{
    /// <summary>Creates a command with parameters bound from a name/value sequence.</summary>
    public static SqliteCommand CreateCommand(this SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    /// <summary>Reads a nullable string column.</summary>
    public static string? GetNullableString(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    /// <summary>Reads a nullable 64-bit integer column.</summary>
    public static long? GetNullableInt64(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    /// <summary>Reads a nullable 32-bit integer column, narrowing from SQLite's 64-bit storage.</summary>
    public static int? GetNullableInt32(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : (int)reader.GetInt64(ordinal);

    /// <summary>Reads a nullable floating point column.</summary>
    public static double? GetNullableDouble(this DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    /// <summary>
    /// Reads a timestamp stored as epoch milliseconds, which is how OpenCode records every time.
    /// </summary>
    public static DateTimeOffset? GetEpochMilliseconds(this DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetInt64(ordinal);
        return value <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }

    /// <summary>
    /// Reads a timestamp stored as SQLite's <c>datetime('now')</c> text, which is how Copilot CLI
    /// records every time. Those values carry no offset and are always UTC.
    /// </summary>
    public static DateTimeOffset? GetSqliteDateTime(this DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return ParseSqliteDateTime(reader.GetString(ordinal));
    }

    /// <summary>Parses SQLite's <c>datetime('now')</c> text form, or an ISO 8601 string.</summary>
    public static DateTimeOffset? ParseSqliteDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // datetime('now') yields "YYYY-MM-DD HH:MM:SS" with an implicit UTC zone.
        if (DateTime.TryParseExact(
                value,
                ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.FFF"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var exact))
        {
            return new DateTimeOffset(exact, TimeSpan.Zero);
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Executes a scalar query and returns it as a nullable 64-bit integer.</summary>
    public static async Task<long?> ExecuteScalarInt64Async(this SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Executes a scalar query and returns it as a nullable string.</summary>
    public static async Task<string?> ExecuteScalarStringAsync(this SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reports whether a table exists. Session store schemas evolve between harness releases, so
    /// sources probe before querying anything that is not part of the original schema.
    /// </summary>
    public static async Task<bool> TableExistsAsync(this SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(
            "SELECT 1 FROM sqlite_master WHERE type IN ('table','view') AND name = $name LIMIT 1;",
            ("$name", table));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null and not DBNull;
    }

    /// <summary>Reports whether a column exists on a table.</summary>
    public static async Task<bool> ColumnExistsAsync(this SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand($"PRAGMA table_info({Quote(table)});");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Quotes an identifier for interpolation into a statement. Only ever applied to names that
    /// come from this codebase or from <c>sqlite_master</c>, never from user input.
    /// </summary>
    public static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
