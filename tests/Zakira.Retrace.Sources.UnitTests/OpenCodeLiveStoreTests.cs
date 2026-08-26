using Microsoft.Extensions.Logging.Abstractions;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Sources.OpenCode;

namespace Zakira.Retrace.Sources.UnitTests;

/// <summary>
/// Exercises the OpenCode source against the developer's real store when one is present.
/// </summary>
/// <remarks>
/// These are skipped on any machine without OpenCode installed, including CI. They exist because
/// the shape of a real multi-gigabyte store is the thing most likely to break: schema drift,
/// unexpected part payloads, and sessions large enough that a naive query stalls. Fixture-based
/// coverage of the same code paths lives in <see cref="OpenCodeFixtureTests"/> and does run everywhere.
/// </remarks>
public sealed class OpenCodeLiveStoreTests
{
    private static OpenCodeSessionSource? TryCreate()
    {
        var paths = new RetracePaths();
        var source = new OpenCodeSessionSource(paths, new OpenCodeSourceConfig(), NullLogger<OpenCodeSessionSource>.Instance);
        var availability = source.ProbeAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return availability.IsAvailable ? source : null;
    }

    [Fact]
    public async Task Probe_reports_the_sqlite_store_when_one_exists()
    {
        var paths = new RetracePaths();
        var source = new OpenCodeSessionSource(paths, new OpenCodeSourceConfig(), NullLogger<OpenCodeSessionSource>.Instance);

        var availability = await source.ProbeAsync(TestContext.Current.CancellationToken);

        if (!availability.IsAvailable)
        {
            Assert.Skip("OpenCode is not installed on this machine.");
            return;
        }

        availability.SessionCount.Should().BePositive();
        availability.DataPath.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task List_returns_recent_sessions_with_populated_metadata()
    {
        var source = TryCreate();
        if (source is null)
        {
            Assert.Skip("OpenCode is not installed on this machine.");
            return;
        }

        var sessions = new List<SessionSummary>();
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 10 }, TestContext.Current.CancellationToken))
        {
            sessions.Add(session);
        }

        sessions.Should().NotBeEmpty();
        sessions.Should().BeInDescendingOrder(session => session.UpdatedAt);

        foreach (var session in sessions)
        {
            session.Ref.SourceId.Should().Be("opencode");
            session.Ref.NativeId.Should().StartWith("ses_");
            session.Title.Should().NotBeNullOrWhiteSpace();
            session.ContentHash.Should().NotBeNullOrWhiteSpace();
            session.Stats.MessageCount.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task Get_materialises_a_transcript_with_turns_and_content()
    {
        var source = TryCreate();
        if (source is null)
        {
            Assert.Skip("OpenCode is not installed on this machine.");
            return;
        }

        SessionSummary? candidate = null;
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 25, MinMessages = 6 }, TestContext.Current.CancellationToken))
        {
            candidate = session;
            break;
        }

        if (candidate is null)
        {
            Assert.Skip("No OpenCode session with enough messages to exercise the transcript path.");
            return;
        }

        var transcript = await source.GetAsync(candidate.Ref.NativeId, TranscriptOptions.Default, TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();
        transcript!.Turns.Should().NotBeEmpty();
        transcript.Turns.Should().Contain(turn => turn.Blocks.Count > 0);
        transcript.Turns.Select(turn => turn.Index).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Get_honours_the_character_budget_and_reports_a_continuation_point()
    {
        var source = TryCreate();
        if (source is null)
        {
            Assert.Skip("OpenCode is not installed on this machine.");
            return;
        }

        SessionSummary? candidate = null;
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 25, MinMessages = 20 }, TestContext.Current.CancellationToken))
        {
            candidate = session;
            break;
        }

        if (candidate is null)
        {
            Assert.Skip("No OpenCode session long enough to exercise truncation.");
            return;
        }

        var options = TranscriptOptions.Default with { MaxCharacters = 500 };
        var transcript = await source.GetAsync(candidate.Ref.NativeId, options, TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();
        transcript!.IsTruncated.Should().BeTrue();
        transcript.NextTurnIndex.Should().NotBeNull();
        transcript.TotalTurns.Should().BeGreaterThan(transcript.Turns.Count);
    }

    [Fact]
    public async Task Resume_command_targets_the_session_and_its_original_directory()
    {
        var source = TryCreate();
        if (source is null)
        {
            Assert.Skip("OpenCode is not installed on this machine.");
            return;
        }

        SessionSummary? candidate = null;
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 1 }, TestContext.Current.CancellationToken))
        {
            candidate = session;
            break;
        }

        if (candidate is null)
        {
            Assert.Skip("No OpenCode sessions available.");
            return;
        }

        var resume = await source.GetResumeCommandAsync(candidate.Ref.NativeId, ResumeOptions.Default, TestContext.Current.CancellationToken);

        resume.Should().NotBeNull();
        resume!.Executable.Should().Be("opencode");
        resume.Arguments.Should().ContainInOrder("--session", candidate.Ref.NativeId);
        resume.DisplayCommand.Should().Contain(candidate.Ref.NativeId);
        resume.RestoresConversation.Should().BeTrue();
    }

    [Fact]
    public async Task Watermark_is_a_parseable_epoch_and_bounds_incremental_listing()
    {
        var source = TryCreate();
        if (source is null)
        {
            Assert.Skip("OpenCode is not installed on this machine.");
            return;
        }

        var watermark = await source.GetWatermarkAsync(TestContext.Current.CancellationToken);
        watermark.Should().NotBeNullOrWhiteSpace();
        long.TryParse(watermark, out var epoch).Should().BeTrue();
        epoch.Should().BePositive();

        // Nothing can have changed after the newest known update, so an incremental pass at the
        // current watermark must be empty. This is the invariant the indexer relies on to skip work.
        var changed = 0;
        await foreach (var _ in source.ListChangedSinceAsync(watermark, TestContext.Current.CancellationToken))
        {
            changed++;
        }

        changed.Should().Be(0);
    }
}
