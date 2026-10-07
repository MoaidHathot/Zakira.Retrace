using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Cli;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Json;
using Zakira.Retrace.Core.Services;

namespace Zakira.Retrace.Mcp;

/// <summary>
/// The MCP tool surface.
/// </summary>
/// <remarks>
/// <para>
/// Two rules shape every method here.
/// </para>
/// <para>
/// First, results are token-bounded. An agent calling <c>session-get</c> on a long session must not
/// receive a megabyte of tool output; every response that could be unbounded takes an explicit
/// budget, defaults it conservatively, and reports a continuation cursor so the agent can ask for
/// more if it actually needs it.
/// </para>
/// <para>
/// Second, nothing here starts a process. <c>session-resume-command</c> returns the command as a
/// string and stops. An agent that was asked a question about a session must not be able to launch
/// an interactive harness on the user's machine as a side effect.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed class RetraceTools(SessionCatalog catalog, IndexSearcher searcher, TagStore tags)
{
    /// <summary>Searches session content across every configured source.</summary>
    [McpServerTool(Name = "sessions-search")]
    [Description("Search the content of past AI coding sessions across OpenCode, Copilot CLI, and Copilot in VS Code. Returns ranked sessions with matching excerpts. Use this to answer 'have I worked on X before' or 'which session did Y'.")]
    public async Task<string> SearchAsync(
        [Description("What to search for. Quote a phrase for an exact match.")] string query,
        [Description("Restrict to these source ids: opencode, copilot-cli, copilot-vscode. Comma-separated.")] string? sources = null,
        [Description("Only sessions whose working directory is at or below this absolute path.")] string? workspace = null,
        [Description("Only sessions whose repository or path contains this value.")] string? repository = null,
        [Description("Only sessions updated since this point. A date (2026-01-31) or a duration (7d, 12h).")] string? since = null,
        [Description("Only sessions updated before this point. Same formats as `since`.")] string? until = null,
        [Description("Maximum results. Defaults to 10.")] int top = 10,
        [Description("Excerpts per result. Defaults to 2.")] int snippets = 2,
        [Description("Retrieval mode: hybrid (default), lexical, or semantic.")] string mode = "hybrid",
        CancellationToken cancellationToken = default)
    {
        var request = new SearchQuery
        {
            Text = query,
            Filter = BuildFilter(sources, workspace, repository, since, until, limit: 0),
            Top = Math.Clamp(top, 1, 50),
            SnippetsPerSession = Math.Clamp(snippets, 0, 10),
            Mode = mode?.Trim().ToLowerInvariant() switch
            {
                "lexical" => SearchMode.Lexical,
                "semantic" => SearchMode.Semantic,
                _ => SearchMode.Hybrid
            }
        };

        var hits = await catalog.SearchAsync(request, QueryMode.Indexed, cancellationToken).ConfigureAwait(false);

        return Serialize(new
        {
            query,
            count = hits.Count,
            results = hits.Select(CliApp.ToJsonHit),
            hint = hits.Count == 0
                ? "No matches. Try fewer or broader terms, or call index-refresh if these sessions are very recent."
                : "Call session-get with a result's `uri` to read the full conversation."
        });
    }

    /// <summary>Lists sessions by metadata, without searching content.</summary>
    [McpServerTool(Name = "sessions-list")]
    [Description("List past AI coding sessions by metadata, newest first, without searching their content. Use this for 'what was I working on recently' or to enumerate every session in a given project.")]
    public async Task<string> ListAsync(
        [Description("Restrict to these source ids: opencode, copilot-cli, copilot-vscode. Comma-separated.")] string? sources = null,
        [Description("Only sessions whose working directory is at or below this absolute path.")] string? workspace = null,
        [Description("Only sessions whose repository or path contains this value.")] string? repository = null,
        [Description("Only sessions updated since this point. A date (2026-01-31) or a duration (7d, 12h).")] string? since = null,
        [Description("Only sessions updated before this point. Same formats as `since`.")] string? until = null,
        [Description("Only sessions run with this agent or mode.")] string? agent = null,
        [Description("Only sessions that used a model whose id contains this value.")] string? model = null,
        [Description("Maximum sessions. Defaults to 25.")] int limit = 25,
        CancellationToken cancellationToken = default)
    {
        var filter = BuildFilter(sources, workspace, repository, since, until, Math.Clamp(limit, 1, 200)) with
        {
            Agent = agent,
            Model = model
        };

        var sessions = await catalog.ListAsync(filter, QueryMode.Indexed, cancellationToken).ConfigureAwait(false);

        return Serialize(new
        {
            count = sessions.Count,
            sessions = sessions.Select(CliApp.ToJsonSession)
        });
    }

    /// <summary>Reads a session's conversation.</summary>
    [McpServerTool(Name = "session-get")]
    [Description("Read the conversation of one past session. Returns turns of prose by default; tool output is excluded unless asked for, because it is usually large and rarely what you need. Long sessions are truncated and report `nextTurnIndex` so you can continue.")]
    public async Task<string> GetAsync(
        [Description("Session URI (retrace://source/id), native id, or unambiguous id prefix.")] string session,
        [Description("First turn to return, inclusive.")] int? fromTurn = null,
        [Description("Last turn to return, inclusive.")] int? toTurn = null,
        [Description("Include captured tool output. Off by default; this is the bulk of a session's size.")] bool includeToolOutput = false,
        [Description("Include model reasoning blocks.")] bool includeReasoning = false,
        [Description("Approximate character budget for the whole response. Defaults to 20000.")] int maxCharacters = 20000,
        CancellationToken cancellationToken = default)
    {
        var options = new TranscriptOptions
        {
            IncludeToolOutput = includeToolOutput,
            IncludeReasoning = includeReasoning,
            IncludeDiffs = false,
            FromTurn = fromTurn,
            ToTurn = toTurn,
            MaxCharacters = Math.Clamp(maxCharacters, 1000, 200_000),
            MaxToolOutputCharacters = 1500
        };

        var transcript = await catalog.GetTranscriptAsync(session, options, cancellationToken).ConfigureAwait(false);
        var payload = CliApp.ToJsonTranscript(transcript);

        return transcript.IsTruncated
            ? Serialize(new
            {
                transcript = payload,
                hint = $"Truncated at turn {transcript.NextTurnIndex} of {transcript.TotalTurns}. "
                    + $"Call session-get again with fromTurn={transcript.NextTurnIndex} to continue."
            })
            : Serialize(new { transcript = payload });
    }

    /// <summary>Lists the files a session touched.</summary>
    [McpServerTool(Name = "session-files")]
    [Description("List the files a past session read or edited. Useful for finding which session last touched a given file.")]
    public async Task<string> FilesAsync(
        [Description("Session URI, native id, or unambiguous id prefix.")] string session,
        CancellationToken cancellationToken = default)
    {
        var summary = await catalog.ResolveAsync(session, cancellationToken).ConfigureAwait(false);
        var files = await catalog.GetFilesAsync(session, cancellationToken).ConfigureAwait(false);

        return Serialize(new
        {
            session = summary.Ref.Uri,
            title = summary.Title,
            count = files.Count,
            files = files.Select(file => new { path = file.Path, tool = file.Tool })
        });
    }

    /// <summary>Describes how to reopen a session.</summary>
    [McpServerTool(Name = "session-resume-command")]
    [Description("Return the shell command that reopens a past session in its own harness. This tool NEVER runs the command; it only describes it, so the user stays in control of launching an interactive session.")]
    public async Task<string> ResumeCommandAsync(
        [Description("Session URI, native id, or unambiguous id prefix.")] string session,
        [Description("Branch the session instead of continuing it, where the harness supports it.")] bool fork = false,
        CancellationToken cancellationToken = default)
    {
        var summary = await catalog.ResolveAsync(session, cancellationToken).ConfigureAwait(false);
        var resume = await catalog
            .GetResumeCommandAsync(session, new ResumeOptions { Fork = fork }, cancellationToken)
            .ConfigureAwait(false);

        return Serialize(new
        {
            session = summary.Ref.Uri,
            title = summary.Title,
            command = resume.DisplayCommand,
            executable = resume.Executable,
            arguments = resume.Arguments,
            workingDirectory = resume.WorkingDirectory,
            restoresConversation = resume.RestoresConversation,
            notes = resume.Notes,
            hint = "Show this command to the user. Do not execute it yourself."
        });
    }

    /// <summary>Reports which sources are available.</summary>
    [McpServerTool(Name = "sources-list")]
    [Description("List the AI harnesses Retrace can read on this machine, whether each is currently available, and how many sessions each holds.")]
    public async Task<string> SourcesAsync(CancellationToken cancellationToken = default)
    {
        var probes = await catalog.ProbeAllAsync(cancellationToken).ConfigureAwait(false);

        return Serialize(new
        {
            sources = probes.Select(probe => new
            {
                id = probe.Source.Id,
                name = probe.Source.DisplayName,
                enabled = probe.Enabled,
                indexByDefault = probe.IndexedByDefault,
                available = probe.Availability.IsAvailable,
                sessionCount = probe.Availability.SessionCount,
                reason = probe.Availability.Reason,
                capabilities = probe.Source.Capabilities.ToString()
            })
        });
    }

    /// <summary>Reports index freshness.</summary>
    [McpServerTool(Name = "index-status")]
    [Description("Report the state of the local search index: how many sessions and passages it holds, when it was last refreshed, and which embedding model it used.")]
    public async Task<string> IndexStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await searcher.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return Serialize(status);
    }

    /// <summary>Updates the index.</summary>
    [McpServerTool(Name = "index-refresh")]
    [Description("Update the local search index with sessions that changed since the last run. Search normally refreshes on its own; call this only after doing work you want to find immediately.")]
    public async Task<string> IndexRefreshAsync(
        [Description("Restrict to these source ids. Comma-separated.")] string? sources = null,
        [Description("Only index sessions updated since this point. A date (2026-01-31) or a duration (90d, 12h). Scoping does not advance the watermark, so excluded sessions are still backfilled by a later unscoped refresh.")] string? since = null,
        [Description("Re-read everything, ignoring change detection. Slow.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        var result = await catalog.RefreshIndexAsync(
            new IndexBuildOptions
            {
                SourceIds = SplitCsv(sources),
                Since = FilterOptions.ParseTimeBound(since, isLowerBound: true),
                Force = force
            },
            cancellationToken).ConfigureAwait(false);

        return Serialize(result);
    }

    /// <summary>Reads or changes a session's tags.</summary>
    [McpServerTool(Name = "session-tags")]
    [Description("Read or change the tags on a past session. Tags are stored locally by Retrace and survive an index rebuild.")]
    public async Task<string> TagsAsync(
        [Description("Session URI, native id, or unambiguous id prefix.")] string session,
        [Description("Tags to add. Comma-separated.")] string? add = null,
        [Description("Tags to remove. Comma-separated.")] string? remove = null,
        CancellationToken cancellationToken = default)
    {
        var summary = await catalog.ResolveAsync(session, cancellationToken).ConfigureAwait(false);

        var toAdd = SplitCsv(add);
        if (toAdd.Count > 0)
        {
            await tags.AddAsync(summary.Ref.Uri, toAdd, origin: "manual", cancellationToken).ConfigureAwait(false);
        }

        var toRemove = SplitCsv(remove);
        if (toRemove.Count > 0)
        {
            await tags.RemoveAsync(summary.Ref.Uri, toRemove, cancellationToken).ConfigureAwait(false);
        }

        var current = await tags.GetAsync(summary.Ref.Uri, cancellationToken).ConfigureAwait(false);

        return Serialize(new
        {
            session = summary.Ref.Uri,
            title = summary.Title,
            tags = current.Select(tag => new { tag = tag.Tag, origin = tag.Origin })
        });
    }

    private static SessionFilter BuildFilter(
        string? sources,
        string? workspace,
        string? repository,
        string? since,
        string? until,
        int limit) => new()
        {
            SourceIds = SplitCsv(sources),
            WorkspacePath = string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace),
            Repository = repository,
            Since = FilterOptions.ParseTimeBound(since, isLowerBound: true),
            Until = FilterOptions.ParseTimeBound(until, isLowerBound: false),
            Limit = limit
        };

    private static List<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string Serialize(object value) => JsonSerializer.Serialize(value, RetraceJson.Options);
}

