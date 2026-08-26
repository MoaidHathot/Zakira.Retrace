using Microsoft.Extensions.Logging;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Index;

namespace Zakira.Retrace.Core.Services;

/// <summary>How a query should reach the data.</summary>
public enum QueryMode
{
    /// <summary>Use the index, refreshing it inline first when it is stale and auto-refresh is on.</summary>
    Indexed,

    /// <summary>Bypass the index and read the sources directly. Always current, always slower.</summary>
    Live
}

/// <summary>
/// The single entry point the CLI and the MCP server both call.
/// </summary>
/// <remarks>
/// <para>
/// Its job is to decide, per operation, whether to answer from the index or go straight to the
/// sources, and to hide that decision from callers so both front ends behave identically.
/// </para>
/// <para>
/// Retrace runs no background process. When the index is stale, the refresh happens inline inside
/// whichever command noticed, and only after a cheap watermark comparison confirms a source
/// actually moved. That keeps the tool a plain command that starts, does work, and exits.
/// </para>
/// </remarks>
public sealed class SessionCatalog(
    RetraceConfig config,
    IEnumerable<ISessionSource> allSources,
    IndexSearcher searcher,
    IndexBuilder builder,
    ILogger<SessionCatalog> logger)
{
    private readonly ISessionSource[] allSources = [.. allSources];

    /// <summary>Sources enabled in configuration.</summary>
    public IReadOnlyList<ISessionSource> Sources => [.. allSources.Where(IsEnabled)];

    /// <summary>Every source, including disabled ones, for `sources` and `doctor` output.</summary>
    public IReadOnlyList<ISessionSource> AllSources => allSources;

    /// <summary>Whether a source is enabled in configuration.</summary>
    public bool IsEnabled(ISessionSource source) => source.Id switch
    {
        "opencode" => config.Sources.OpenCode.Enabled,
        "copilot-cli" => config.Sources.CopilotCli.Enabled,
        "copilot-vscode" => config.Sources.CopilotVsCode.Enabled,
        _ => true
    };

    /// <summary>Probes every source.</summary>
    public async Task<IReadOnlyList<(ISessionSource Source, SourceAvailability Availability, bool Enabled)>> ProbeAllAsync(CancellationToken cancellationToken)
    {
        var results = new List<(ISessionSource, SourceAvailability, bool)>();

        foreach (var source in allSources)
        {
            var enabled = IsEnabled(source);
            SourceAvailability availability;

            try
            {
                availability = enabled
                    ? await source.ProbeAsync(cancellationToken).ConfigureAwait(false)
                    : SourceAvailability.Unavailable("Disabled in configuration.");
            }
            catch (Exception ex)
            {
                availability = SourceAvailability.Unavailable(ex.Message);
            }

            results.Add((source, availability, enabled));
        }

        return results;
    }

    /// <summary>
    /// Sources the last automatic refresh could not bring fully up to date, if any.
    /// </summary>
    /// <remarks>
    /// Read this after a query to tell the user the index is behind. Being visibly stale is far
    /// better than being silently slow, which is what happens when a query blocks until indexing
    /// finishes.
    /// </remarks>
    public IReadOnlyList<string> PendingSources { get; private set; } = [];

    /// <summary>Lists sessions.</summary>
    public async Task<IReadOnlyList<SessionSummary>> ListAsync(SessionFilter filter, QueryMode mode, CancellationToken cancellationToken)
    {
        if (mode == QueryMode.Indexed && searcher.Exists)
        {
            await RefreshIfStaleAsync(cancellationToken).ConfigureAwait(false);
            return await searcher.ListAsync(filter, cancellationToken).ConfigureAwait(false);
        }

        return await ListLiveAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a content search.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, QueryMode mode, CancellationToken cancellationToken)
    {
        if (mode == QueryMode.Indexed)
        {
            if (!searcher.Exists)
            {
                throw new IndexNotBuiltException(searcher.IndexPath);
            }

            await RefreshIfStaleAsync(cancellationToken).ConfigureAwait(false);
            return await searcher.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }

        return await SearchLiveAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves an identifier to one session, consulting the index first, then the sources.</summary>
    public async Task<SessionSummary> ResolveAsync(string identifier, CancellationToken cancellationToken)
    {
        if (searcher.Exists)
        {
            var resolved = await searcher.ResolveAsync(identifier, cancellationToken).ConfigureAwait(false);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        // Falling back to the sources means a session created since the last refresh is still
        // reachable by id even though it is not in the index yet.
        if (SessionRef.TryParse(identifier, out var reference) && reference is not null)
        {
            var source = Sources.FirstOrDefault(candidate => candidate.Id.Equals(reference.SourceId, StringComparison.OrdinalIgnoreCase));
            if (source is not null)
            {
                var transcript = await source.GetAsync(reference.NativeId, TranscriptOptions.Default with { ToTurn = 0 }, cancellationToken).ConfigureAwait(false);
                if (transcript is not null)
                {
                    return transcript.Summary;
                }
            }
        }

        foreach (var source in Sources)
        {
            var transcript = await TryGetAsync(source, identifier, TranscriptOptions.Default with { ToTurn = 0 }, cancellationToken).ConfigureAwait(false);
            if (transcript is not null)
            {
                return transcript.Summary;
            }
        }

        throw new SessionNotFoundException(identifier);
    }

    /// <summary>Materialises a session. Always reads the source, never the index.</summary>
    public async Task<SessionTranscript> GetTranscriptAsync(string identifier, TranscriptOptions options, CancellationToken cancellationToken)
    {
        var summary = await ResolveAsync(identifier, cancellationToken).ConfigureAwait(false);
        var source = RequireSource(summary.Ref.SourceId);

        var transcript = await source.GetAsync(summary.Ref.NativeId, options, cancellationToken).ConfigureAwait(false);
        return transcript ?? throw new SessionNotFoundException(identifier);
    }

    /// <summary>Builds the resume command for a session.</summary>
    public async Task<ResumeCommand> GetResumeCommandAsync(string identifier, ResumeOptions options, CancellationToken cancellationToken)
    {
        var summary = await ResolveAsync(identifier, cancellationToken).ConfigureAwait(false);
        var source = RequireSource(summary.Ref.SourceId);

        var command = await source.GetResumeCommandAsync(summary.Ref.NativeId, options, cancellationToken).ConfigureAwait(false);
        return command ?? throw new RetraceException($"Source '{source.Id}' cannot produce a resume command for {summary.Ref.Uri}.");
    }

    /// <summary>Lists the files a session touched.</summary>
    public async Task<IReadOnlyList<TouchedFile>> GetFilesAsync(string identifier, CancellationToken cancellationToken)
    {
        var summary = await ResolveAsync(identifier, cancellationToken).ConfigureAwait(false);

        if (searcher.Exists)
        {
            var indexed = await searcher.GetFilesAsync(summary.Ref.Uri, cancellationToken).ConfigureAwait(false);
            if (indexed.Count > 0)
            {
                return indexed;
            }
        }

        var transcript = await GetTranscriptAsync(identifier, TranscriptOptions.Default, cancellationToken).ConfigureAwait(false);
        return transcript.Files;
    }

    /// <summary>Reports index state.</summary>
    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken) => searcher.GetStatusAsync(cancellationToken);

    /// <summary>Builds or refreshes the index.</summary>
    public Task<IndexBuildResult> RefreshIndexAsync(IndexBuildOptions options, CancellationToken cancellationToken) =>
        builder.BuildAsync(options, cancellationToken);

    /// <summary>
    /// Tops the index up when a source's watermark has moved, under a strict time budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The governing rule is that a query must never become slow because indexing work is
    /// outstanding. Two things enforce that:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>Embedding is off. Inference is the dominant cost, and there is no
    ///   version of "run a neural network over every session you created today" that belongs in
    ///   front of a search. Content still becomes keyword-searchable immediately.</description></item>
    ///   <item><description>A hard deadline. Whatever completes is committed, whatever does not is
    ///   reported through <see cref="PendingSources"/>, and the query answers either way.</description></item>
    /// </list>
    /// </remarks>
    private async Task RefreshIfStaleAsync(CancellationToken cancellationToken)
    {
        PendingSources = [];

        if (!config.Index.AutoRefresh)
        {
            return;
        }

        var status = await searcher.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        // Rate-limit the staleness probe itself. Without this, a script running many queries in a
        // row would re-probe every source on each one, and probing means opening every session
        // database.
        if (status.LastRefresh is { } last
            && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(Math.Max(config.Index.AutoRefreshMinIntervalMinutes, 0)))
        {
            return;
        }

        var stale = new List<string>();

        foreach (var source in Sources)
        {
            if (source is not IIncrementalSource incremental)
            {
                continue;
            }

            try
            {
                var current = await incremental.GetWatermarkAsync(cancellationToken).ConfigureAwait(false);
                var recorded = status.Sources.FirstOrDefault(state => state.SourceId == source.Id)?.Watermark;

                if (current is not null && !string.Equals(current, recorded, StringComparison.Ordinal))
                {
                    stale.Add(source.Id);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not check freshness for source {SourceId}.", source.Id);
            }
        }

        if (stale.Count == 0)
        {
            return;
        }

        logger.LogInformation("Topping up the index for {Sources}.", string.Join(", ", stale));

        var budget = Math.Max(config.Index.AutoRefreshMaxSeconds, 0);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (budget > 0)
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(budget));
        }

        try
        {
            await builder.BuildAsync(
                new IndexBuildOptions
                {
                    SourceIds = stale,
                    Embed = config.Index.AutoRefreshEmbed
                },
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget expired, not the user. The guard matters: without it a genuine Ctrl-C
            // would be swallowed here and the command would carry on as though nothing happened.
            // Sessions indexed before the cutoff are already committed.
            PendingSources = stale;
            logger.LogInformation(
                "Index top-up hit its {Budget}s budget; {Sources} may be behind.",
                budget,
                string.Join(", ", stale));
            return;
        }

        // Completing inside the budget does not by itself mean the source is now exhaustive: a
        // scoped or partially failed build can also finish early. Re-check the watermarks rather
        // than assume.
        PendingSources = await FindStaleSourcesAsync(stale, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Re-probes the given sources and returns those whose watermark still differs.</summary>
    private async Task<IReadOnlyList<string>> FindStaleSourcesAsync(IReadOnlyList<string> candidates, CancellationToken cancellationToken)
    {
        var status = await searcher.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var pending = new List<string>();

        foreach (var id in candidates)
        {
            var source = Sources.FirstOrDefault(item => item.Id == id);
            if (source is not IIncrementalSource incremental)
            {
                continue;
            }

            try
            {
                var current = await incremental.GetWatermarkAsync(cancellationToken).ConfigureAwait(false);
                var recorded = status.Sources.FirstOrDefault(state => state.SourceId == id)?.Watermark;

                if (current is not null && !string.Equals(current, recorded, StringComparison.Ordinal))
                {
                    pending.Add(id);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not re-check freshness for source {SourceId}.", id);
            }
        }

        return pending;
    }

    private async Task<IReadOnlyList<SessionSummary>> ListLiveAsync(SessionFilter filter, CancellationToken cancellationToken)
    {
        var selected = SelectSources(filter.SourceIds);
        var results = new List<SessionSummary>();

        // Sources are independent and mostly I/O bound, so fanning out is a straight win on a
        // machine with several harnesses installed.
        var tasks = selected.Select(async source =>
        {
            var items = new List<SessionSummary>();
            try
            {
                await foreach (var summary in source.ListAsync(filter, cancellationToken).ConfigureAwait(false))
                {
                    items.Add(summary);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Source {SourceId} failed while listing; its results are omitted.", source.Id);
            }

            return items;
        });

        foreach (var batch in await Task.WhenAll(tasks).ConfigureAwait(false))
        {
            results.AddRange(batch);
        }

        var ordered = filter.Sort switch
        {
            SessionSortOrder.Created => results.OrderByDescending(item => item.CreatedAt),
            SessionSortOrder.Size => results.OrderByDescending(item => item.Stats.MessageCount ?? 0),
            _ => results.OrderByDescending(item => item.UpdatedAt)
        };

        return filter.Limit > 0 ? [.. ordered.Take(filter.Limit)] : [.. ordered];
    }

    private async Task<IReadOnlyList<SearchHit>> SearchLiveAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var selected = SelectSources(query.Filter.SourceIds);
        var hits = new List<SearchHit>();

        foreach (var source in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (source is INativeSearchSource native)
                {
                    // Reuse the harness's own index where it has one; scanning its store instead
                    // would be strictly slower and no more accurate.
                    await foreach (var hit in native.SearchNativeAsync(query, cancellationToken).ConfigureAwait(false))
                    {
                        hits.Add(hit);
                    }

                    continue;
                }

                await foreach (var hit in SearchByScanAsync(source, query, cancellationToken).ConfigureAwait(false))
                {
                    hits.Add(hit);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Source {SourceId} failed while searching; its results are omitted.", source.Id);
            }
        }

        return [.. hits.OrderByDescending(hit => hit.Score).Take(Math.Max(query.Top, 1))];
    }

    /// <summary>
    /// Substring scan for a source with no native index. Deliberately bounded: live search is the
    /// freshness escape hatch, not a replacement for the index.
    /// </summary>
    private static async IAsyncEnumerable<SearchHit> SearchByScanAsync(
        ISessionSource source,
        SearchQuery query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var terms = Search.Fts5Query.Tokenize(query.Text).ToArray();
        if (terms.Length == 0)
        {
            yield break;
        }

        var scanned = 0;
        const int maxScanned = 400;

        var filter = query.Filter with { Limit = maxScanned };

        await foreach (var summary in source.ListAsync(filter, cancellationToken).ConfigureAwait(false))
        {
            if (++scanned > maxScanned)
            {
                yield break;
            }

            SessionTranscript? transcript;
            try
            {
                transcript = await source.GetAsync(summary.Ref.NativeId, TranscriptOptions.Default, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                continue;
            }

            if (transcript is null)
            {
                continue;
            }

            var snippets = new List<SearchSnippet>();
            var matches = 0;

            foreach (var turn in transcript.Turns)
            {
                var text = turn.PlainText;
                if (text.Length == 0)
                {
                    continue;
                }

                var hitTerms = terms.Count(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (hitTerms == 0)
                {
                    continue;
                }

                matches += hitTerms;

                if (snippets.Count < query.SnippetsPerSession)
                {
                    snippets.Add(new SearchSnippet
                    {
                        Role = turn.Role,
                        TurnIndex = turn.Index,
                        Timestamp = turn.Timestamp,
                        Text = Text.TextUtilities.Preview(text, 240)
                    });
                }
            }

            if (matches > 0)
            {
                yield return new SearchHit
                {
                    Session = summary,
                    // Normalised by term count so a query with more terms is not inherently
                    // higher-scoring than a narrower one.
                    Score = (double)matches / terms.Length,
                    LexicalScore = matches,
                    Snippets = snippets
                };
            }
        }
    }

    private IReadOnlyList<ISessionSource> SelectSources(IReadOnlyList<string> sourceIds) =>
        sourceIds.Count == 0
            ? Sources
            : [.. Sources.Where(source => sourceIds.Contains(source.Id, StringComparer.OrdinalIgnoreCase))];

    private ISessionSource RequireSource(string sourceId) =>
        Sources.FirstOrDefault(source => source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
        ?? throw new SourceUnavailableException(sourceId, "the source is not registered or is disabled in configuration");

    private static async Task<SessionTranscript?> TryGetAsync(ISessionSource source, string nativeId, TranscriptOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await source.GetAsync(nativeId, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
