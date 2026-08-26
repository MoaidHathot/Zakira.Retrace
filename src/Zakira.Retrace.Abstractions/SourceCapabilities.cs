namespace Zakira.Retrace.Abstractions;

/// <summary>What a source can do, so the CLI and MCP layers can adapt without type checks.</summary>
[Flags]
public enum SourceCapabilities
{
    /// <summary>Listing and reading only.</summary>
    None = 0,

    /// <summary>Can produce a command that reopens a session with its conversation intact.</summary>
    Resume = 1 << 0,

    /// <summary>Can branch a session rather than continuing it in place.</summary>
    Fork = 1 << 1,

    /// <summary>Records tool invocations, not just prose.</summary>
    ToolCalls = 1 << 2,

    /// <summary>Records file edits with diffs.</summary>
    Patches = 1 << 3,

    /// <summary>Exposes a usable change cursor, so the indexer can refresh incrementally.</summary>
    Incremental = 1 << 4,

    /// <summary>Has its own full-text index that live search can federate to.</summary>
    NativeSearch = 1 << 5,

    /// <summary>Records token counts and cost.</summary>
    Usage = 1 << 6,

    /// <summary>Records the files a session touched, independently of tool call parsing.</summary>
    FileTracking = 1 << 7
}

/// <summary>Whether a source can be read right now, and why not when it cannot.</summary>
public sealed record SourceAvailability
{
    /// <summary>Whether the source's data store was found and is readable.</summary>
    public required bool IsAvailable { get; init; }

    /// <summary>Resolved path to the data store, whether or not it exists.</summary>
    public string? DataPath { get; init; }

    /// <summary>Why the source is unavailable, in a form worth showing the user.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Number of sessions the source can see. <see langword="null"/> when counting would be
    /// expensive enough to be worth skipping in a health check.
    /// </summary>
    public int? SessionCount { get; init; }

    /// <summary>Harness version, when it is recorded in the store.</summary>
    public string? HarnessVersion { get; init; }

    /// <summary>Extra facts worth surfacing in `doctor`, such as which of several stores was chosen.</summary>
    public IReadOnlyList<string> Details { get; init; } = [];

    /// <summary>Builds an available result.</summary>
    public static SourceAvailability Available(string dataPath, int? sessionCount = null, params string[] details) =>
        new() { IsAvailable = true, DataPath = dataPath, SessionCount = sessionCount, Details = details };

    /// <summary>Builds an unavailable result.</summary>
    public static SourceAvailability Unavailable(string reason, string? dataPath = null) =>
        new() { IsAvailable = false, Reason = reason, DataPath = dataPath };
}
