using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Json;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Sources.OpenCode;

/// <summary>
/// Reads sessions from OpenCode's legacy JSON file tree.
/// </summary>
/// <remarks>
/// <para>Layout:</para>
/// <code>
/// storage/project/&lt;projectHash&gt;.json
/// storage/session/&lt;projectHash&gt;/&lt;sessionId&gt;.json
/// storage/message/&lt;sessionId&gt;/&lt;messageId&gt;.json
/// storage/part/&lt;messageId&gt;/&lt;partId&gt;.json
/// </code>
/// <para>
/// This store is superseded by the SQLite database and is only read when the database is missing
/// or the user has explicitly opted back into it. It is materially slower: a single session
/// transcript costs one file open per part, which on a long session is thousands of syscalls.
/// </para>
/// </remarks>
internal sealed class OpenCodeJsonReader(string storageDirectory)
{
    private string SessionDirectory => Path.Combine(storageDirectory, "session");

    private string MessageDirectory => Path.Combine(storageDirectory, "message");

    private string PartDirectory => Path.Combine(storageDirectory, "part");

    private string ProjectDirectory => Path.Combine(storageDirectory, "project");

    /// <summary>Counts session files.</summary>
    public int CountSessions() =>
        Directory.Exists(SessionDirectory)
            ? Directory.EnumerateFiles(SessionDirectory, "*.json", SearchOption.AllDirectories).Count()
            : 0;

