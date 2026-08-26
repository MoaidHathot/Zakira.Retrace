namespace Zakira.Retrace.Abstractions;

/// <summary>Which retrieval signals contribute to a search.</summary>
public enum SearchMode
{
    /// <summary>Fuse keyword and vector results. The default when an embedding model is available.</summary>
    Hybrid,

    /// <summary>Keyword only. Always available; the automatic fallback when no model is installed.</summary>
    Lexical,

    /// <summary>Vector only. Useful for diagnosing recall problems.</summary>
    Semantic
}

/// <summary>A content search request.</summary>
public sealed record SearchQuery
{
    /// <summary>The raw user query.</summary>
    public required string Text { get; init; }

    /// <summary>Metadata predicate applied before ranking.</summary>
    public SessionFilter Filter { get; init; } = SessionFilter.All;

    /// <summary>Maximum sessions to return.</summary>
    public int Top { get; init; } = 20;

    /// <summary>Maximum snippets to attach per session.</summary>
    public int SnippetsPerSession { get; init; } = 3;

    /// <summary>Which signals to use.</summary>
    public SearchMode Mode { get; init; } = SearchMode.Hybrid;

    /// <summary>
    /// Bypass the two-tier vector shortlist and score every chunk vector that survives the metadata
    /// filter. Slower by roughly an order of magnitude, but it is the escape hatch when the
    /// session-level shortlist misses a match buried deep in a long session.
    /// </summary>
    public bool Deep { get; init; }
}

/// <summary>A matched passage inside a session, with enough context to display without a second read.</summary>
public sealed record SearchSnippet
{
    /// <summary>Who produced the passage.</summary>
    public required TurnRole Role { get; init; }

    /// <summary>Turn index the passage came from.</summary>
    public int TurnIndex { get; init; }

    /// <summary>When it was produced.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>The passage, already trimmed to a display-friendly length.</summary>
    public required string Text { get; init; }

    /// <summary>The passage with matched terms wrapped in the highlight markers, when available.</summary>
    public string? Highlighted { get; init; }
}

/// <summary>One ranked search result.</summary>
public sealed record SearchHit
{
    /// <summary>The matched session.</summary>
    public required SessionSummary Session { get; init; }

    /// <summary>Fused relevance score. Comparable within one result set only.</summary>
    public double Score { get; init; }

    /// <summary>Rank contribution from the keyword signal, for diagnostics.</summary>
    public double LexicalScore { get; init; }

    /// <summary>Rank contribution from the vector signal, for diagnostics.</summary>
    public double SemanticScore { get; init; }

    /// <summary>Matched passages.</summary>
    public IReadOnlyList<SearchSnippet> Snippets { get; init; } = [];
}
