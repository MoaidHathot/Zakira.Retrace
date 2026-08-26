using Microsoft.Data.Sqlite;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Index;

namespace Zakira.Retrace.Core.Services;

/// <summary>A tag applied to a session.</summary>
public sealed record SessionTag(string Tag, string Origin, DateTimeOffset CreatedAt);

/// <summary>
/// Stores tags alongside the index.
/// </summary>
/// <remarks>
/// <para>
/// Tags live in the index database but are keyed by session URI rather than by row id, so they
/// survive a full index rebuild. That distinction matters: everything else in the index is derived
/// data that can be regenerated, while a tag a user typed cannot be.
/// </para>
/// <para>
/// The <c>origin</c> column separates tags a user entered from tags assigned automatically. Nothing
/// writes <c>auto</c> yet; the column exists so a future classifier can populate and re-populate
/// its own tags without ever touching a manual one.
/// </para>
/// </remarks>
public sealed class TagStore(RetracePaths paths, RetraceConfig config)
{
    private string IndexPath => string.IsNullOrWhiteSpace(config.Index.Path)
        ? paths.DefaultIndexPath
        : paths.ExpandPath(config.Index.Path);

    /// <summary>Reads a session's tags.</summary>
    public async Task<IReadOnlyList<SessionTag>> GetAsync(string sessionUri, CancellationToken cancellationToken)
    {
        await using var connection = await RetraceIndexSchema.OpenAsync(IndexPath, cancellationToken).ConfigureAwait(false);

        var tags = new List<SessionTag>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag, origin, created_utc FROM tag WHERE session_uri = $uri ORDER BY tag;";
        command.Parameters.AddWithValue("$uri", sessionUri);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tags.Add(new SessionTag(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        }

        return tags;
    }

    /// <summary>Adds tags, ignoring duplicates.</summary>
    public async Task<int> AddAsync(string sessionUri, IReadOnlyList<string> tags, string origin, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return 0;
        }

        await using var connection = await RetraceIndexSchema.OpenAsync(IndexPath, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var added = 0;
        foreach (var tag in tags.Select(Normalize).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO tag (session_uri, tag, origin, created_utc)
                VALUES ($uri, $tag, $origin, $now)
                ON CONFLICT(session_uri, tag) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$uri", sessionUri);
            command.Parameters.AddWithValue("$tag", tag);
            command.Parameters.AddWithValue("$origin", origin);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            added += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }

    /// <summary>Removes tags.</summary>
    public async Task<int> RemoveAsync(string sessionUri, IReadOnlyList<string> tags, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return 0;
        }

        await using var connection = await RetraceIndexSchema.OpenAsync(IndexPath, cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var removed = 0;
        foreach (var tag in tags.Select(Normalize).Where(tag => tag.Length > 0))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tag WHERE session_uri = $uri AND tag = $tag COLLATE NOCASE;";
            command.Parameters.AddWithValue("$uri", sessionUri);
            command.Parameters.AddWithValue("$tag", tag);

            removed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    /// <summary>Lists every tag in use, with how many sessions carry it.</summary>
    public async Task<IReadOnlyList<(string Tag, int Count)>> ListAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = await RetraceIndexSchema.OpenAsync(IndexPath, cancellationToken).ConfigureAwait(false);

        var tags = new List<(string, int)>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tag, COUNT(*) FROM tag GROUP BY tag ORDER BY COUNT(*) DESC, tag;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            tags.Add((reader.GetString(0), (int)reader.GetInt64(1)));
        }

        return tags;
    }

    /// <summary>
    /// Normalises a tag: trimmed, lower-cased, with internal whitespace collapsed to hyphens, so
    /// "Bug Fix" and "bug-fix" are the same tag rather than two.
    /// </summary>
    private static string Normalize(string tag) =>
        string.Join('-', tag.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
