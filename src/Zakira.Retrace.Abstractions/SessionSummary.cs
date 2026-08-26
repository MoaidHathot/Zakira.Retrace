namespace Zakira.Retrace.Abstractions;

/// <summary>
/// The listing/search projection of a session: everything needed to render a result row and to
/// decide whether to open it, without touching the message bodies.
/// </summary>
/// <remarks>
/// Producing a <see cref="SessionSummary"/> must stay cheap. The session stores this reads from
/// are large (OpenCode's database is multi-gigabyte, Copilot CLI's is high hundreds of megabytes)
/// and the message payloads are where nearly all of that volume lives. Sources are expected to
/// satisfy listing from metadata tables/headers alone and to defer body reads to
/// <see cref="ISessionSource.GetAsync"/>.
/// </remarks>
public sealed record SessionSummary
{
    /// <summary>Global handle for this session.</summary>
    public required SessionRef Ref { get; init; }

    /// <summary>
    /// Session title. Harnesses that generate one (OpenCode, Copilot CLI summaries) supply it
    /// directly; the rest fall back to a trimmed first user message.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>A short excerpt of the opening user message, for disambiguating similar titles.</summary>
    public string? Preview { get; init; }

    /// <summary>Where the session ran.</summary>
    public WorkspaceInfo? Workspace { get; init; }

    /// <summary>Agent or mode name, such as <c>build</c>, <c>plan</c>, <c>ask</c>, or <c>agent</c>.</summary>
    public string? Agent { get; init; }

    /// <summary>Distinct models used across the session, in first-seen order.</summary>
    public IReadOnlyList<string> Models { get; init; } = [];

    /// <summary>When the session started.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the session was last written to.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Roll-up counters.</summary>
    public SessionStats Stats { get; init; } = new();

    /// <summary>Tags applied to this session, whether entered manually or assigned automatically.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// A cheap change token. The indexer stores this and skips re-chunking a session whose hash is
    /// unchanged, so it must vary whenever the session's indexable content varies. Sources
    /// typically compose it from the update timestamp plus a message count.
    /// </summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>Whether the harness has marked this session archived or deleted.</summary>
    public bool IsArchived { get; init; }

    /// <summary>
    /// Parent session id, for harnesses that support forking or sub-agent sessions. Native form,
    /// not a Retrace URI.
    /// </summary>
    public string? ParentNativeId { get; init; }
}
