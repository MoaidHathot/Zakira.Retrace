using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Index;

namespace Zakira.Retrace.Tui;

/// <summary>
/// Everything the browser needs from the rest of Retrace, behind one seam.
/// </summary>
/// <remarks>
/// The browser is a state machine driven by key presses and completed queries; nothing in it
/// should care whether results come from the real catalog or from a fixture. Keeping the surface
/// small also documents exactly what the TUI does: list, search, read, resume, tag, refresh.
/// </remarks>
public interface IBrowserBackend
{
    /// <summary>Source ids the user can cycle through as a filter, enabled ones only.</summary>
    IReadOnlyList<string> SourceIds { get; }

    /// <summary>Whether an index exists, which decides whether search is possible at all.</summary>
    bool IndexExists { get; }

    /// <summary>Sources the last automatic top-up could not bring fully current.</summary>
    IReadOnlyList<string> PendingSources { get; }

    /// <summary>Lists sessions, newest first.</summary>
    Task<IReadOnlyList<SessionSummary>> ListAsync(SessionFilter filter, CancellationToken cancellationToken);

    /// <summary>Runs a ranked search.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>Reads a transcript.</summary>
    Task<SessionTranscript> GetTranscriptAsync(SessionRef session, TranscriptOptions options, CancellationToken cancellationToken);

    /// <summary>Builds the command that reopens a session in its harness.</summary>
    Task<ResumeCommand> GetResumeCommandAsync(SessionRef session, ResumeOptions options, CancellationToken cancellationToken);

    /// <summary>Adds and removes tags on a session; returns the tags it carries afterwards.</summary>
    Task<IReadOnlyList<string>> UpdateTagsAsync(SessionRef session, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken cancellationToken);

    /// <summary>Refreshes the index for the sources indexed by default, keyword-only.</summary>
    Task<IndexBuildResult> RefreshIndexAsync(IProgress<IndexProgress> progress, CancellationToken cancellationToken);
}
