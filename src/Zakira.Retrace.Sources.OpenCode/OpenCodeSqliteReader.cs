using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Json;
using Zakira.Retrace.Core.Storage;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Sources.OpenCode;

/// <summary>
/// Reads sessions from OpenCode's SQLite store.
/// </summary>
/// <remarks>
/// The store is large: a routine install reaches multiple gigabytes, nearly all of it inside the
/// <c>part.data</c> JSON blobs holding tool output and diffs. Every query here is written so the
/// blob columns are only touched when a caller has explicitly asked for a transcript. Listing works
/// entirely off the <c>session</c> table plus one batched count.
/// </remarks>
internal sealed class OpenCodeSqliteReader(string databasePath, string snapshotDirectory)
{
    /// <summary>
    /// Part types worth materialising. <c>step-start</c> and <c>step-finish</c> alone account for
    /// roughly 45% of all part rows and carry no content, so they are excluded in SQL rather than
    /// filtered after the blobs have already been read across the process boundary.
    /// </summary>
    private const string ContentPartTypes = "'text','reasoning','tool','patch'";

    private async Task<ReadOnlyDatabase> OpenAsync(CancellationToken cancellationToken) =>
        await ReadOnlyDatabase.OpenAsync(databasePath, snapshotDirectory, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Counts the sessions that would actually be listed, for the availability probe.
    /// </summary>
    /// <remarks>
    /// Child sessions are excluded unless configured otherwise, so this has to apply the same
    /// predicate as listing. Reporting the raw table count would tell the user 2,000 sessions are
    /// available and then index 800 of them, which reads as a bug.
    /// </remarks>
    public async Task<int> CountSessionsAsync(bool includeChildSessions, CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = await OpenCodeSchema.DiscoverAsync(database.Connection, cancellationToken).ConfigureAwait(false);

        var predicate = !includeChildSessions && schema.HasParent ? " WHERE parent_id IS NULL" : string.Empty;
        var count = await database.Connection
            .ExecuteScalarInt64Async($"SELECT COUNT(*) FROM session{predicate};", cancellationToken)
            .ConfigureAwait(false);

        return (int)(count ?? 0);
    }

    /// <summary>Reads the newest session version string, which reports the OpenCode build in use.</summary>
    public async Task<string?> GetHarnessVersionAsync(CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await database.Connection
            .ExecuteScalarStringAsync("SELECT version FROM session WHERE version IS NOT NULL ORDER BY time_updated DESC LIMIT 1;", cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Returns the newest update timestamp, used as the incremental watermark.</summary>
    public async Task<string?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var value = await database.Connection
            .ExecuteScalarInt64Async("SELECT MAX(time_updated) FROM session;", cancellationToken)
            .ConfigureAwait(false);
        return value?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Streams every session id, for prune detection.</summary>
    public async IAsyncEnumerable<string> ListIdsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT id FROM session;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reader.GetString(0);
        }
    }

    /// <summary>Streams session metadata matching a filter.</summary>
    public async IAsyncEnumerable<SessionSummary> ListAsync(
        SessionFilter filter,
        bool includeChildSessions,
        string? updatedAfter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = await OpenCodeSchema.DiscoverAsync(database.Connection, cancellationToken).ConfigureAwait(false);

        var (where, parameters) = BuildWhere(filter, schema, includeChildSessions, updatedAfter);
        var order = filter.Sort switch
        {
            SessionSortOrder.Created => "s.time_created DESC",
            SessionSortOrder.Size => "s.time_updated DESC",
            _ => "s.time_updated DESC"
        };

        var sql = $"""
            SELECT {schema.BuildSessionProjection()}
            {schema.BuildFromClause()}
            {where}
            ORDER BY {order}
            {(filter.Limit > 0 ? $"LIMIT {filter.Limit}" : string.Empty)};
            """;

        var rows = new List<SessionSummary>();
        await using (var command = database.Connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(MapSession(reader));
            }
        }

        // Message counts come from one batched query over the page that was actually returned,
        // rather than a correlated subquery evaluated per candidate row. On a store with a hundred
        // thousand messages that difference is the whole latency budget of `retrace list`.
        var counts = await CountMessagesAsync(database.Connection, rows.Select(row => row.Ref.NativeId).ToArray(), cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            var count = counts.GetValueOrDefault(row.Ref.NativeId);
            if (filter.MinMessages is { } minimum && count < minimum)
            {
                continue;
            }

            yield return row with { Stats = row.Stats with { MessageCount = count } };
        }
    }

