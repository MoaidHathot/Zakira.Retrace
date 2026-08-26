namespace Zakira.Retrace.Abstractions;

/// <summary>
/// Cheap, roll-up counters for a session. Every field is optional because no single harness
/// records all of them: OpenCode tracks tokens, cost, and a diff summary; Copilot CLI tracks
/// turns and touched files; VS Code tracks neither tokens nor cost.
/// </summary>
public sealed record SessionStats
{
    /// <summary>Number of messages/turns in the session.</summary>
    public int? MessageCount { get; init; }

    /// <summary>Number of tool invocations across the session.</summary>
    public int? ToolCallCount { get; init; }

    /// <summary>Total tokens consumed, when the harness records it.</summary>
    public long? TotalTokens { get; init; }

    /// <summary>Total cost in the harness's own accounting units, when recorded.</summary>
    public double? Cost { get; init; }

    /// <summary>Lines added across all edits the session made.</summary>
    public int? Additions { get; init; }

    /// <summary>Lines deleted across all edits the session made.</summary>
    public int? Deletions { get; init; }

    /// <summary>Distinct files the session edited.</summary>
    public int? FilesChanged { get; init; }
}
