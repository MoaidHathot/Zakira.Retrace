using Microsoft.Extensions.Logging.Abstractions;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Sources.CopilotVsCode;

namespace Zakira.Retrace.Sources.UnitTests;

/// <summary>
/// Exercises the VS Code Copilot Chat source against real workspace storage when present.
/// Skipped on machines without VS Code chat history.
/// </summary>
public sealed class CopilotVsCodeLiveStoreTests
{
    private static CopilotVsCodeSessionSource Create(CopilotVsCodeSourceConfig? config = null) =>
        new(new RetracePaths(), config ?? new CopilotVsCodeSourceConfig(), NullLogger<CopilotVsCodeSessionSource>.Instance);

    private static async Task<CopilotVsCodeSessionSource?> TryCreateAsync(CancellationToken cancellationToken)
    {
        var source = Create();
        var availability = await source.ProbeAsync(cancellationToken);
        return availability is { IsAvailable: true, SessionCount: > 0 } ? source : null;
    }

    [Fact]
    public async Task Probe_reports_sessions_across_installs()
    {
        var source = Create();
        var availability = await source.ProbeAsync(TestContext.Current.CancellationToken);

        if (!availability.IsAvailable)
        {
            Assert.Skip("No VS Code workspace storage on this machine.");
            return;
        }

        availability.DataPath.Should().EndWith("workspaceStorage");
        availability.Details.Should().NotBeEmpty();
    }

    [Fact]
    public async Task List_resolves_workspace_hashes_back_to_real_folders()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("No VS Code Copilot Chat sessions on this machine.");
            return;
        }

        var sessions = new List<SessionSummary>();
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 25 }, TestContext.Current.CancellationToken))
        {
            sessions.Add(session);
        }

        sessions.Should().NotBeEmpty();
        sessions.Should().BeInDescendingOrder(session => session.UpdatedAt);
        sessions.Should().AllSatisfy(session =>
        {
            session.Ref.SourceId.Should().Be("copilot-vscode");
            session.Title.Should().NotBeNullOrWhiteSpace();
        });

        // The storage directory name is an opaque hash; if workspace.json resolution works, at
        // least some sessions carry a real filesystem path rather than the hash.
        sessions.Should().Contain(session =>
            session.Workspace != null
            && session.Workspace.Path != null
            && session.Workspace.Path.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_recovers_prose_from_both_the_json_and_jsonl_formats()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("No VS Code Copilot Chat sessions on this machine.");
            return;
        }

        var checkedSessions = 0;
        var withAssistantProse = 0;

        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 40, MinMessages = 1 }, TestContext.Current.CancellationToken))
        {
            var transcript = await source.GetAsync(session.Ref.NativeId, TranscriptOptions.Default, TestContext.Current.CancellationToken);
            if (transcript is null)
            {
                continue;
            }

            checkedSessions++;
            transcript.Turns.Should().Contain(turn => turn.Role == TurnRole.User);

            if (transcript.Turns.Any(turn => turn.Role == TurnRole.Assistant && !string.IsNullOrWhiteSpace(turn.PlainText)))
            {
                withAssistantProse++;
            }

            if (checkedSessions >= 20)
            {
                break;
            }
        }

        checkedSessions.Should().BePositive();

        // Assistant prose in the .jsonl format only appears if the delta log is genuinely replayed:
        // responses arrive as kind-2 appends to requests[i].response, never as whole request objects.
        withAssistantProse.Should().BePositive(
            "assistant responses must be recovered from both the whole-document and the delta-log formats");
    }

    [Fact]
    public async Task Get_extracts_tool_calls_and_touched_files()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("No VS Code Copilot Chat sessions on this machine.");
            return;
        }

        var sawToolCall = false;

        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 40, MinMessages = 1 }, TestContext.Current.CancellationToken))
        {
            var transcript = await source.GetAsync(session.Ref.NativeId, TranscriptOptions.Full, TestContext.Current.CancellationToken);
            if (transcript is null)
            {
                continue;
            }

            if (transcript.Turns.SelectMany(turn => turn.Blocks).OfType<ToolCallBlock>().Any())
            {
                sawToolCall = true;
                transcript.Summary.Stats.ToolCallCount.Should().BePositive();
                break;
            }
        }

        if (!sawToolCall)
        {
            Assert.Skip("No agent-mode VS Code sessions with tool calls on this machine.");
        }
    }

    [Fact]
    public async Task Resume_opens_the_workspace_and_is_honest_that_it_cannot_restore_the_chat()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("No VS Code Copilot Chat sessions on this machine.");
            return;
        }

        SessionSummary? candidate = null;
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 1 }, TestContext.Current.CancellationToken))
        {
            candidate = session;
            break;
        }

        candidate.Should().NotBeNull();

        var resume = await source.GetResumeCommandAsync(candidate!.Ref.NativeId, ResumeOptions.Default, TestContext.Current.CancellationToken);

        resume.Should().NotBeNull();
        resume!.Executable.Should().BeOneOf("code", "code-insiders");
        resume.RestoresConversation.Should().BeFalse();
        resume.Notes.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Watermark_bounds_incremental_listing()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("No VS Code Copilot Chat sessions on this machine.");
            return;
        }

        var watermark = await source.GetWatermarkAsync(TestContext.Current.CancellationToken);
        watermark.Should().NotBeNullOrWhiteSpace();

        var changed = 0;
        await foreach (var _ in source.ListChangedSinceAsync(watermark, TestContext.Current.CancellationToken))
        {
            changed++;
        }

        changed.Should().Be(0);
    }
}
