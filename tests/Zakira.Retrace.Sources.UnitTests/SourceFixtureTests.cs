using Microsoft.Extensions.Logging.Abstractions;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Sources.CopilotCli;
using Zakira.Retrace.Sources.CopilotVsCode;
using Zakira.Retrace.Sources.OpenCode;

namespace Zakira.Retrace.Sources.UnitTests;

/// <summary>
/// Source coverage that runs on any machine, using synthetic stores built to each harness's real
/// schema. These are the tests CI relies on; the live-store suites only add confidence on a
/// developer machine that happens to have the harnesses installed.
/// </summary>
public sealed class SourceFixtureTests
{
    // ---- OpenCode --------------------------------------------------------------------------

    [Fact]
    public async Task OpenCode_reads_sessions_messages_parts_and_metadata()
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("opencode");
        SourceFixtures.CreateOpenCodeDatabase(dataDirectory, sessions: 3, messagesPerSession: 4);

        var source = CreateOpenCode(dataDirectory);

        var availability = await source.ProbeAsync(TestContext.Current.CancellationToken);
        availability.IsAvailable.Should().BeTrue();
        availability.SessionCount.Should().Be(3);
        availability.Details.Should().Contain("store: sqlite");

        var sessions = await CollectAsync(source.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken));
        sessions.Should().HaveCount(3);
        sessions.Should().BeInDescendingOrder(session => session.UpdatedAt);

        var newest = sessions[0];
        newest.Ref.SourceId.Should().Be("opencode");
        newest.Agent.Should().Be("build");
        // The model column holds a JSON object; the reader must extract the id, not the raw blob.
        newest.Models.Should().ContainSingle().Which.Should().Be("claude-opus-5");
        newest.Stats.MessageCount.Should().Be(4);
        newest.Stats.TotalTokens.Should().Be(1500);
        newest.Stats.Cost.Should().Be(1.25);
        newest.Stats.FilesChanged.Should().Be(2);
        newest.Workspace!.Path.Should().Be("P:/Github/Fixture");
    }

    [Fact]
    public async Task OpenCode_transcript_parses_text_and_tool_parts_and_skips_step_markers()
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("opencode");
        SourceFixtures.CreateOpenCodeDatabase(dataDirectory, sessions: 1, messagesPerSession: 4);

        var source = CreateOpenCode(dataDirectory);

        var transcript = await source.GetAsync(
            "ses_fixture0000",
            TranscriptOptions.Full,
            TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();
        transcript!.Turns.Should().HaveCount(4);
        transcript.Turns[0].Role.Should().Be(TurnRole.User);
        transcript.Turns[1].Role.Should().Be(TurnRole.Assistant);

        var toolCalls = transcript.Turns.SelectMany(turn => turn.Blocks).OfType<ToolCallBlock>().ToArray();
        toolCalls.Should().HaveCount(2, "each assistant message in the fixture carries one tool part");
        toolCalls[0].ToolName.Should().Be("bash");
        toolCalls[0].Status.Should().Be("completed");
        toolCalls[0].Output.Should().Be("Build succeeded.");
        toolCalls[0].Duration.Should().Be(TimeSpan.FromMilliseconds(500));

        // step-finish parts carry no content and are filtered out in SQL, never surfacing as blocks.
        transcript.Turns
            .SelectMany(turn => turn.Blocks)
            .OfType<TextBlock>()
            .Should().NotContain(text => text.Text.Contains("step-finish", StringComparison.Ordinal));

        transcript.Summary.Stats.ToolCallCount.Should().Be(2);
    }

    [Fact]
    public async Task OpenCode_excludes_tool_output_unless_requested()
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("opencode");
        SourceFixtures.CreateOpenCodeDatabase(dataDirectory, sessions: 1, messagesPerSession: 2);

        var source = CreateOpenCode(dataDirectory);

        var withoutOutput = await source.GetAsync("ses_fixture0000", TranscriptOptions.Default, TestContext.Current.CancellationToken);
        var tool = withoutOutput!.Turns.SelectMany(turn => turn.Blocks).OfType<ToolCallBlock>().Single();

        tool.ToolName.Should().Be("bash", "the call itself is always visible");
        tool.Output.Should().BeNull("captured output is opt-in because it dominates transcript size");
    }

    [Fact]
    public async Task OpenCode_falls_back_to_the_legacy_json_store_when_the_database_is_absent()
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("opencode");

        // Only the JSON tree exists, which is what an older install looks like.
        var storage = Path.Combine(dataDirectory, "storage");
        Directory.CreateDirectory(Path.Combine(storage, "session", "hash1"));
        Directory.CreateDirectory(Path.Combine(storage, "project"));

        var created = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

        await File.WriteAllTextAsync(
            Path.Combine(storage, "project", "hash1.json"),
            System.Text.Json.JsonSerializer.Serialize(new { id = "hash1", worktree = "P:/Github/Legacy", vcs = "git" }),
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            Path.Combine(storage, "session", "hash1", "ses_legacy1.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                id = "ses_legacy1",
                slug = "legacy",
                projectID = "hash1",
                directory = "P:/Github/Legacy",
                title = "Legacy session",
                time = new { created, updated = created }
            }),
            TestContext.Current.CancellationToken);

        var source = CreateOpenCode(dataDirectory);

        var availability = await source.ProbeAsync(TestContext.Current.CancellationToken);
        availability.IsAvailable.Should().BeTrue();
        availability.Details.Should().Contain("store: json");

        var sessions = await CollectAsync(source.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken));
        sessions.Should().ContainSingle();
        sessions[0].Title.Should().Be("Legacy session");
    }

    [Fact]
    public async Task OpenCode_is_unavailable_when_nothing_is_installed()
    {
        using var temp = SourceFixtures.NewDirectory();
        var source = CreateOpenCode(temp.Combine("missing"));

        var availability = await source.ProbeAsync(TestContext.Current.CancellationToken);

        availability.IsAvailable.Should().BeFalse();
        availability.Reason.Should().Contain("not found");
    }

    // ---- Copilot CLI -----------------------------------------------------------------------

    [Fact]
    public async Task CopilotCli_reads_sessions_and_hides_turnless_shells()
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("copilot");
        SourceFixtures.CreateCopilotCliDatabase(dataDirectory, sessions: 3, turnsPerSession: 2, emptySessions: 4);

        var source = CreateCopilotCli(dataDirectory);

        var availability = await source.ProbeAsync(TestContext.Current.CancellationToken);
        availability.IsAvailable.Should().BeTrue();
        availability.SessionCount.Should().Be(3, "the four turn-less shells are filtered out by minTurns");
        availability.HarnessVersion.Should().Be("schema 6");

        var sessions = await CollectAsync(source.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken));
        sessions.Should().HaveCount(3);

        var session = sessions[0];
        session.Workspace!.Repository.Should().Be("Owner/Fixture");
        session.Workspace.Branch.Should().Be("main");
        // Model and cost live in assistant_usage_events, a different table from sessions.
        session.Models.Should().ContainSingle().Which.Should().Be("claude-opus-4.8");
        session.Stats.TotalTokens.Should().Be(300);
        session.Stats.Cost.Should().Be(4.0);
        // The `summary` column is the raw opening prompt, so the title has to be derived from it.
        session.Title.Should().NotBe(session.Preview);
        session.Title.Length.Should().BeLessThanOrEqualTo(90);
    }

    [Fact]
    public async Task CopilotCli_expands_each_turn_row_into_a_user_and_an_assistant_turn()
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("copilot");
        SourceFixtures.CreateCopilotCliDatabase(dataDirectory, sessions: 1, turnsPerSession: 3, emptySessions: 0);

        var source = CreateCopilotCli(dataDirectory);

        var transcript = await source.GetAsync(
            "00000000-0000-0000-0000-000000000000",
            TranscriptOptions.Default,
            TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();
        // Copilot CLI stores an exchange per row; the shared model wants alternating turns.
        transcript!.Turns.Should().HaveCount(6);
        transcript.Turns.Select(turn => turn.Role).Should().ContainInOrder(
            TurnRole.User, TurnRole.Assistant, TurnRole.User, TurnRole.Assistant, TurnRole.User, TurnRole.Assistant);
        transcript.Turns[1].Model.Should().Be("claude-opus-4.8");
        transcript.TotalTurns.Should().Be(3, "TotalTurns counts source exchanges, not expanded turns");
        transcript.Files.Should().ContainSingle();
    }

    [Fact]
    public async Task CopilotCli_native_search_uses_the_harness_fts_index()
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("copilot");
        SourceFixtures.CreateCopilotCliDatabase(dataDirectory, sessions: 3, turnsPerSession: 2, emptySessions: 0);

        var source = CreateCopilotCli(dataDirectory);

        var hits = new List<SearchHit>();
        await foreach (var hit in source.SearchNativeAsync(
            new SearchQuery { Text = "exponential backoff", Top = 5, SnippetsPerSession = 2 },
            TestContext.Current.CancellationToken))
        {
            hits.Add(hit);
        }

        hits.Should().NotBeEmpty();
        hits.Should().BeInDescendingOrder(hit => hit.Score);
        hits[0].Snippets.Should().NotBeEmpty();
        hits[0].Snippets[0].Highlighted.Should().Contain("<<");
    }

    [Theory]
    [InlineData("\"unbalanced")]
    [InlineData("NEAR(")]
    [InlineData("a AND")]
    [InlineData("-")]
    [InlineData("*")]
    [InlineData("( ) \"")]
    [InlineData("^^^ OR")]
    public async Task CopilotCli_native_search_never_throws_on_fts5_metacharacters(string hostile)
    {
        using var temp = SourceFixtures.NewDirectory();
        var dataDirectory = temp.Combine("copilot");
        SourceFixtures.CreateCopilotCliDatabase(dataDirectory, sessions: 1, turnsPerSession: 1, emptySessions: 0);

        var source = CreateCopilotCli(dataDirectory);

        var count = 0;
        await foreach (var _ in source.SearchNativeAsync(
            new SearchQuery { Text = hostile, Top = 3 },
            TestContext.Current.CancellationToken))
        {
            count++;
        }

        count.Should().BeGreaterThanOrEqualTo(0, "user input is quoted before it reaches FTS5");
    }

    // ---- Copilot in VS Code ----------------------------------------------------------------

    [Fact]
    public async Task VsCode_reads_the_whole_document_format()
    {
        using var temp = SourceFixtures.NewDirectory();
        var workspaceFolder = temp.Combine("Repo");
        Directory.CreateDirectory(workspaceFolder);
        var (_, jsonSessionId, _) = SourceFixtures.CreateVsCodeWorkspaceStorage(temp.Path, workspaceFolder);

        var source = CreateVsCode(temp.Path);

        var transcript = await source.GetAsync(jsonSessionId, TranscriptOptions.Full, TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();
        transcript!.Turns.Should().HaveCount(2);
        transcript.Turns[0].Role.Should().Be(TurnRole.User);
        transcript.Turns[0].PlainText.Should().Be("Where is the retry policy configured?");

        var assistant = transcript.Turns[1];
        assistant.Role.Should().Be(TurnRole.Assistant);
        // Adjacent markdown fragments are merged into one readable block rather than left as
        // dozens of streaming pieces.
        assistant.PlainText.Should().Be("The retry policy lives in `RetryOptions.cs` and uses exponential backoff.");
        assistant.Model.Should().Be("claude-opus-4.6", "the copilot/ vendor prefix is stripped");
        assistant.Agent.Should().Be("editsAgent");

        assistant.Blocks.OfType<ToolCallBlock>().Should().ContainSingle()
            .Which.Title.Should().Be("Searching codebase for \"retry policy\"");
        assistant.Blocks.OfType<ReasoningBlock>().Should().ContainSingle();

        transcript.Files.Should().Contain(file => file.Path.EndsWith("RetryOptions.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VsCode_replays_the_delta_log_format_to_recover_streamed_responses()
    {
        using var temp = SourceFixtures.NewDirectory();
        var workspaceFolder = temp.Combine("Repo");
        Directory.CreateDirectory(workspaceFolder);
        var (_, _, jsonlSessionId) = SourceFixtures.CreateVsCodeWorkspaceStorage(temp.Path, workspaceFolder);

        var source = CreateVsCode(temp.Path);

        var transcript = await source.GetAsync(jsonlSessionId, TranscriptOptions.Full, TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();

        // The snapshot on line 0 has an empty requests array. Everything below only exists if the
        // kind-1 and kind-2 deltas were genuinely applied.
        transcript!.Turns.Should().HaveCount(2);
        transcript.Turns[0].PlainText.Should().Be("Add jitter to the backoff calculation.");
        transcript.Turns[1].PlainText.Should().Be("Jitter is added by multiplying the delay by a random factor.");
        transcript.Summary.Title.Should().Be("Backoff jitter", "a kind-1 delta set customTitle after the snapshot");
    }

    [Fact]
    public async Task VsCode_resolves_the_workspace_hash_back_to_a_real_folder()
    {
        using var temp = SourceFixtures.NewDirectory();
        var workspaceFolder = temp.Combine("Repo");
        Directory.CreateDirectory(workspaceFolder);
        SourceFixtures.CreateVsCodeWorkspaceStorage(temp.Path, workspaceFolder);

        var source = CreateVsCode(temp.Path);

        var sessions = await CollectAsync(source.ListAsync(SessionFilter.All, TestContext.Current.CancellationToken));

        sessions.Should().HaveCount(2);
        // workspace.json stores a percent-encoded file URI; without decoding it the path would
        // still contain %3A and no workspace filter would ever match.
        sessions.Should().AllSatisfy(session =>
        {
            session.Workspace.Should().NotBeNull();
            session.Workspace!.Path.Should().NotBeNull();
            session.Workspace.Path!.Should().NotContain("%3A");
        });

        sessions[0].Workspace!.Path.Should().Be(workspaceFolder.TrimEnd('\\', '/'));
    }

    [Fact]
    public async Task VsCode_workspace_filter_matches_the_decoded_path()
    {
        using var temp = SourceFixtures.NewDirectory();
        var workspaceFolder = temp.Combine("Repo");
        Directory.CreateDirectory(workspaceFolder);
        SourceFixtures.CreateVsCodeWorkspaceStorage(temp.Path, workspaceFolder);

        var source = CreateVsCode(temp.Path);

        var matching = await CollectAsync(source.ListAsync(
            SessionFilter.All with { WorkspacePath = workspaceFolder },
            TestContext.Current.CancellationToken));

        var notMatching = await CollectAsync(source.ListAsync(
            SessionFilter.All with { WorkspacePath = temp.Combine("Elsewhere") },
            TestContext.Current.CancellationToken));

        matching.Should().HaveCount(2);
        notMatching.Should().BeEmpty();
    }

    [Fact]
    public async Task VsCode_resume_reports_that_it_cannot_restore_the_conversation()
    {
        using var temp = SourceFixtures.NewDirectory();
        var workspaceFolder = temp.Combine("Repo");
        Directory.CreateDirectory(workspaceFolder);
        var (_, jsonSessionId, _) = SourceFixtures.CreateVsCodeWorkspaceStorage(temp.Path, workspaceFolder);

        var source = CreateVsCode(temp.Path);

        var resume = await source.GetResumeCommandAsync(jsonSessionId, ResumeOptions.Default, TestContext.Current.CancellationToken);

        resume.Should().NotBeNull();
        resume!.RestoresConversation.Should().BeFalse();
        resume.Notes.Should().Contain("cannot reopen a specific chat session");
        resume.Arguments.Should().ContainSingle().Which.Should().Be(workspaceFolder.TrimEnd('\\', '/'));
    }

    [Fact]
    public async Task VsCode_skips_a_torn_final_line_without_losing_the_session()
    {
        using var temp = SourceFixtures.NewDirectory();
        var workspaceFolder = temp.Combine("Repo");
        Directory.CreateDirectory(workspaceFolder);
        var (workspaceStorage, _, jsonlSessionId) = SourceFixtures.CreateVsCodeWorkspaceStorage(temp.Path, workspaceFolder);

        // VS Code appends to these files live, so a reader will regularly see a half-written
        // final line. Everything before it must still be recovered.
        var path = Directory.EnumerateFiles(workspaceStorage, jsonlSessionId + ".jsonl", SearchOption.AllDirectories).Single();
        await File.AppendAllTextAsync(path, "{\"kind\":2,\"k\":[\"requ", TestContext.Current.CancellationToken);

        var source = CreateVsCode(temp.Path);

        var transcript = await source.GetAsync(jsonlSessionId, TranscriptOptions.Default, TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();
        transcript!.Turns.Should().HaveCount(2);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static OpenCodeSessionSource CreateOpenCode(string dataDirectory) =>
        new(new RetracePaths(),
            new OpenCodeSourceConfig { DataDirectory = dataDirectory },
            NullLogger<OpenCodeSessionSource>.Instance);

    private static CopilotCliSessionSource CreateCopilotCli(string dataDirectory) =>
        new(new RetracePaths(),
            new CopilotCliSourceConfig { DataDirectory = dataDirectory },
            NullLogger<CopilotCliSessionSource>.Instance);

    private static CopilotVsCodeSessionSource CreateVsCode(string userDataRoot) =>
        new(new RetracePaths(),
            // The layout treats an explicit dataDirectory as a User directory, so point it at the
            // fixture's Code/User folder.
            new CopilotVsCodeSourceConfig { DataDirectory = Path.Combine(userDataRoot, "Code", "User") },
            NullLogger<CopilotVsCodeSessionSource>.Instance);

    private static async Task<List<SessionSummary>> CollectAsync(IAsyncEnumerable<SessionSummary> source)
    {
        var results = new List<SessionSummary>();
        await foreach (var item in source)
        {
            results.Add(item);
        }

        return results;
    }
}
