namespace Zakira.Retrace.Abstractions;

/// <summary>
/// One indexable passage extracted from a session.
/// </summary>
/// <remarks>
/// Chunks are the unit of both keyword and vector retrieval. <see cref="IsEmbeddable"/> is what
/// keeps the vector store tractable: tool output and diffs are indexed for keyword search, where
/// they are genuinely useful for "which session ran that command", but are never embedded. Across
/// the supported harnesses they account for the overwhelming majority of stored bytes while
/// carrying almost no semantic signal.
/// </remarks>
public sealed record SessionChunk
{
    /// <summary>Position of this chunk within its session.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Turn the chunk was extracted from.</summary>
    public int TurnIndex { get; init; }

    /// <summary>Who produced the underlying turn.</summary>
    public required TurnRole Role { get; init; }

    /// <summary>When the underlying turn happened.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>The passage text.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Whether this chunk should get a vector. Keyword indexing applies to every chunk regardless.
    /// </summary>
    public bool IsEmbeddable { get; init; } = true;
}

/// <summary>How a transcript is split into chunks.</summary>
public sealed record ChunkPolicy
{
    /// <summary>Target chunk size in characters.</summary>
    public int MaxCharacters { get; init; } = 1200;

    /// <summary>
    /// Characters of trailing context repeated at the start of the next chunk, so a match that
    /// straddles a boundary is still found.
    /// </summary>
    public int OverlapCharacters { get; init; } = 150;

    /// <summary>Drop chunks shorter than this. Filters out one-word acknowledgements.</summary>
    public int MinCharacters { get; init; } = 16;

    /// <summary>Index tool names, arguments, and output for keyword search.</summary>
    public bool IncludeToolOutput { get; init; } = true;

    /// <summary>Cap a single tool output before indexing it. <c>0</c> means unbounded.</summary>
    public int MaxToolOutputCharacters { get; init; } = 4000;

    /// <summary>Index model reasoning.</summary>
    public bool IncludeReasoning { get; init; } = true;

    /// <summary>Defaults.</summary>
    public static ChunkPolicy Default { get; } = new();
}

/// <summary>
/// Implemented by sources that can stream chunks without building a whole
/// <see cref="SessionTranscript"/> first. Optional: the indexer falls back to chunking a
/// transcript when a source does not implement it.
/// </summary>
public interface IChunkStreamSource
{
    /// <summary>Streams the indexable passages of one session.</summary>
    IAsyncEnumerable<SessionChunk> StreamChunksAsync(string nativeId, ChunkPolicy policy, CancellationToken cancellationToken);
}
