namespace Zakira.Retrace.Abstractions;

/// <summary>A file a session read or wrote.</summary>
public sealed record TouchedFile
{
    /// <summary>Path as the harness recorded it.</summary>
    public required string Path { get; init; }

    /// <summary>Tool that touched it, when known.</summary>
    public string? Tool { get; init; }

    /// <summary>Turn index at which it was first touched.</summary>
    public int? TurnIndex { get; init; }
}

/// <summary>
/// An external reference extracted from a session: a URL, issue, pull request, or commit. Only
/// Copilot CLI records these natively today; other sources leave the list empty.
/// </summary>
public sealed record SessionReference
{
    /// <summary>Reference kind as the harness classified it, such as <c>url</c> or <c>pr</c>.</summary>
    public required string Type { get; init; }

    /// <summary>The reference itself.</summary>
    public required string Value { get; init; }

    /// <summary>Turn index the reference appeared in.</summary>
    public int? TurnIndex { get; init; }
}

/// <summary>
/// A fully hydrated session: the summary plus every turn and the derived file and reference lists.
/// Produced only on explicit request, never while listing.
/// </summary>
public sealed record SessionTranscript
{
    /// <summary>The listing projection of this same session.</summary>
    public required SessionSummary Summary { get; init; }

    /// <summary>Ordered turns.</summary>
    public IReadOnlyList<Turn> Turns { get; init; } = [];

    /// <summary>Files the session touched.</summary>
    public IReadOnlyList<TouchedFile> Files { get; init; } = [];

    /// <summary>External references the session mentioned.</summary>
    public IReadOnlyList<SessionReference> References { get; init; } = [];

    /// <summary>
    /// Total number of turns in the underlying session, which is larger than
    /// <see cref="Turns"/>.Count when the caller requested a window.
    /// </summary>
    public int TotalTurns { get; init; }

    /// <summary>Index of the first turn after this window, or <see langword="null"/> when complete.</summary>
    public int? NextTurnIndex { get; init; }

    /// <summary>Whether content was dropped to satisfy a character budget.</summary>
    public bool IsTruncated { get; init; }
}
