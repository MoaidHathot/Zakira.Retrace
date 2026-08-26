namespace Zakira.Retrace.Abstractions;

/// <summary>How sessions are ordered when listing.</summary>
public enum SessionSortOrder
{
    /// <summary>Most recently updated first. The default: it is what "what was I just doing" needs.</summary>
    Recent,

    /// <summary>Most recently created first.</summary>
    Created,

    /// <summary>Largest session first, by message count.</summary>
    Size
}

/// <summary>
/// Metadata-only predicate over sessions. Applied by sources when listing and translated into SQL
/// by the index when searching, so the same filter shape works in both live and indexed modes.
/// </summary>
public sealed record SessionFilter
{
    /// <summary>Restrict to these source ids. Empty means every enabled source.</summary>
    public IReadOnlyList<string> SourceIds { get; init; } = [];

    /// <summary>
    /// Restrict to sessions whose working directory is at or below this path. Matching is
    /// prefix-based and case-insensitive on Windows.
    /// </summary>
    public string? WorkspacePath { get; init; }

    /// <summary>Restrict to a repository, matched as a case-insensitive substring.</summary>
    public string? Repository { get; init; }

    /// <summary>Restrict to an exact branch name.</summary>
    public string? Branch { get; init; }

    /// <summary>Only sessions updated at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Only sessions updated at or before this instant.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>Restrict to an agent or mode name, matched case-insensitively.</summary>
    public string? Agent { get; init; }

    /// <summary>Restrict to sessions that used a model whose id contains this value.</summary>
    public string? Model { get; init; }

    /// <summary>Restrict to sessions carrying every one of these tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Drop sessions with fewer than this many messages. Filters out empty session shells.</summary>
    public int? MinMessages { get; init; }

    /// <summary>Include sessions the harness marked archived. Off by default.</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>Maximum rows to return. <c>0</c> means unbounded.</summary>
    public int Limit { get; init; }

    /// <summary>Ordering.</summary>
    public SessionSortOrder Sort { get; init; } = SessionSortOrder.Recent;

    /// <summary>A filter that matches everything, unbounded.</summary>
    public static SessionFilter All { get; } = new();
}
