using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Sources.CopilotCli;

/// <summary>
/// Reads sessions recorded by the GitHub Copilot CLI.
/// </summary>
public sealed class CopilotCliSessionSource : ISessionSource, IIncrementalSource, INativeSearchSource
{
    /// <summary>Stable source id used in URIs, config, and CLI filters.</summary>
    public const string SourceId = "copilot-cli";

    private readonly CopilotCliLayout layout;
    private readonly CopilotCliSourceConfig config;
    private readonly ILogger<CopilotCliSessionSource> logger;
    private readonly CopilotCliReader reader;

    /// <summary>Creates the source.</summary>
    public CopilotCliSessionSource(RetracePaths paths, CopilotCliSourceConfig config, ILogger<CopilotCliSessionSource> logger)
    {
        this.config = config;
        this.logger = logger;
        layout = new CopilotCliLayout(paths, config);
        reader = new CopilotCliReader(layout.DatabasePath, paths.SnapshotDirectory);
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "GitHub Copilot CLI";

    /// <inheritdoc />
    public SourceCapabilities Capabilities =>
        SourceCapabilities.Resume
        | SourceCapabilities.ToolCalls
        | SourceCapabilities.Incremental
        | SourceCapabilities.NativeSearch
        | SourceCapabilities.Usage
        | SourceCapabilities.FileTracking;

    /// <inheritdoc />
    public async ValueTask<SourceAvailability> ProbeAsync(CancellationToken cancellationToken)
    {
        if (!layout.HasDatabase)
        {
            return SourceAvailability.Unavailable(
                "Copilot CLI session store not found. Install the GitHub Copilot CLI, or set sources.copilot-cli.dataDirectory.",
                layout.DatabasePath);
        }

        try
        {
            var count = await reader.CountSessionsAsync(config.MinTurns, cancellationToken).ConfigureAwait(false);
            var schemaVersion = await reader.GetSchemaVersionAsync(cancellationToken).ConfigureAwait(false);
            var hasIndex = await reader.HasSearchIndexAsync(cancellationToken).ConfigureAwait(false);

            var details = new List<string>();
            if (schemaVersion is not null)
            {
                details.Add($"schema version: {schemaVersion}");
            }

            details.Add(hasIndex && config.UseNativeSearchIndex
                ? "native FTS5 index: available"
                : "native FTS5 index: not used");

            if (config.MinTurns > 0)
            {
                details.Add($"sessions with fewer than {config.MinTurns} turn(s) are hidden");
            }

            return new SourceAvailability
            {
                IsAvailable = true,
                DataPath = layout.DatabasePath,
                SessionCount = count,
                HarnessVersion = schemaVersion is null ? null : $"schema {schemaVersion}",
                Details = details
            };
        }
        catch (Exception ex)
        {
            return SourceAvailability.Unavailable($"session-store.db could not be opened: {ex.Message}", layout.DatabasePath);
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SessionSummary> ListAsync(SessionFilter filter, CancellationToken cancellationToken) =>
        reader.ListAsync(filter, config.MinTurns, updatedAfter: null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<SessionTranscript?> GetAsync(string nativeId, TranscriptOptions options, CancellationToken cancellationToken)
    {
        var transcript = await reader.GetAsync(nativeId, options, cancellationToken).ConfigureAwait(false);
        if (transcript is null || !options.IncludeToolOutput)
        {
            return transcript;
        }

        // The database has no tool call data at all, so when the caller asked for it the only place
        // to look is the optional event log. It is usually absent, and its absence is not an error.
        var toolCalls = await CopilotCliEventLogReader
            .ReadToolCallsAsync(layout.GetEventLogPath(nativeId), options.MaxToolOutputCharacters, cancellationToken)
            .ConfigureAwait(false);

        if (toolCalls.Count == 0)
        {
            return transcript;
        }

        var turns = transcript.Turns.ToList();
        turns.Add(new Turn
        {
            Index = turns.Count,
            Role = TurnRole.Tool,
            Timestamp = turns.Count > 0 ? turns[^1].Timestamp : null,
            Blocks = [.. toolCalls]
        });

        return transcript with
        {
            Turns = turns,
            Summary = transcript.Summary with
            {
                Stats = transcript.Summary.Stats with { ToolCallCount = toolCalls.Count }
            }
        };
    }

    /// <inheritdoc />
    public async ValueTask<ResumeCommand?> GetResumeCommandAsync(string nativeId, ResumeOptions options, CancellationToken cancellationToken)
    {
        var summary = await reader.GetSummaryAsync(nativeId, cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        // Copilot CLI takes the session id as a single --resume=<id> token rather than as a
        // separate argument, and has no fork equivalent.
        var arguments = new List<string> { $"--resume={nativeId}" };

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

        return new ResumeCommand
        {
            Executable = "copilot",
            Arguments = arguments,
            WorkingDirectory = summary.Workspace?.Path,
            DisplayCommand = ResumeCommandFormatter.Format("copilot", arguments),
            RestoresConversation = true,
            Notes = options.Fork
                ? "Copilot CLI cannot fork a session; this resumes it in place."
                : null
        };
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await reader.GetWatermarkAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the Copilot CLI watermark.");
            return null;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SessionSummary> ListChangedSinceAsync(string? watermark, CancellationToken cancellationToken) =>
        reader.ListAsync(SessionFilter.All, config.MinTurns, watermark, cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListAllIdsAsync(CancellationToken cancellationToken) =>
        reader.ListIdsAsync(cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchHit> SearchNativeAsync(SearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!config.UseNativeSearchIndex || !layout.HasDatabase)
        {
            yield break;
        }

        // Over-fetch, because several hits routinely land in the same session and collapse.
        var candidateLimit = Math.Max(query.Top * 5, 50);

        var bySession = new Dictionary<string, List<SearchSnippet>>(StringComparer.Ordinal);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        await foreach (var match in reader.SearchAsync(query.Text, candidateLimit, cancellationToken).ConfigureAwait(false))
        {
            if (!bySession.TryGetValue(match.SessionId, out var snippets))
            {
                snippets = [];
                bySession[match.SessionId] = snippets;
            }

            if (snippets.Count < query.SnippetsPerSession)
            {
                snippets.Add(new SearchSnippet
                {
                    Role = match.SourceType == "turn" ? TurnRole.Assistant : TurnRole.Info,
                    TurnIndex = match.TurnIndex ?? 0,
                    Text = match.Excerpt.Replace("<<", string.Empty, StringComparison.Ordinal).Replace(">>", string.Empty, StringComparison.Ordinal),
                    Highlighted = match.Excerpt
                });
            }

            // A session's score is its best-scoring passage, not a sum: a long session should not
            // outrank a precise one simply by containing more text.
            scores[match.SessionId] = Math.Max(scores.GetValueOrDefault(match.SessionId), match.Score);
        }

        foreach (var (sessionId, score) in scores.OrderByDescending(entry => entry.Value).Take(query.Top))
        {
            var summary = await reader.GetSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (summary is null)
            {
                continue;
            }

            yield return new SearchHit
            {
                Session = summary,
                Score = score,
                LexicalScore = score,
                Snippets = bySession.GetValueOrDefault(sessionId) ?? []
            };
        }
    }
}
