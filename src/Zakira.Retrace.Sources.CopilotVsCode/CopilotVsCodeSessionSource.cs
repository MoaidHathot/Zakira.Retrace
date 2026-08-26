using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Sources.CopilotVsCode;

/// <summary>
/// Reads GitHub Copilot Chat sessions stored by VS Code.
/// </summary>
/// <remarks>
/// This source is read-only in a stronger sense than the others: VS Code exposes no command-line
/// switch that reopens a specific chat session, so <see cref="GetResumeCommandAsync"/> returns a
/// command that opens the right workspace and says plainly that the conversation itself must be
/// picked from the chat history panel.
/// </remarks>
public sealed class CopilotVsCodeSessionSource : ISessionSource, IIncrementalSource
{
    /// <summary>Stable source id used in URIs, config, and CLI filters.</summary>
    public const string SourceId = "copilot-vscode";

    private readonly VsCodeLayout layout;
    private readonly CopilotVsCodeSourceConfig config;
    private readonly ILogger<CopilotVsCodeSessionSource> logger;
    private readonly VsCodeWorkspaceMap workspaces = new();

    /// <summary>Creates the source.</summary>
    public CopilotVsCodeSessionSource(RetracePaths paths, CopilotVsCodeSourceConfig config, ILogger<CopilotVsCodeSessionSource> logger)
    {
        this.config = config;
        this.logger = logger;
        layout = new VsCodeLayout(paths, config);
    }

    /// <inheritdoc />
    public string Id => SourceId;

    /// <inheritdoc />
    public string DisplayName => "GitHub Copilot (VS Code)";

    /// <inheritdoc />
    public SourceCapabilities Capabilities =>
        SourceCapabilities.ToolCalls
        | SourceCapabilities.Patches
        | SourceCapabilities.Incremental
        | SourceCapabilities.FileTracking;

