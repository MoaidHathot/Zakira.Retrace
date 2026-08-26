using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Search;
using Zakira.Retrace.Core.Storage;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Sources.CopilotCli;

/// <summary>
/// Reads sessions from GitHub Copilot CLI's SQLite store.
/// </summary>
/// <remarks>
/// <para>Schema, as of <c>schema_version</c> 6:</para>
/// <list type="bullet">
///   <item><description><c>sessions(id, cwd, repository, branch, summary, created_at, updated_at, host_type)</c>
///   where <c>summary</c> holds the opening user prompt rather than a generated summary, and both
///   timestamps are ISO 8601 with a <c>Z</c> suffix.</description></item>
///   <item><description><c>turns(session_id, turn_index, user_message, assistant_response, timestamp)</c>,
///   one row per exchange rather than one per message.</description></item>
///   <item><description><c>assistant_usage_events(session_id, turn_index, agent_id, model, *_tokens, total_nano_aiu, ...)</c>,
///   the only place model, token, and cost data is recorded.</description></item>
///   <item><description><c>search_index</c>, an FTS5 table over turn and checkpoint text with
///   <c>source_id</c> shaped as <c>&lt;sessionId&gt;:turn:&lt;index&gt;</c>.</description></item>
/// </list>
/// <para>
/// Copilot CLI writes a session row on every launch, so a heavily used machine accumulates
/// thousands of rows with no turns at all. Those are filtered out by default rather than
/// surfaced as empty results.
/// </para>
/// </remarks>
internal sealed class CopilotCliReader(string databasePath, string snapshotDirectory)
{
    /// <summary>Nano AI units per AI unit, the denominator for <c>total_nano_aiu</c>.</summary>
    private const double NanoAiuPerAiu = 1_000_000_000d;

    private async Task<ReadOnlyDatabase> OpenAsync(CancellationToken cancellationToken) =>
        await ReadOnlyDatabase.OpenAsync(databasePath, snapshotDirectory, cancellationToken).ConfigureAwait(false);

    /// <summary>Counts sessions that have at least the configured number of turns.</summary>
    public async Task<int> CountSessionsAsync(int minTurns, CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var sql = minTurns <= 0
            ? "SELECT COUNT(*) FROM sessions;"
            : $"SELECT COUNT(*) FROM sessions s WHERE (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) >= {minTurns};";
        var count = await database.Connection.ExecuteScalarInt64Async(sql, cancellationToken).ConfigureAwait(false);
        return (int)(count ?? 0);
    }

