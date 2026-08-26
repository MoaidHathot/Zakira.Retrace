using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Sources.OpenCode;

/// <summary>
/// Reads sessions recorded by OpenCode.
/// </summary>
public sealed class OpenCodeSessionSource : ISessionSource, IIncrementalSource
{
    /// <summary>Stable source id used in URIs, config, and CLI filters.</summary>
    public const string SourceId = "opencode";

    private readonly OpenCodeLayout layout;
    private readonly OpenCodeSourceConfig config;
    private readonly ILogger<OpenCodeSessionSource> logger;
    private readonly OpenCodeSqliteReader sqliteReader;
    private readonly OpenCodeJsonReader jsonReader;

    /// <summary>Creates the source.</summary>
    public OpenCodeSessionSource(RetracePaths paths, OpenCodeSourceConfig config, ILogger<OpenCodeSessionSource> logger)
    {
        this.config = config;
        this.logger = logger;
        layout = new OpenCodeLayout(paths, config);
        sqliteReader = new OpenCodeSqliteReader(layout.DatabasePath, paths.SnapshotDirectory);
        jsonReader = new OpenCodeJsonReader(layout.StorageDirectory);
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "OpenCode";

    /// <inheritdoc />
    public SourceCapabilities Capabilities =>
        SourceCapabilities.Resume
        | SourceCapabilities.Fork
        | SourceCapabilities.ToolCalls
        | SourceCapabilities.Patches
        | SourceCapabilities.Incremental
        | SourceCapabilities.Usage;

    /// <inheritdoc />
    public async ValueTask<SourceAvailability> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(layout.DataDirectory))
        {
            return SourceAvailability.Unavailable(
                "OpenCode data directory not found. Install OpenCode, or set sources.opencode.dataDirectory.",
                layout.DataDirectory);
        }

        switch (layout.StoreKind)
        {
            case OpenCodeStoreKind.Sqlite:
                try
                {
                    var count = await sqliteReader.CountSessionsAsync(config.IncludeChildSessions, cancellationToken).ConfigureAwait(false);
                    var version = await sqliteReader.GetHarnessVersionAsync(cancellationToken).ConfigureAwait(false);
                    return new SourceAvailability
                    {
                        IsAvailable = true,
                        DataPath = layout.DatabasePath,
                        SessionCount = count,
                        HarnessVersion = version,
                        Details = ["store: sqlite"]
                    };
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "OpenCode SQLite store could not be read; falling back to the JSON tree.");
                    if (layout.HasJsonStore)
                    {
                        return SourceAvailability.Available(layout.StorageDirectory, jsonReader.CountSessions(), "store: json (sqlite unreadable)");
                    }

                    return SourceAvailability.Unavailable($"opencode.db could not be opened: {ex.Message}", layout.DatabasePath);
                }

            case OpenCodeStoreKind.Json:
                return SourceAvailability.Available(layout.StorageDirectory, jsonReader.CountSessions(), "store: json");

            default:
                return SourceAvailability.Unavailable(
                    "No OpenCode session store found (expected opencode.db or storage/session).",
                    layout.DataDirectory);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SessionSummary> ListAsync(SessionFilter filter, CancellationToken cancellationToken) =>
        ListCoreAsync(filter, updatedAfter: null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<SessionTranscript?> GetAsync(string nativeId, TranscriptOptions options, CancellationToken cancellationToken)
    {
        if (layout.StoreKind == OpenCodeStoreKind.Sqlite)
        {
            try
            {
                return await sqliteReader.GetAsync(nativeId, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (layout.HasJsonStore)
            {
                logger.LogDebug(ex, "Falling back to the OpenCode JSON store for session {SessionId}.", nativeId);
            }
        }

        return layout.HasJsonStore
            ? await jsonReader.GetAsync(nativeId, options, cancellationToken).ConfigureAwait(false)
            : null;
    }

    /// <inheritdoc />
    public async ValueTask<ResumeCommand?> GetResumeCommandAsync(string nativeId, ResumeOptions options, CancellationToken cancellationToken)
    {
        var transcript = await GetAsync(nativeId, TranscriptOptions.Default with { ToTurn = 0 }, cancellationToken).ConfigureAwait(false);
        if (transcript is null)
        {
            return null;
        }

        var arguments = new List<string> { "--session", nativeId };

        if (options.Fork)
        {
            arguments.Add("--fork");
        }

        if (!string.IsNullOrWhiteSpace(options.Agent))
        {
            arguments.Add("--agent");
            arguments.Add(options.Agent);
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.Add("--model");
            arguments.Add(options.Model);
        }

        var workingDirectory = transcript.Summary.Workspace?.Path;

        return new ResumeCommand
        {
            Executable = "opencode",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            DisplayCommand = ResumeCommandFormatter.Format("opencode", arguments),
            RestoresConversation = true,
            Notes = options.Fork
                ? "--fork branches the session, leaving the original untouched."
                : null
        };
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        try
        {
            return layout.StoreKind == OpenCodeStoreKind.Sqlite
                ? await sqliteReader.GetWatermarkAsync(cancellationToken).ConfigureAwait(false)
                : jsonReader.GetWatermark();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the OpenCode watermark.");
            return null;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SessionSummary> ListChangedSinceAsync(string? watermark, CancellationToken cancellationToken) =>
        ListCoreAsync(
            SessionFilter.All with { IncludeArchived = true },
            watermark,
            cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListAllIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (layout.StoreKind == OpenCodeStoreKind.Sqlite)
        {
            await foreach (var id in sqliteReader.ListIdsAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return id;
            }

            yield break;
        }

        foreach (var id in jsonReader.ListIds())
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return id;
        }
    }

    private async IAsyncEnumerable<SessionSummary> ListCoreAsync(
        SessionFilter filter,
        string? updatedAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var kind = layout.StoreKind;
        if (kind == OpenCodeStoreKind.None)
        {
            yield break;
        }

        if (kind == OpenCodeStoreKind.Sqlite)
        {
            IAsyncEnumerator<SessionSummary>? enumerator = null;
            try
            {
                enumerator = sqliteReader.ListAsync(filter, config.IncludeChildSessions, updatedAfter, cancellationToken).GetAsyncEnumerator(cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Falling back to the OpenCode JSON store for listing.");
            }

            if (enumerator is not null)
            {
                await using (enumerator.ConfigureAwait(false))
                {
                    while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield return enumerator.Current;
                    }
                }

                yield break;
            }
        }

        if (!layout.HasJsonStore)
        {
            yield break;
        }

        await foreach (var summary in jsonReader.ListAsync(filter, config.IncludeChildSessions, updatedAfter, cancellationToken).ConfigureAwait(false))
        {
            yield return summary;
        }
    }
}