    /// <inheritdoc />
    public ValueTask<SourceAvailability> ProbeAsync(CancellationToken cancellationToken)
    {
        var installs = layout.AvailableInstalls;
        if (installs.Count == 0)
        {
            return ValueTask.FromResult(SourceAvailability.Unavailable(
                "No VS Code workspace storage found. Check sources.copilot-vscode.editors, or set sources.copilot-vscode.dataDirectory.",
                layout.Installs.Count > 0 ? layout.Installs[0].WorkspaceStorageDirectory : null));
        }

        var details = new List<string>();
        var sessionCount = 0;
        var workspaceCount = 0;

        foreach (var group in layout.EnumerateChatSessionDirectories().GroupBy(entry => entry.Install.Id))
        {
            var files = 0;
            var workspacesWithSessions = 0;
            foreach (var (_, _, directory) in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = CountSessionFiles(directory);
                if (count > 0)
                {
                    workspacesWithSessions++;
                    files += count;
                }
            }

            sessionCount += files;
            workspaceCount += workspacesWithSessions;

            var install = layout.AvailableInstalls.FirstOrDefault(item => item.Id == group.Key);
            details.Add($"{install?.DisplayName ?? group.Key}: {files} session(s) across {workspacesWithSessions} workspace(s)");
        }

        details.Add("resume opens the workspace only; VS Code has no per-session resume switch");

        return ValueTask.FromResult(new SourceAvailability
        {
            IsAvailable = true,
            DataPath = installs[0].WorkspaceStorageDirectory,
            SessionCount = sessionCount,
            Details = details
        });
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SessionSummary> ListAsync(SessionFilter filter, CancellationToken cancellationToken) =>
        ListCoreAsync(filter, modifiedAfter: null, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<SessionTranscript?> GetAsync(string nativeId, TranscriptOptions options, CancellationToken cancellationToken)
    {
        var located = Locate(nativeId);
        if (located is null)
        {
            return null;
        }

        var (path, workspace) = located.Value;
        var document = await VsCodeSessionFileReader.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        var summary = VsCodeTranscriptBuilder.BuildSummary(
            document,
            nativeId,
            workspace,
            new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
            includeEmpty: true);

        return summary is null ? null : VsCodeTranscriptBuilder.Build(document, summary, options);
    }

    /// <inheritdoc />
    public ValueTask<ResumeCommand?> GetResumeCommandAsync(string nativeId, ResumeOptions options, CancellationToken cancellationToken)
    {
        var located = Locate(nativeId);
        if (located is null)
        {
            return ValueTask.FromResult<ResumeCommand?>(null);
        }

        var (_, workspace) = located.Value;
        var folder = workspace.Path;

        // `code <folder>` is the closest honest approximation. Claiming anything stronger would
        // mislead: the chat session has to be selected from the history panel by hand.
        var available = layout.AvailableInstalls;
        var executable = available.Count > 0 && available[0].Id == "insiders" ? "code-insiders" : "code";
        var arguments = folder is null ? new List<string>() : [folder];

        return ValueTask.FromResult<ResumeCommand?>(new ResumeCommand
        {
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = folder,
            DisplayCommand = ResumeCommandFormatter.Format(executable, arguments),
            RestoresConversation = false,
            Notes = $"VS Code cannot reopen a specific chat session from the command line. This opens the workspace; "
                + $"pick session {nativeId} from the Copilot Chat history panel, or read it here with "
                + $"`retrace show {SourceId}/{nativeId}`."
        });
    }

    /// <inheritdoc />
    public ValueTask<string?> GetWatermarkAsync(CancellationToken cancellationToken)
    {
        var newest = DateTime.MinValue;

        foreach (var (_, _, directory) in layout.EnumerateChatSessionDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in EnumerateSessionFiles(directory))
            {
                var modified = File.GetLastWriteTimeUtc(file);
                if (modified > newest)
                {
                    newest = modified;
                }
            }
        }

        return ValueTask.FromResult(newest == DateTime.MinValue
            ? null
            : new DateTimeOffset(newest, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public IAsyncEnumerable<SessionSummary> ListChangedSinceAsync(string? watermark, CancellationToken cancellationToken) =>
        ListCoreAsync(SessionFilter.All, watermark, cancellationToken);

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListAllIdsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        foreach (var (_, _, directory) in layout.EnumerateChatSessionDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var file in EnumerateSessionFiles(directory))
            {
                yield return Path.GetFileNameWithoutExtension(file);
            }
        }
    }

    private async IAsyncEnumerable<SessionSummary> ListCoreAsync(
        SessionFilter filter,
        string? modifiedAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var cutoff = long.TryParse(modifiedAfter, out var epoch)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epoch)
            : (DateTimeOffset?)null;

        var summaries = new List<SessionSummary>();

        foreach (var (_, workspaceHash, directory) in layout.EnumerateChatSessionDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workspaceStorage = Path.GetDirectoryName(directory)!;
            var workspace = workspaces.Resolve(workspaceStorage, workspaceHash);

            // Filtering by workspace before opening any file turns a whole-tree scan into a couple
            // of directory reads, which matters because parsing these documents is the expensive part.
            if (!string.IsNullOrWhiteSpace(filter.WorkspacePath)
                && !TextUtilities.IsUnder(workspace.Path, filter.WorkspacePath))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(filter.Repository)
                && workspace.Path?.Contains(filter.Repository, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            foreach (var file in EnumerateSessionFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);

                if (cutoff is { } since && modified <= since)
                {
                    continue;
                }

                if (filter.Since is { } from && modified < from)
                {
                    continue;
                }

                if (filter.Until is { } to && modified > to)
                {
                    continue;
                }

                var document = await VsCodeSessionFileReader.LoadAsync(file, cancellationToken).ConfigureAwait(false);
                if (document is null)
                {
                    logger.LogDebug("Skipped unreadable VS Code chat session {Path}.", file);
                    continue;
                }

                var summary = VsCodeTranscriptBuilder.BuildSummary(
                    document,
                    Path.GetFileNameWithoutExtension(file),
                    workspace,
                    modified,
                    config.IncludeEmptySessions);

                if (summary is null || !Matches(summary, filter))
                {
                    continue;
                }

                summaries.Add(summary);
            }
        }

        var ordered = filter.Sort switch
        {
            SessionSortOrder.Created => summaries.OrderByDescending(item => item.CreatedAt),
            SessionSortOrder.Size => summaries.OrderByDescending(item => item.Stats.MessageCount ?? 0),
            _ => summaries.OrderByDescending(item => item.UpdatedAt)
        };

        foreach (var summary in filter.Limit > 0 ? ordered.Take(filter.Limit) : ordered)
        {
            yield return summary;
        }
    }

    private (string Path, WorkspaceInfo Workspace)? Locate(string sessionId)
    {
        foreach (var (_, workspaceHash, directory) in layout.EnumerateChatSessionDirectories())
        {
            foreach (var extension in (string[])[".json", ".jsonl"])
            {
                var candidate = Path.Combine(directory, sessionId + extension);
                if (File.Exists(candidate))
                {
                    return (candidate, workspaces.Resolve(Path.GetDirectoryName(directory)!, workspaceHash));
                }
            }
        }

        return null;
    }

    private static bool Matches(SessionSummary summary, SessionFilter filter)
    {
        if (filter.MinMessages is { } minimum && (summary.Stats.MessageCount ?? 0) < minimum)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Agent)
            && summary.Agent?.Contains(filter.Agent, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Model)
            && !summary.Models.Any(model => model.Contains(filter.Model, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return true;
    }

    private static IEnumerable<string> EnumerateSessionFiles(string directory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory);
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
            {
                yield return file;
            }
        }
    }

    private static int CountSessionFiles(string directory) => EnumerateSessionFiles(directory).Count();
}