    /// <summary>Reads the store's schema version, which is the closest thing to a harness version.</summary>
    public async Task<string?> GetSchemaVersionAsync(CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await database.Connection.TableExistsAsync("schema_version", cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var version = await database.Connection.ExecuteScalarInt64Async("SELECT MAX(version) FROM schema_version;", cancellationToken).ConfigureAwait(false);
        return version?.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the FTS index is present and populated.</summary>
    public async Task<bool> HasSearchIndexAsync(CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await database.Connection.TableExistsAsync("search_index", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the newest update timestamp. The stored ISO 8601 form sorts lexicographically, so it
    /// works directly as both a watermark and a SQL comparison operand.
    /// </summary>
    public async Task<string?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await database.Connection.ExecuteScalarStringAsync("SELECT MAX(updated_at) FROM sessions;", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Streams every session id.</summary>
    public async IAsyncEnumerable<string> ListIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = database.Connection.CreateCommand();
        command.CommandText = "SELECT id FROM sessions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return reader.GetString(0);
        }
    }

    /// <summary>Streams session metadata matching a filter.</summary>
    public async IAsyncEnumerable<SessionSummary> ListAsync(
        SessionFilter filter,
        int minTurns,
        string? updatedAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var hasUsage = await database.Connection.TableExistsAsync("assistant_usage_events", cancellationToken).ConfigureAwait(false);

        var (where, parameters) = BuildWhere(filter, minTurns, updatedAfter);
        var order = filter.Sort switch
        {
            SessionSortOrder.Created => "s.created_at DESC",
            SessionSortOrder.Size => "turn_count DESC",
            _ => "s.updated_at DESC"
        };

        var sql = $"""
            SELECT s.id,
                   s.cwd,
                   s.repository,
                   s.branch,
                   s.summary,
                   s.created_at,
                   s.updated_at,
                   (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) AS turn_count
            FROM sessions s
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

        var usage = hasUsage
            ? await ReadUsageAsync(database.Connection, rows.Select(row => row.Ref.NativeId).ToArray(), cancellationToken).ConfigureAwait(false)
            : [];

        foreach (var row in rows)
        {
            yield return usage.TryGetValue(row.Ref.NativeId, out var facts) ? Enrich(row, facts) : row;
        }
    }

    /// <summary>Reads one session's metadata without its turns.</summary>
    public async Task<SessionSummary?> GetSummaryAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var summary = await ReadSummaryAsync(database.Connection, sessionId, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        if (await database.Connection.TableExistsAsync("assistant_usage_events", cancellationToken).ConfigureAwait(false))
        {
            var usage = await ReadUsageAsync(database.Connection, [sessionId], cancellationToken).ConfigureAwait(false);
            if (usage.TryGetValue(sessionId, out var facts))
            {
                summary = Enrich(summary, facts);
            }
        }

        return summary;
    }

    /// <summary>Materialises one session.</summary>
    public async Task<SessionTranscript?> GetAsync(string sessionId, TranscriptOptions options, CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var summary = await ReadSummaryAsync(database.Connection, sessionId, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var hasUsage = await database.Connection.TableExistsAsync("assistant_usage_events", cancellationToken).ConfigureAwait(false);
        var usageByTurn = hasUsage
            ? await ReadTurnUsageAsync(database.Connection, sessionId, cancellationToken).ConfigureAwait(false)
            : [];

        if (hasUsage)
        {
            var usage = await ReadUsageAsync(database.Connection, [sessionId], cancellationToken).ConfigureAwait(false);
            if (usage.TryGetValue(sessionId, out var facts))
            {
                summary = Enrich(summary, facts);
            }
        }

        var turns = new List<Turn>();
        var totalExchanges = 0;
        var characters = 0;
        var truncated = false;
        int? nextTurn = null;

        await using (var command = database.Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT turn_index, user_message, assistant_response, timestamp
                FROM turns
                WHERE session_id = $id
                ORDER BY turn_index;
                """;
            command.Parameters.AddWithValue("$id", sessionId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                totalExchanges++;

                var turnIndex = (int)reader.GetInt64(0);
                if (options.FromTurn is { } from && turnIndex < from)
                {
                    continue;
                }

                if (options.ToTurn is { } to && turnIndex > to)
                {
                    nextTurn ??= turnIndex;
                    truncated = true;
                    continue;
                }

                var userMessage = reader.GetNullableString(1);
                var assistantResponse = reader.GetNullableString(2);
                var timestamp = SqliteExtensions.ParseSqliteDateTime(reader.GetNullableString(3));
                usageByTurn.TryGetValue(turnIndex, out var turnUsage);

                if (options.MaxCharacters > 0)
                {
                    characters += (userMessage?.Length ?? 0) + (assistantResponse?.Length ?? 0);
                    if (characters > options.MaxCharacters && turns.Count > 0)
                    {
                        nextTurn = turnIndex;
                        truncated = true;
                        break;
                    }
                }

                // Copilot CLI stores an exchange as a single row. The shared model represents a
                // conversation as alternating turns, so each row expands into up to two.
                if (!string.IsNullOrWhiteSpace(userMessage))
                {
                    turns.Add(new Turn
                    {
                        Index = turns.Count,
                        Role = TurnRole.User,
                        Timestamp = timestamp,
                        Blocks = [new TextBlock(userMessage)]
                    });
                }

                if (!string.IsNullOrWhiteSpace(assistantResponse))
                {
                    turns.Add(new Turn
                    {
                        Index = turns.Count,
                        Role = TurnRole.Assistant,
                        Timestamp = timestamp,
                        Model = turnUsage.Model,
                        Agent = turnUsage.AgentId,
                        Blocks = [new TextBlock(assistantResponse)]
                    });
                }
            }
        }

        var files = await ReadFilesAsync(database.Connection, sessionId, cancellationToken).ConfigureAwait(false);
        var references = await ReadReferencesAsync(database.Connection, sessionId, cancellationToken).ConfigureAwait(false);

        return new SessionTranscript
        {
            Summary = summary with
            {
                Stats = summary.Stats with { MessageCount = summary.Stats.MessageCount ?? totalExchanges }
            },
            Turns = turns,
            Files = files,
            References = references,
            TotalTurns = totalExchanges,
            NextTurnIndex = nextTurn,
            IsTruncated = truncated
        };
    }

    /// <summary>
    /// Runs a query against the harness's own FTS5 index.
    /// </summary>
    /// <remarks>
    /// Reusing this index rather than scanning turn text is what makes <c>--live</c> search viable
    /// on this source: the store is hundreds of megabytes and the index is already maintained by
    /// Copilot CLI itself.
    /// </remarks>
    public async IAsyncEnumerable<CopilotCliSearchMatch> SearchAsync(
        string query,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var database = await OpenAsync(cancellationToken).ConfigureAwait(false);
        if (!await database.Connection.TableExistsAsync("search_index", cancellationToken).ConfigureAwait(false))
        {
            yield break;
        }

        var matchExpression = Fts5Query.Build(query);
        if (matchExpression.Length == 0)
        {
            yield break;
        }

        await using var command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT session_id,
                   source_type,
                   source_id,
                   snippet(search_index, 0, '<<', '>>', '…', 24) AS excerpt,
                   bm25(search_index) AS rank
            FROM search_index
            WHERE search_index MATCH $query
            ORDER BY rank
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$query", matchExpression);
        command.Parameters.AddWithValue("$limit", limit);

        SqliteDataReader reader;
        try
        {
            reader = (SqliteDataReader)await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // A malformed MATCH expression is a user error, not a fault: yield nothing rather than
            // failing the whole federated search when other sources may still have results.
            yield break;
        }

        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return new CopilotCliSearchMatch(
                    reader.GetString(0),
                    reader.GetNullableString(1) ?? "turn",
                    reader.GetNullableString(2),
                    reader.GetNullableString(3) ?? string.Empty,
                    // bm25 returns a negative score where more negative is more relevant.
                    -reader.GetDouble(4));
            }
        }
    }

