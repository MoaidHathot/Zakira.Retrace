namespace Zakira.Retrace.Abstractions;

/// <summary>
/// A place sessions can be read from. One implementation per harness.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are constructed once per process and must be safe for concurrent use, because
/// both the indexer and the live query path fan out across sources in parallel.
/// </para>
/// <para>
/// Implementations must be read-only. Every supported harness keeps its store open while running,
/// and a write from Retrace could corrupt an active session. Sources that read SQLite go through
/// the shared read-only accessor in the Core project, which enforces this.
/// </para>
/// </remarks>
public interface ISessionSource
{
    /// <summary>
    /// Stable identifier used in URIs, config keys, and CLI filters. Lowercase and hyphenated, for
    /// example <c>opencode</c>, <c>copilot-cli</c>, or <c>copilot-vscode</c>.
    /// </summary>
    string Id { get; }

    /// <summary>Human-facing name.</summary>
    string DisplayName { get; }

    /// <summary>What this source supports.</summary>
    SourceCapabilities Capabilities { get; }

    /// <summary>
    /// Checks whether the underlying store exists and can be opened. Must not throw for the
    /// ordinary "harness is not installed" case; return an unavailable result instead.
    /// </summary>
    ValueTask<SourceAvailability> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams session metadata matching a filter. Must not read message bodies.
    /// </summary>
    IAsyncEnumerable<SessionSummary> ListAsync(SessionFilter filter, CancellationToken cancellationToken);

    /// <summary>
    /// Materialises one session. Returns <see langword="null"/> when the id is unknown to this source.
    /// </summary>
    ValueTask<SessionTranscript?> GetAsync(string nativeId, TranscriptOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Describes how to reopen a session. Returns <see langword="null"/> when the source cannot
    /// resume at all, or when the id is unknown.
    /// </summary>
    ValueTask<ResumeCommand?> GetResumeCommandAsync(string nativeId, ResumeOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by sources that can report a cheap change cursor, letting the indexer skip work.
/// </summary>
/// <remarks>
/// The watermark is opaque to callers: it is stored verbatim and handed back unchanged. Sources
/// typically use a max update timestamp or a max file modification time.
/// </remarks>
public interface IIncrementalSource
{
    /// <summary>Current cursor for the whole source.</summary>
    ValueTask<string?> GetWatermarkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams sessions that changed at or after the given cursor. A <see langword="null"/> cursor
    /// means "everything", which is what a first build passes.
    /// </summary>
    IAsyncEnumerable<SessionSummary> ListChangedSinceAsync(string? watermark, CancellationToken cancellationToken);

    /// <summary>
    /// Every native id currently present, for detecting sessions deleted out from under the index.
    /// Only called by a pruning rebuild, because for large stores it is the one unavoidable full scan.
    /// </summary>
    IAsyncEnumerable<string> ListAllIdsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Implemented by sources with their own full-text index, which live search can query directly
/// instead of scanning.
/// </summary>
public interface INativeSearchSource
{
    /// <summary>Runs a query against the harness's own index.</summary>
    IAsyncEnumerable<SearchHit> SearchNativeAsync(SearchQuery query, CancellationToken cancellationToken);
}
