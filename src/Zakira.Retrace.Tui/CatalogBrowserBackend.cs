using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Services;

namespace Zakira.Retrace.Tui;

/// <summary>The real backend: <see cref="SessionCatalog"/>, <see cref="TagStore"/>, and <see cref="IndexSearcher"/> behind <see cref="IBrowserBackend"/>.</summary>
public sealed class CatalogBrowserBackend(SessionCatalog catalog, TagStore tags, IndexSearcher searcher, RetraceConfig config) : IBrowserBackend
{
    /// <inheritdoc />
    public IReadOnlyList<string> SourceIds => [.. catalog.Sources.Select(source => source.Id)];

    /// <inheritdoc />
    public bool IndexExists => searcher.Exists;

    /// <inheritdoc />
    public IReadOnlyList<string> PendingSources => catalog.PendingSources;

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(SessionFilter filter, CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.ListAsync(filter, QueryMode.Indexed, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexNotBuiltException)
        {
            return await catalog.ListAsync(filter, QueryMode.Live, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken) =>
        catalog.SearchAsync(query, QueryMode.Indexed, cancellationToken);

    /// <inheritdoc />
    public Task<SessionTranscript> GetTranscriptAsync(SessionRef session, TranscriptOptions options, CancellationToken cancellationToken) =>
        catalog.GetTranscriptAsync(session.Uri, options, cancellationToken);

    /// <inheritdoc />
    public Task<ResumeCommand> GetResumeCommandAsync(SessionRef session, ResumeOptions options, CancellationToken cancellationToken) =>
        catalog.GetResumeCommandAsync(session.Uri, options, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> UpdateTagsAsync(SessionRef session, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken cancellationToken)
    {
        if (add.Count > 0)
        {
            await tags.AddAsync(session.Uri, add, origin: "manual", cancellationToken).ConfigureAwait(false);
        }

        if (remove.Count > 0)
        {
            await tags.RemoveAsync(session.Uri, remove, cancellationToken).ConfigureAwait(false);
        }

        var current = await tags.GetAsync(session.Uri, cancellationToken).ConfigureAwait(false);
        return [.. current.Select(tag => tag.Tag)];
    }

    /// <inheritdoc />
    public Task<IndexBuildResult> RefreshIndexAsync(IProgress<IndexProgress> progress, CancellationToken cancellationToken) =>
        catalog.RefreshIndexAsync(
            new IndexBuildOptions
            {
                // Keyword-only, like the automatic top-up: the browser is interactive, and
                // inference belongs in an explicit `retrace index refresh`.
                Embed = config.Index.AutoRefreshEmbed,
                Progress = progress
            },
            cancellationToken);
}