    private static (string Where, IReadOnlyList<(string Name, object Value)> Parameters) BuildWhere(
        SessionFilter filter,
        int minTurns,
        string? updatedAfter)
    {
        var clauses = new List<string>();
        var parameters = new List<(string, object)>();

        if (minTurns > 0)
        {
            clauses.Add($"(SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) >= {minTurns}");
        }

        if (filter.MinMessages is { } minimum && minimum > minTurns)
        {
            clauses.Add($"(SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) >= {minimum}");
        }

        if (filter.Since is { } since)
        {
            clauses.Add("s.updated_at >= $since");
            parameters.Add(("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        }

        if (filter.Until is { } until)
        {
            clauses.Add("s.updated_at <= $until");
            parameters.Add(("$until", until.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrWhiteSpace(updatedAfter))
        {
            clauses.Add("s.updated_at > $watermark");
            parameters.Add(("$watermark", updatedAfter));
        }

        if (!string.IsNullOrWhiteSpace(filter.WorkspacePath))
        {
            // Prefix matching without LIKE: escaping backslashes inside a LIKE pattern is
            // error-prone on Windows paths, and substr comparison is both simpler and exact.
            var normalized = filter.WorkspacePath.TrimEnd('/', '\\');
            clauses.Add("""
                (s.cwd = $workspace COLLATE NOCASE
                 OR substr(s.cwd, 1, length($workspace) + 1) IN ($workspaceSlash, $workspaceBackslash) COLLATE NOCASE)
                """);
            parameters.Add(("$workspace", normalized));
            parameters.Add(("$workspaceSlash", normalized + "/"));
            parameters.Add(("$workspaceBackslash", normalized + "\\"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Repository))
        {
            clauses.Add("s.repository LIKE $repository");
            parameters.Add(("$repository", $"%{filter.Repository}%"));
        }

        if (!string.IsNullOrWhiteSpace(filter.Branch))
        {
            clauses.Add("s.branch = $branch COLLATE NOCASE");
            parameters.Add(("$branch", filter.Branch));
        }

        return (clauses.Count == 0 ? string.Empty : "WHERE " + string.Join("\n  AND ", clauses), parameters);
    }

    private static async Task<SessionSummary?> ReadSummaryAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id, s.cwd, s.repository, s.branch, s.summary, s.created_at, s.updated_at,
                   (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) AS turn_count
            FROM sessions s
            WHERE s.id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapSession(reader) : null;
    }

    private static SessionSummary MapSession(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var cwd = reader.GetNullableString(1);
        var repository = reader.GetNullableString(2);
        var branch = reader.GetNullableString(3);
        var prompt = reader.GetNullableString(4);
        var created = SqliteExtensions.ParseSqliteDateTime(reader.GetNullableString(5)) ?? DateTimeOffset.UnixEpoch;
        var updated = SqliteExtensions.ParseSqliteDateTime(reader.GetNullableString(6)) ?? created;
        var turnCount = (int)reader.GetInt64(7);

        return new SessionSummary
        {
            Ref = new SessionRef(CopilotCliSessionSource.SourceId, id),
            // The `summary` column holds the opening prompt verbatim, so a readable title has to be
            // derived from it rather than used as-is.
            Title = TextUtilities.DeriveTitle(prompt),
            Preview = TextUtilities.Preview(prompt, 200),
            Workspace = new WorkspaceInfo
            {
                Path = cwd,
                Repository = repository,
                Branch = branch
            },
            CreatedAt = created,
            UpdatedAt = updated,
            Stats = new SessionStats { MessageCount = turnCount },
            ContentHash = $"{updated:O}:{turnCount}"
        };
    }

    private static SessionSummary Enrich(SessionSummary summary, UsageFacts facts) => summary with
    {
        Agent = summary.Agent ?? facts.AgentId,
        Models = facts.Models.Count > 0 ? facts.Models : summary.Models,
        Stats = summary.Stats with
        {
            TotalTokens = facts.TotalTokens > 0 ? facts.TotalTokens : null,
            Cost = facts.NanoAiu > 0 ? facts.NanoAiu / NanoAiuPerAiu : null
        }
    };

    private static async Task<Dictionary<string, UsageFacts>> ReadUsageAsync(
        SqliteConnection connection,
        string[] sessionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, UsageFacts>(StringComparer.Ordinal);
        if (sessionIds.Length == 0)
        {
            return result;
        }

        const int batchSize = 400;
        for (var offset = 0; offset < sessionIds.Length; offset += batchSize)
        {
            var batch = sessionIds.Skip(offset).Take(batchSize).ToArray();
            var placeholders = string.Join(",", batch.Select((_, index) => $"$p{index}"));

            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT session_id,
                       model,
                       MAX(agent_id),
                       SUM(COALESCE(input_tokens,0) + COALESCE(output_tokens,0) + COALESCE(reasoning_tokens,0)),
                       SUM(COALESCE(total_nano_aiu,0))
                FROM assistant_usage_events
                WHERE session_id IN ({placeholders})
                GROUP BY session_id, model;
                """;
            for (var index = 0; index < batch.Length; index++)
            {
                command.Parameters.AddWithValue($"$p{index}", batch[index]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var sessionId = reader.GetString(0);
                var model = reader.GetNullableString(1);
                var agentId = reader.GetNullableString(2);
                var tokens = reader.GetNullableInt64(3) ?? 0;
                var nanoAiu = reader.GetNullableDouble(4) ?? 0;

                if (!result.TryGetValue(sessionId, out var facts))
                {
                    facts = new UsageFacts();
                    result[sessionId] = facts;
                }

                if (!string.IsNullOrWhiteSpace(model) && !facts.Models.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    facts.Models.Add(model);
                }

                facts.AgentId ??= agentId;
                facts.TotalTokens += tokens;
                facts.NanoAiu += nanoAiu;
            }
        }

        return result;
    }

    private static async Task<Dictionary<int, (string? Model, string? AgentId)>> ReadTurnUsageAsync(
        SqliteConnection connection,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, (string?, string?)>();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT turn_index, MAX(model), MAX(agent_id)
            FROM assistant_usage_events
            WHERE session_id = $id AND turn_index IS NOT NULL
            GROUP BY turn_index;
            """;
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[(int)reader.GetInt64(0)] = (reader.GetNullableString(1), reader.GetNullableString(2));
        }

        return result;
    }

    private static async Task<IReadOnlyList<TouchedFile>> ReadFilesAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        if (!await connection.TableExistsAsync("session_files", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var files = new List<TouchedFile>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT file_path, tool_name, turn_index FROM session_files WHERE session_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            files.Add(new TouchedFile
            {
                Path = reader.GetString(0),
                Tool = reader.GetNullableString(1),
                TurnIndex = reader.GetNullableInt32(2)
            });
        }

        return files;
    }

    private static async Task<IReadOnlyList<SessionReference>> ReadReferencesAsync(SqliteConnection connection, string sessionId, CancellationToken cancellationToken)
    {
        if (!await connection.TableExistsAsync("session_refs", cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var references = new List<SessionReference>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ref_type, ref_value, turn_index FROM session_refs WHERE session_id = $id ORDER BY id;";
        command.Parameters.AddWithValue("$id", sessionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            references.Add(new SessionReference
            {
                Type = reader.GetString(0),
                Value = reader.GetString(1),
                TurnIndex = reader.GetNullableInt32(2)
            });
        }

        return references;
    }

    private sealed class UsageFacts
    {
        public List<string> Models { get; } = [];

        public string? AgentId { get; set; }

        public long TotalTokens { get; set; }

        public double NanoAiu { get; set; }
    }
}

/// <summary>One hit from Copilot CLI's own full-text index.</summary>
internal sealed record CopilotCliSearchMatch(string SessionId, string SourceType, string? SourceId, string Excerpt, double Score)
{
    /// <summary>
    /// Turn index parsed out of the <c>&lt;sessionId&gt;:turn:&lt;index&gt;</c> source id, when the
    /// hit came from a turn rather than a checkpoint.
    /// </summary>
    public int? TurnIndex
    {
        get
        {
            if (SourceId is null)
            {
                return null;
            }

            var marker = SourceId.LastIndexOf(":turn:", StringComparison.Ordinal);
            return marker >= 0 && int.TryParse(SourceId.AsSpan(marker + 6), out var index) ? index : null;
        }
    }
}