/// <summary>
/// Read-only MCP resources exposing the catalogue.
/// </summary>
/// <remarks>
/// Resources complement the tools: a client that supports them can attach a session to its context
/// directly, without spending a tool call to fetch it.
/// </remarks>
[McpServerResourceType]
public sealed class RetraceResources(SessionCatalog catalog)
{
    /// <summary>Lists the available sources.</summary>
    [McpServerResource(UriTemplate = "retrace://sources", Name = "Sources", MimeType = "application/json")]
    [Description("The AI harnesses Retrace can read on this machine.")]
    public async Task<string> SourcesAsync(CancellationToken cancellationToken)
    {
        var probes = await catalog.ProbeAllAsync(cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(
            probes.Select(probe => new
            {
                id = probe.Source.Id,
                name = probe.Source.DisplayName,
                available = probe.Availability.IsAvailable,
                sessionCount = probe.Availability.SessionCount
            }),
            RetraceJson.Options);
    }

    /// <summary>Returns the most recently updated sessions.</summary>
    [McpServerResource(UriTemplate = "retrace://sessions/recent", Name = "Recent sessions", MimeType = "application/json")]
    [Description("The 25 most recently updated sessions across every source.")]
    public async Task<string> RecentAsync(CancellationToken cancellationToken)
    {
        var sessions = await catalog
            .ListAsync(SessionFilter.All with { Limit = 25 }, QueryMode.Indexed, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Serialize(sessions.Select(CliApp.ToJsonSession), RetraceJson.Options);
    }

    /// <summary>Returns one session's transcript.</summary>
    [McpServerResource(UriTemplate = "retrace://sessions/{sourceId}/{sessionId}", Name = "Session transcript", MimeType = "application/json")]
    [Description("The conversation of one session, prose only, bounded to 20000 characters.")]
    public async Task<string> SessionAsync(string sourceId, string sessionId, CancellationToken cancellationToken)
    {
        var transcript = await catalog
            .GetTranscriptAsync($"retrace://{sourceId}/{sessionId}", TranscriptOptions.Default with { MaxCharacters = 20000 }, cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Serialize(CliApp.ToJsonTranscript(transcript), RetraceJson.Options);
    }
}
