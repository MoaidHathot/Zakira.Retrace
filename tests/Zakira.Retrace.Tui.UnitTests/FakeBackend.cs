using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Index;

namespace Zakira.Retrace.Tui.UnitTests;

/// <summary>An in-memory backend with a handful of sessions, so browser tests never touch a harness.</summary>
internal sealed class FakeBackend : IBrowserBackend
{
    public List<SessionTranscript> Transcripts { get; } = [];

    public List<string> SearchQueries { get; } = [];

    public List<(SessionRef Session, string[] Add, string[] Remove)> TagChanges { get; } = [];

    public Dictionary<string, List<string>> Tags { get; } = new(StringComparer.Ordinal);

    public int RefreshCalls { get; private set; }

    public bool ThrowIndexNotBuilt { get; set; }

    public TimeSpan Delay { get; set; }

    public IReadOnlyList<string> SourceIds { get; set; } = ["opencode", "copilot-cli"];

    public bool IndexExists => !ThrowIndexNotBuilt;

    public IReadOnlyList<string> PendingSources { get; set; } = [];

    public static SessionTranscript MakeTranscript(string source, string id, string title, string workspace, params string[] turns)
    {
        var updated = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(-id.Length);
        var blocks = new List<Turn>();
        for (var index = 0; index < turns.Length; index++)
        {
            blocks.Add(new Turn
            {
                Index = index,
                Role = index % 2 == 0 ? TurnRole.User : TurnRole.Assistant,
                Timestamp = updated.AddMinutes(index),
                Blocks = [new TextBlock(turns[index])]
            });
        }

        blocks.Add(new Turn
        {
            Index = turns.Length,
            Role = TurnRole.Assistant,
            Blocks = [new ToolCallBlock { ToolName = "bash", Title = "run tests", Output = "SECRET_TOOL_OUTPUT all passed" }]
        });

        return new SessionTranscript
        {
            Summary = new SessionSummary
            {
                Ref = new SessionRef(source, id),
                Title = title,
                Preview = turns.Length > 0 ? turns[0] : null,
                Workspace = new WorkspaceInfo { Path = workspace },
                Agent = "build",
                Models = ["test-model"],
                CreatedAt = updated.AddHours(-1),
                UpdatedAt = updated,
                Stats = new SessionStats { MessageCount = turns.Length + 1 }
            },
            Turns = blocks,
            TotalTurns = blocks.Count
        };
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(SessionFilter filter, CancellationToken cancellationToken)
    {
        await Task.Delay(Delay, cancellationToken);

        return [.. Transcripts
            .Select(Decorate)
            .Where(summary => filter.SourceIds.Count == 0 || filter.SourceIds.Contains(summary.Ref.SourceId, StringComparer.OrdinalIgnoreCase))
            .Where(summary => filter.WorkspacePath is null || string.Equals(summary.Workspace?.Path, filter.WorkspacePath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(summary => summary.UpdatedAt)
            .Take(filter.Limit > 0 ? filter.Limit : int.MaxValue)];
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var running = Interlocked.Increment(ref concurrentSearches);
        MaxConcurrentSearches = Math.Max(MaxConcurrentSearches, running);
        try
        {
            await Task.Delay(Delay, cancellationToken);
            SearchQueries.Add(query.Text);

            if (ThrowIndexNotBuilt)
            {
                throw new IndexNotBuiltException("/tmp/index.db");
            }

            var hits = new List<SearchHit>();
            foreach (var transcript in Transcripts)
            {
                if (query.Filter.SourceIds.Count > 0 && !query.Filter.SourceIds.Contains(transcript.Summary.Ref.SourceId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var snippets = transcript.Turns
                    .Where(turn => turn.PlainText.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                    .Select(turn => new SearchSnippet { Role = turn.Role, TurnIndex = turn.Index, Text = turn.PlainText })
                    .ToList();

                if (snippets.Count > 0 || transcript.Summary.Title.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(new SearchHit { Session = Decorate(transcript), Score = snippets.Count + 1, Snippets = snippets });
                }
            }

            return [.. hits.OrderByDescending(hit => hit.Score)];
        }
        finally
        {
            Interlocked.Decrement(ref concurrentSearches);
        }
    }

    private int concurrentSearches;

    public int MaxConcurrentSearches { get; private set; }

    public async Task<SessionTranscript> GetTranscriptAsync(SessionRef session, TranscriptOptions options, CancellationToken cancellationToken)
    {
        await Task.Delay(Delay, cancellationToken);
        var transcript = Transcripts.First(item => item.Summary.Ref == session);

        var turns = transcript.Turns
            .Select(turn => turn with
            {
                Blocks = [.. turn.Blocks.Select(block => block is ToolCallBlock tool && !options.IncludeToolOutput ? tool with { Output = null } : block)]
            })
            .ToList();

        return transcript with { Summary = Decorate(transcript), Turns = turns };
    }

    public Task<ResumeCommand> GetResumeCommandAsync(SessionRef session, ResumeOptions options, CancellationToken cancellationToken)
    {
        var transcript = Transcripts.First(item => item.Summary.Ref == session);
        var arguments = new List<string> { "--session", session.NativeId };
        if (options.Fork)
        {
            arguments.Add("--fork");
        }

        return Task.FromResult(new ResumeCommand
        {
            Executable = session.SourceId == "opencode" ? "opencode" : "copilot",
            Arguments = arguments,
            WorkingDirectory = transcript.Summary.Workspace?.Path,
            DisplayCommand = $"{(session.SourceId == "opencode" ? "opencode" : "copilot")} {string.Join(' ', arguments)}"
        });
    }

    public Task<IReadOnlyList<string>> UpdateTagsAsync(SessionRef session, IReadOnlyList<string> add, IReadOnlyList<string> remove, CancellationToken cancellationToken)
    {
        TagChanges.Add((session, [.. add], [.. remove]));
        if (!Tags.TryGetValue(session.Uri, out var current))
        {
            current = [];
            Tags[session.Uri] = current;
        }

        current.AddRange(add.Where(tag => !current.Contains(tag)));
        current.RemoveAll(remove.Contains);
        return Task.FromResult<IReadOnlyList<string>>([.. current]);
    }

    public Task<IndexBuildResult> RefreshIndexAsync(IProgress<IndexProgress> progress, CancellationToken cancellationToken)
    {
        RefreshCalls++;
        ThrowIndexNotBuilt = false;
        progress.Report(new IndexProgress("opencode", 3, 3, "done"));
        return Task.FromResult(new IndexBuildResult
        {
            Sources = [new SourceIndexResult { SourceId = "opencode", SessionsIndexed = 3 }],
            Duration = TimeSpan.FromSeconds(1.5)
        });
    }

    public int TopUpCalls { get; private set; }

    public bool TopUpChanges { get; set; }

    public int WarmUpCalls { get; private set; }

    public bool SemanticAvailable { get; set; } = true;

    public Task<bool> TopUpIndexAsync(IProgress<IndexProgress> progress, CancellationToken cancellationToken)
    {
        TopUpCalls++;
        progress.Report(new IndexProgress("opencode", 1, 1, "indexed 1 session(s)"));
        return Task.FromResult(TopUpChanges);
    }

    public Task WarmUpAsync(CancellationToken cancellationToken)
    {
        WarmUpCalls++;
        return Task.CompletedTask;
    }

    public Task<IndexInfo> GetIndexInfoAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new IndexInfo(!ThrowIndexNotBuilt, Transcripts.Count, SemanticAvailable ? "fake-model" : null, SemanticAvailable));

    private SessionSummary Decorate(SessionTranscript transcript) =>
        Tags.TryGetValue(transcript.Summary.Ref.Uri, out var tags)
            ? transcript.Summary with { Tags = [.. tags] }
            : transcript.Summary;
}
