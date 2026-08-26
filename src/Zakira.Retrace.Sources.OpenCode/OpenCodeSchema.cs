using Microsoft.Data.Sqlite;
using Zakira.Retrace.Core.Storage;

namespace Zakira.Retrace.Sources.OpenCode;

/// <summary>
/// Which optional columns the installed OpenCode database actually has.
/// </summary>
/// <remarks>
/// OpenCode's schema is managed by migrations that add columns over time: <c>agent</c>,
/// <c>model</c>, <c>cost</c>, and the <c>tokens_*</c> family all arrived after the initial session
/// table. Probing once per connection and building the projection from the result means Retrace
/// reads a two-year-old install and a nightly build with the same code path, instead of failing on
/// "no such column".
/// </remarks>
internal sealed record OpenCodeSchema
{
    /// <summary>Whether the <c>session.agent</c> column exists.</summary>
    public bool HasAgent { get; init; }

    /// <summary>Whether the <c>session.model</c> JSON column exists.</summary>
    public bool HasModel { get; init; }

    /// <summary>Whether the <c>session.cost</c> column exists.</summary>
    public bool HasCost { get; init; }

    /// <summary>Whether the <c>session.tokens_*</c> columns exist.</summary>
    public bool HasTokens { get; init; }

    /// <summary>Whether the <c>session.time_archived</c> column exists.</summary>
    public bool HasArchived { get; init; }

    /// <summary>Whether the <c>session.parent_id</c> column exists.</summary>
    public bool HasParent { get; init; }

    /// <summary>Whether the summary roll-up columns exist.</summary>
    public bool HasSummary { get; init; }

    /// <summary>Whether a <c>project</c> table exists to join worktree metadata from.</summary>
    public bool HasProject { get; init; }

    /// <summary>Probes the connection.</summary>
    public static async Task<OpenCodeSchema> DiscoverAsync(SqliteConnection connection, CancellationToken cancellationToken) => new()
    {
        HasAgent = await connection.ColumnExistsAsync("session", "agent", cancellationToken).ConfigureAwait(false),
        HasModel = await connection.ColumnExistsAsync("session", "model", cancellationToken).ConfigureAwait(false),
        HasCost = await connection.ColumnExistsAsync("session", "cost", cancellationToken).ConfigureAwait(false),
        HasTokens = await connection.ColumnExistsAsync("session", "tokens_output", cancellationToken).ConfigureAwait(false),
        HasArchived = await connection.ColumnExistsAsync("session", "time_archived", cancellationToken).ConfigureAwait(false),
        HasParent = await connection.ColumnExistsAsync("session", "parent_id", cancellationToken).ConfigureAwait(false),
        HasSummary = await connection.ColumnExistsAsync("session", "summary_files", cancellationToken).ConfigureAwait(false),
        HasProject = await connection.TableExistsAsync("project", cancellationToken).ConfigureAwait(false)
    };

    /// <summary>
    /// Builds the session projection, substituting <c>NULL</c> for every column this database does
    /// not have so the reader can always use fixed ordinals.
    /// </summary>
    public string BuildSessionProjection() => string.Join(",\n       ",
    [
        "s.id",
        "s.project_id",
        HasParent ? "s.parent_id" : "NULL AS parent_id",
        "s.slug",
        "s.directory",
        "s.title",
        "s.version",
        HasSummary ? "s.summary_additions" : "NULL AS summary_additions",
        HasSummary ? "s.summary_deletions" : "NULL AS summary_deletions",
        HasSummary ? "s.summary_files" : "NULL AS summary_files",
        "s.time_created",
        "s.time_updated",
        HasArchived ? "s.time_archived" : "NULL AS time_archived",
        HasAgent ? "s.agent" : "NULL AS agent",
        HasModel ? "s.model" : "NULL AS model",
        HasCost ? "s.cost" : "NULL AS cost",
        HasTokens ? "s.tokens_input" : "NULL AS tokens_input",
        HasTokens ? "s.tokens_output" : "NULL AS tokens_output",
        HasTokens ? "s.tokens_reasoning" : "NULL AS tokens_reasoning",
        HasProject ? "p.worktree" : "NULL AS worktree",
        HasProject ? "p.vcs" : "NULL AS vcs",
        HasProject ? "p.name" : "NULL AS project_name"
    ]);

    /// <summary>The <c>FROM</c> clause, joining the project table only when it exists.</summary>
    public string BuildFromClause() => HasProject
        ? "FROM session s LEFT JOIN project p ON p.id = s.project_id"
        : "FROM session s";
}

/// <summary>Fixed column ordinals for the projection above.</summary>
internal static class OpenCodeColumns
{
    public const int Id = 0;
    public const int ProjectId = 1;
    public const int ParentId = 2;
    public const int Slug = 3;
    public const int Directory = 4;
    public const int Title = 5;
    public const int Version = 6;
    public const int SummaryAdditions = 7;
    public const int SummaryDeletions = 8;
    public const int SummaryFiles = 9;
    public const int TimeCreated = 10;
    public const int TimeUpdated = 11;
    public const int TimeArchived = 12;
    public const int Agent = 13;
    public const int Model = 14;
    public const int Cost = 15;
    public const int TokensInput = 16;
    public const int TokensOutput = 17;
    public const int TokensReasoning = 18;
    public const int Worktree = 19;
    public const int Vcs = 20;
    public const int ProjectName = 21;
}