    /// <summary>Newest session file modification time, used as the incremental watermark.</summary>
    public string? GetWatermark()
    {
        if (!Directory.Exists(SessionDirectory))
        {
            return null;
        }

        var newest = Directory
            .EnumerateFiles(SessionDirectory, "*.json", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).LastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        return newest == DateTime.MinValue
            ? null
            : new DateTimeOffset(newest, TimeSpan.Zero).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Streams every session id.</summary>
    public IEnumerable<string> ListIds() =>
        Directory.Exists(SessionDirectory)
            ? Directory.EnumerateFiles(SessionDirectory, "*.json", SearchOption.AllDirectories).Select(Path.GetFileNameWithoutExtension).OfType<string>()
            : [];

    /// <summary>Streams session metadata matching a filter.</summary>
    public async IAsyncEnumerable<SessionSummary> ListAsync(
        SessionFilter filter,
        bool includeChildSessions,
        string? updatedAfter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(SessionDirectory))
        {
            yield break;
        }

        var projects = LoadProjects();
        var watermark = long.TryParse(updatedAfter, out var parsed) ? parsed : (long?)null;

        var summaries = new List<SessionSummary>();
        foreach (var file in Directory.EnumerateFiles(SessionDirectory, "*.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summary = await ReadSessionFileAsync(file, projects, cancellationToken).ConfigureAwait(false);
            if (summary is null)
            {
                continue;
            }

            if (!includeChildSessions && summary.ParentNativeId is not null)
            {
                continue;
            }

            if (!filter.IncludeArchived && summary.IsArchived)
            {
                continue;
            }

            if (watermark is { } cutoff && summary.UpdatedAt.ToUnixTimeMilliseconds() <= cutoff)
            {
                continue;
            }

            if (!Matches(summary, filter))
            {
                continue;
            }

            summaries.Add(summary with
            {
                Stats = summary.Stats with { MessageCount = CountMessages(summary.Ref.NativeId) }
            });
        }

        var ordered = filter.Sort switch
        {
            SessionSortOrder.Created => summaries.OrderByDescending(item => item.CreatedAt),
            SessionSortOrder.Size => summaries.OrderByDescending(item => item.Stats.MessageCount ?? 0),
            _ => summaries.OrderByDescending(item => item.UpdatedAt)
        };

        var results = filter.Limit > 0 ? ordered.Take(filter.Limit) : ordered;
        foreach (var summary in results)
        {
            if (filter.MinMessages is { } minimum && (summary.Stats.MessageCount ?? 0) < minimum)
            {
                continue;
            }

            yield return summary;
        }
    }

    /// <summary>Materialises one session.</summary>
    public async Task<SessionTranscript?> GetAsync(string sessionId, TranscriptOptions options, CancellationToken cancellationToken)
    {
        var file = Directory.Exists(SessionDirectory)
            ? Directory.EnumerateFiles(SessionDirectory, $"{sessionId}.json", SearchOption.AllDirectories).FirstOrDefault()
            : null;

        if (file is null)
        {
            return null;
        }

        var summary = await ReadSessionFileAsync(file, LoadProjects(), cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var messages = new List<OpenCodeMessage>();
        var partsByMessage = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        var messageDirectory = Path.Combine(MessageDirectory, sessionId);
        if (Directory.Exists(messageDirectory))
        {
            // Message ids are monotonically increasing, so lexicographic order is chronological.
            foreach (var messageFile in Directory.EnumerateFiles(messageDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var messageId = Path.GetFileNameWithoutExtension(messageFile);
                var data = await ReadAllTextSafeAsync(messageFile, cancellationToken).ConfigureAwait(false);
                if (data is null)
                {
                    continue;
                }

                messages.Add(new OpenCodeMessage(messageId, data, File.GetLastWriteTimeUtc(messageFile)));

                var partDirectory = Path.Combine(PartDirectory, messageId);
                if (!Directory.Exists(partDirectory))
                {
                    continue;
                }

                var parts = new List<string>();
                foreach (var partFile in Directory.EnumerateFiles(partDirectory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
                {
                    var partData = await ReadAllTextSafeAsync(partFile, cancellationToken).ConfigureAwait(false);
                    if (partData is not null)
                    {
                        parts.Add(partData);
                    }
                }

                partsByMessage[messageId] = parts;
            }
        }

        return OpenCodeTranscriptBuilder.Build(summary, messages, partsByMessage, options);
    }

    private int CountMessages(string sessionId)
    {
        var directory = Path.Combine(MessageDirectory, sessionId);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Count() : 0;
    }

    private Dictionary<string, (string? Worktree, string? Vcs)> LoadProjects()
    {
        var projects = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        if (!Directory.Exists(ProjectDirectory))
        {
            return projects;
        }

        foreach (var file in Directory.EnumerateFiles(ProjectDirectory, "*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                var id = root.GetStringOrNull("id") ?? Path.GetFileNameWithoutExtension(file);
                projects[id] = (root.GetStringOrNull("worktree"), root.GetStringOrNull("vcs"));
            }
            catch (Exception)
            {
                // A malformed project file only costs us the worktree label for its sessions.
            }
        }

        return projects;
    }

    private static async Task<SessionSummary?> ReadSessionFileAsync(
        string file,
        Dictionary<string, (string? Worktree, string? Vcs)> projects,
        CancellationToken cancellationToken)
    {
        var json = await ReadAllTextSafeAsync(file, cancellationToken).ConfigureAwait(false);
        using var document = RetraceJson.TryParse(json);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var id = root.GetStringOrNull("id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var created = RetraceJson.FromEpochMilliseconds(root.Path("time")?.GetInt64OrNull("created")) ?? DateTimeOffset.UnixEpoch;
        var updated = RetraceJson.FromEpochMilliseconds(root.Path("time")?.GetInt64OrNull("updated")) ?? created;
        var projectId = root.GetStringOrNull("projectID") ?? root.GetStringOrNull("projectId");
        var project = projectId is not null && projects.TryGetValue(projectId, out var found) ? found : default;

        return new SessionSummary
        {
            Ref = new SessionRef(OpenCodeSessionSource.SourceId, id),
            Title = root.GetStringOrNull("title") ?? root.GetStringOrNull("slug") ?? "(untitled session)",
            Workspace = new WorkspaceInfo
            {
                Path = root.GetStringOrNull("directory") ?? project.Worktree,
                Repository = project.Worktree
            },
            CreatedAt = created,
            UpdatedAt = updated,
            Stats = new SessionStats
            {
                Additions = (int?)root.Path("summary")?.GetInt64OrNull("additions"),
                Deletions = (int?)root.Path("summary")?.GetInt64OrNull("deletions"),
                FilesChanged = (int?)root.Path("summary")?.GetInt64OrNull("files")
            },
            ContentHash = updated.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            IsArchived = root.Path("time")?.GetInt64OrNull("archived") is > 0,
            ParentNativeId = root.GetStringOrNull("parentID")
        };
    }

    private static bool Matches(SessionSummary summary, SessionFilter filter)
    {
        if (filter.Since is { } since && summary.UpdatedAt < since)
        {
            return false;
        }

        if (filter.Until is { } until && summary.UpdatedAt > until)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.WorkspacePath)
            && !TextUtilities.IsUnder(summary.Workspace?.Path, filter.WorkspacePath))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(filter.Repository)
            && summary.Workspace?.Repository?.Contains(filter.Repository, StringComparison.OrdinalIgnoreCase) != true)
        {
            return false;
        }

        return true;
    }

    private static async Task<string?> ReadAllTextSafeAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // FileShare.ReadWrite because OpenCode may be writing this very file right now.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
