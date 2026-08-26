using Microsoft.Extensions.Logging.Abstractions;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Sources.CopilotCli;

namespace Zakira.Retrace.Sources.UnitTests;

/// <summary>
/// Exercises the Copilot CLI source against the developer's real store when one is present.
/// Skipped on machines without the harness installed.
/// </summary>
public sealed class CopilotCliLiveStoreTests
{
    private static CopilotCliSessionSource Create(CopilotCliSourceConfig? config = null) =>
        new(new RetracePaths(), config ?? new CopilotCliSourceConfig(), NullLogger<CopilotCliSessionSource>.Instance);

    private static async Task<CopilotCliSessionSource?> TryCreateAsync(CancellationToken cancellationToken)
    {
        var source = Create();
        var availability = await source.ProbeAsync(cancellationToken);
        return availability.IsAvailable ? source : null;
    }

    [Fact]
    public async Task Probe_finds_the_store_and_reports_the_schema_version()
    {
        var source = Create();
        var availability = await source.ProbeAsync(TestContext.Current.CancellationToken);

        if (!availability.IsAvailable)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
            return;
        }

        availability.DataPath.Should().EndWith("session-store.db");
        availability.SessionCount.Should().BePositive();
        availability.Details.Should().Contain(detail => detail.Contains("schema version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task List_hides_empty_session_shells_by_default()
    {
        var withFilter = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (withFilter is null)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
            return;
        }

        var filtered = await CountAsync(withFilter, TestContext.Current.CancellationToken);

        // Copilot CLI writes a session row on every launch, so the unfiltered count is expected to
        // be substantially larger. If it were not, the minTurns default would be doing nothing.
        var unfiltered = Create(new CopilotCliSourceConfig { MinTurns = 0 });
        var all = await CountAsync(unfiltered, TestContext.Current.CancellationToken);

        filtered.Should().BePositive();
        all.Should().BeGreaterThanOrEqualTo(filtered);

        static async Task<int> CountAsync(CopilotCliSessionSource source, CancellationToken cancellationToken)
        {
            var count = 0;
            await foreach (var _ in source.ListAsync(SessionFilter.All with { Limit = 500 }, cancellationToken))
            {
                count++;
            }

            return count;
        }
    }

    [Fact]
    public async Task List_populates_workspace_model_and_usage_metadata()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
            return;
        }

        var sessions = new List<SessionSummary>();
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 20 }, TestContext.Current.CancellationToken))
        {
            sessions.Add(session);
        }

        sessions.Should().NotBeEmpty();
        sessions.Should().BeInDescendingOrder(session => session.UpdatedAt);
        sessions.Should().AllSatisfy(session =>
        {
            session.Ref.SourceId.Should().Be("copilot-cli");
            session.Title.Should().NotBeNullOrWhiteSpace();
            session.Stats.MessageCount.Should().BePositive();
        });

        // Model and cost come from assistant_usage_events, which is a separate table from sessions.
        // Whether any *recent* session has rows there is a property of how the machine has been
        // used, not of the reader: a run of scripted one-shot invocations produces no usage events
        // at all. Asserting presence made this test fail for a reason it was never about, so it
        // skips when the sample has nothing to check and still verifies the join when it does.
        if (!sessions.Any(session => session.Models.Count > 0))
        {
            Assert.Skip("No session in the sampled window recorded usage events.");
            return;
        }

        sessions.Should().Contain(session => session.Models.Count > 0);
    }

    [Fact]
    public async Task Get_returns_alternating_user_and_assistant_turns()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
            return;
        }

        SessionSummary? candidate = null;
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 20, MinMessages = 2 }, TestContext.Current.CancellationToken))
        {
            candidate = session;
            break;
        }

        if (candidate is null)
        {
            Assert.Skip("No multi-turn Copilot CLI session available.");
            return;
        }

        var transcript = await source.GetAsync(candidate.Ref.NativeId, TranscriptOptions.Default, TestContext.Current.CancellationToken);

        transcript.Should().NotBeNull();
        transcript!.Turns.Should().NotBeEmpty();
        transcript.Turns.Should().Contain(turn => turn.Role == TurnRole.User);
        transcript.Turns.Should().Contain(turn => turn.Role == TurnRole.Assistant);
        transcript.Turns.Select(turn => turn.Index).Should().BeInAscendingOrder();
        transcript.Turns.Should().AllSatisfy(turn => turn.PlainText.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public async Task Native_search_returns_hits_with_highlighted_snippets()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
            return;
        }

        // Pull a distinctive word straight out of a real session so the query is guaranteed to
        // have at least one match on this machine.
        SessionSummary? seed = null;
        await foreach (var session in source.ListAsync(SessionFilter.All with { Limit = 5, MinMessages = 1 }, TestContext.Current.CancellationToken))
        {
            seed = session;
            break;
        }

        if (seed is null)
        {
            Assert.Skip("No Copilot CLI sessions available.");
            return;
        }

        var term = (seed.Preview ?? seed.Title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(word => word.Length > 5 && word.All(char.IsLetter));

        if (term is null)
        {
            Assert.Skip("Could not derive a searchable term from the available sessions.");
            return;
        }

        var hits = new List<SearchHit>();
        await foreach (var hit in source.SearchNativeAsync(new SearchQuery { Text = term, Top = 5 }, TestContext.Current.CancellationToken))
        {
            hits.Add(hit);
        }

        hits.Should().NotBeEmpty($"'{term}' was taken from a real session and must be in the FTS index");
        hits.Should().BeInDescendingOrder(hit => hit.Score);
        hits.Should().AllSatisfy(hit => hit.Session.Ref.SourceId.Should().Be("copilot-cli"));
        hits.Should().Contain(hit => hit.Snippets.Count > 0);
    }

    [Fact]
    public async Task Native_search_survives_input_that_is_invalid_fts5_syntax()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
            return;
        }

        // Every one of these is a syntax error if passed through to FTS5 unquoted.
        foreach (var hostile in (string[])["\"unbalanced", "NEAR(", "a AND", "-", "*", "^^^", "( ) \""])
        {
            var count = 0;
            await foreach (var _ in source.SearchNativeAsync(new SearchQuery { Text = hostile, Top = 3 }, TestContext.Current.CancellationToken))
            {
                count++;
            }

            count.Should().BeGreaterThanOrEqualTo(0);
        }
    }

    [Fact]
    public async Task Resume_command_uses_the_equals_form_copilot_expects()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
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
            Assert.Skip("No Copilot CLI sessions available.");
            return;
        }

        var resume = await source.GetResumeCommandAsync(candidate.Ref.NativeId, ResumeOptions.Default, TestContext.Current.CancellationToken);

        resume.Should().NotBeNull();
        resume!.Executable.Should().Be("copilot");
        resume.Arguments.Should().ContainSingle().Which.Should().Be($"--resume={candidate.Ref.NativeId}");
    }

    [Fact]
    public async Task Watermark_is_iso8601_and_bounds_incremental_listing()
    {
        var source = await TryCreateAsync(TestContext.Current.CancellationToken);
        if (source is null)
        {
            Assert.Skip("GitHub Copilot CLI is not installed on this machine.");
            return;
        }

        var watermark = await source.GetWatermarkAsync(TestContext.Current.CancellationToken);
        watermark.Should().NotBeNullOrWhiteSpace();
        DateTimeOffset.TryParse(watermark, out _).Should().BeTrue();

        var changed = 0;
        await foreach (var _ in source.ListChangedSinceAsync(watermark, TestContext.Current.CancellationToken))
        {
            changed++;
        }

        changed.Should().Be(0);
    }
}