    /// <summary>Materialises one session.</summary>
    public async Task<SessionTranscript?> GetAsync(string sessionId, TranscriptOptions options, CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var schema = await OpenCodeSchema.DiscoverAsync(database.Connection, cancellationToken).ConfigureAwait(false);

        SessionSummary? summary = null;
        await using (var command = database.Connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT {schema.BuildSessionProjection()}
                {schema.BuildFromClause()}
                WHERE s.id = $id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                summary = MapSession(reader);
            }
        }

        if (summary is null)
        {
            return null;
        }

        var messages = await ReadMessagesAsync(database.Connection, sessionId, cancellationToken).ConfigureAwait(false);
        var partsByMessage = await ReadPartsAsync(database.Connection, sessionId, cancellationToken).ConfigureAwait(false);

        return OpenCodeTranscriptBuilder.Build(summary, messages, partsByMessage, options);
    }

    private static (string Where, IReadOnlyList<(string Name, object Value)> Parameters) BuildWhere(
        SessionFilter filter,
        OpenCodeSchema schema,
        bool includeChildSessions,
        string? updatedAfter)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object)>();

        if (!includeChildSessions && schema.HasParent)
        {
            clauses.Add("s.parent_id IS NULL");
        }

        if (!filter.IncludeArchived && schema.HasArchived)
        {
            clauses.Add("s.time_archived IS NULL");
        }

        if (filter.Since is { } since)
        {
            clauses.Add("s.time_updated >= $since");
            parameters.Add(("$since", since.ToUnixTimeMilliseconds()));
        }

        if (filter.Until is { } until)
        {
            clauses.Add("s.time_updated <= $until");
            parameters.Add(("$until", until.ToUnixTimeMilliseconds()));
        }

        if (updatedAfter is not null && long.TryParse(updatedAfter, out var watermark))
        {
            clauses.Add("s.time_updated > $watermark");
            parameters.Add(("$watermark", watermark));
        }

        if (!string.IsNullOrWhiteSpace(filter.WorkspacePath))
        {
            // OpenCode stores forward slashes in `directory` even on Windows, and callers pass
            // native paths, so both sides are normalised before comparison. LIKE with an escaped
            // prefix keeps the match sargable.
            var normalized = filter.WorkspacePath.Replace('\\', '/').TrimEnd('/');
            clauses.Add("(REPLACE(s.directory, '\\', '/') = $workspace OR REPLACE(s.directory, '\\', '/') LIKE $workspacePrefix)");
            parameters.Add(("$workspace", normalized));
            parameters.Add(("$workspacePrefix", normalized + "/%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Agent) && schema.HasAgent)
        {
            clauses.Add("s.agent = $agent COLLATE NOCASE");
            parameters.Add(("$agent", filter.Agent));
        }

        if (!string.IsNullOrWhiteSpace(filter.Model) && schema.HasModel)
        {
            clauses.Add("s.model LIKE $model");
            parameters.Add(("$model", $"%{filter.Model}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Repository) && schema.HasProject)
        {
            clauses.Add("(p.worktree LIKE $repository OR s.directory LIKE $repository)");
            parameters.Add(("$repository", $"%{filter.Repository}%"));
        }

        return (clauses.Count == 0 ? string.Empty : "WHERE " + string.Join("\n  AND ", clauses), parameters);
    }

    private static SessionSummary MapSession(DbDataReader reader)
    {
        var id = reader.GetString(OpenCodeColumns.Id);
        var directory = reader.GetNullableString(OpenCodeColumns.Directory);
        var worktree = reader.GetNullableString(OpenCodeColumns.Worktree);
        var created = reader.GetEpochMilliseconds(OpenCodeColumns.TimeCreated) ?? DateTimeOffset.UnixEpoch;
        var updated = reader.GetEpochMilliseconds(OpenCodeColumns.TimeUpdated) ?? created;
        var title = reader.GetNullableString(OpenCodeColumns.Title);
        var slug = reader.GetNullableString(OpenCodeColumns.Slug);

        var models = new List<string>();
        if (ParseModelId(reader.GetNullableString(OpenCodeColumns.Model)) is { } modelId)
        {
            models.Add(modelId);
        }

        var tokensInput = reader.GetNullableInt64(OpenCodeColumns.TokensInput) ?? 0;
        var tokensOutput = reader.GetNullableInt64(OpenCodeColumns.TokensOutput) ?? 0;
        var tokensReasoning = reader.GetNullableInt64(OpenCodeColumns.TokensReasoning) ?? 0;
        var totalTokens = tokensInput + tokensOutput + tokensReasoning;

        return new SessionSummary
        {
            Ref = new SessionRef(OpenCodeSessionSource.SourceId, id),
            Title = string.IsNullOrWhiteSpace(title) ? slug ?? "(untitled session)" : title,
            Preview = null,
            Workspace = new WorkspaceInfo
            {
                Path = directory ?? worktree,
                Repository = worktree,
                DisplayName = reader.GetNullableString(OpenCodeColumns.ProjectName)
            },
            Agent = reader.GetNullableString(OpenCodeColumns.Agent),
            Models = models,
            CreatedAt = created,
            UpdatedAt = updated,
            Stats = new SessionStats
            {
                TotalTokens = totalTokens > 0 ? totalTokens : null,
                Cost = reader.GetNullableDouble(OpenCodeColumns.Cost),
                Additions = reader.GetNullableInt32(OpenCodeColumns.SummaryAdditions),
                Deletions = reader.GetNullableInt32(OpenCodeColumns.SummaryDeletions),
                FilesChanged = reader.GetNullableInt32(OpenCodeColumns.SummaryFiles)
            },
            // time_updated moves on every write, and tokens_output moves whenever content is added,
            // so the pair detects both metadata-only and content changes without reading messages.
            ContentHash = $"{updated.ToUnixTimeMilliseconds()}:{tokensOutput}",
            IsArchived = reader.GetNullableInt64(OpenCodeColumns.TimeArchived) is > 0,
            ParentNativeId = reader.GetNullableString(OpenCodeColumns.ParentId)
        };
    }

    private static string? ParseModelId(string? modelJson)
    {
        if (string.IsNullOrWhiteSpace(modelJson))
        {
            return null;
        }

        // The column holds {"id":"...","providerID":"...","variant":"..."} on current builds and a
        // bare model id string on older ones.
        using var document = RetraceJson.TryParse(modelJson);
        if (document is null)
        {
            return modelJson;
        }

        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.String)
        {
            return root.GetString();
        }

        return root.GetStringOrNull("id") ?? root.GetStringOrNull("modelID");
    }

    private static async Task<Dictionary<string, int>> CountMessagesAsync(SqliteConnection connection, string[] sessionIds, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (sessionIds.Length == 0)
        {
            return counts;
        }

        // Chunked so the generated IN list stays well below SQLite's variable limit.
        const int batchSize = 400;
        for (var offset = 0; offset < sessionIds.Length; offset += batchSize)
        {
            var batch = sessionIds.Skip(offset).Take(batchSize).ToArray();
            var placeholders = string.Join(",", batch.Select((_, index) => $"$p{index}"));

            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT session_id, COUNT(*) FROM message WHERE session_id IN ({placeholders}) GROUP BY session_id;";
            for (var index = 0; index < batch.Length; index++)
            {
                command.Parameters.AddWithValue($"$p{index}", batch[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                counts[reader.GetString(0)] = (int)reader.GetInt64(1);
            }
        }

        return counts;
    }

    private static async Task<List<OpenCodeMessage>> ReadMessagesAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        var messages = new List<OpenCodeMessage>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, data, time_created
            FROM message
            WHERE session_id = $id
            ORDER BY time_created, id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new OpenCodeMessage(
                reader.GetString(0),
                reader.GetNullableString(1) ?? "{}",
                reader.GetEpochMilliseconds(2)));
        }

        return messages;
    }

    private static async Task<Dictionary<string, List<string>>> ReadPartsAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        var parts = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        // json_extract in the predicate is evaluated per row, but it avoids transferring the
        // step-start/step-finish blobs at all, which is the far larger cost.
        command.CommandText = $"""
            SELECT message_id, data
            FROM part
            WHERE session_id = $id
              AND json_extract(data, '$.type') IN ({ContentPartTypes})
            ORDER BY message_id, id;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var messageId = reader.GetString(0);
            if (!parts.TryGetValue(messageId, out var list))
            {
                list = [];
                parts[messageId] = list;
            }

            list.Add(reader.GetNullableString(1) ?? "{}");
        }

        return parts;
    }
}

/// <summary>One row of OpenCode's <c>message</c> table, with its payload still unparsed.</summary>
internal sealed record OpenCodeMessage(string Id, string Data, DateTimeOffset? TimeCreated);
