using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

public sealed class BrowserResponsivenessTests
{
    private static FakeBackend Backend()
    {
        var backend = new FakeBackend();
        backend.Transcripts.Add(FakeBackend.MakeTranscript("opencode", "alpha", "Alpha", "/alpha", "alpha body", "retry"));
        backend.Transcripts.Add(FakeBackend.MakeTranscript("copilot-cli", "beta", "Beta", "/beta", "beta body", "other"));
        return backend;
    }

    private static async Task<SessionBrowser> Start(FakeBackend backend, BrowserOptions? options = null)
    {
        var browser = SessionBrowser.CreateHeadless(backend, options ?? new BrowserOptions(), 140, 30);
        browser.Start();
        await browser.SettleAsync();
        return browser;
    }

    private static KeyEvent Key(char value) => new(KeyKind.Character, value);

    [Fact]
    public async Task Superseded_search_is_cancelled_and_does_not_block_the_latest_query()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SearchHandler = async (query, token) =>
        {
            if (query.Text == "old")
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    cancelled.TrySetResult();
                }
            }

            return [new SearchHit { Session = backend.Transcripts[1].Summary }];
        };

        browser.Type("old");
        browser.ForceDueQueriesForTest();
        await backend.SearchStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        browser.Press(new KeyEvent(KeyKind.Character, 'u', Ctrl: true));
        browser.Type("new");
        await cancelled.Task.WaitAsync(TestContext.Current.CancellationToken);
        await browser.SettleAsync();
        backend.SearchQueries.Should().Equal("old", "new");
        backend.MaxConcurrentSearches.Should().Be(1);
        browser.Screen.Should().Contain("Matches · 1").And.Contain("Beta");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_results_and_errors_from_an_old_filter_cannot_replace_the_current_view(bool fail)
    {
        var backend = Backend();
        using var browser = await Start(backend);
        var first = new TaskCompletionSource<IReadOnlyList<SearchHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IReadOnlyList<SearchHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SearchHandler = (query, _) =>
        {
            if (query.Filter.SourceIds.Count == 2)
            {
                return first.Task; // Simulates a backend that ignores cancellation.
            }

            secondStarted.TrySetResult();
            return second.Task;
        };

        browser.Type("retry");
        browser.ForceDueQueriesForTest();
        await backend.SearchStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('s')); // same text, different filter
        browser.ForceDueQueriesForTest();
        if (fail)
        {
            first.SetException(new IOException("obsolete error"));
        }
        else
        {
            first.SetResult([new SearchHit { Session = backend.Transcripts[1].Summary }]);
        }

        var settling = browser.SettleAsync();
        try
        {
            await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            browser.Screen.Should().Contain("Sessions · 2").And.NotContain("obsolete error").And.NotContain("Matches · 1");
        }
        finally
        {
            second.TrySetResult([new SearchHit { Session = backend.Transcripts[0].Summary }]);
            await settling;
        }

        browser.Screen.Should().Contain("Matches · 1").And.Contain("Alpha");
    }

    [Fact]
    public async Task Enter_in_a_picker_waits_for_the_current_query_instead_of_picking_a_stale_row()
    {
        var backend = Backend();
        using var browser = await Start(backend, new BrowserOptions { Pick = PickKind.Uri });
        var complete = new TaskCompletionSource<IReadOnlyList<SearchHit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SearchHandler = (_, _) => complete.Task;
        browser.Type("new query");
        browser.ForceDueQueriesForTest();
        await backend.SearchStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        browser.Press(new KeyEvent(KeyKind.Enter));
        browser.IsRunning.Should().BeTrue();
        complete.SetResult([new SearchHit { Session = backend.Transcripts[0].Summary }]);
        await browser.SettleAsync();
        browser.Result.Output.Should().Be("retrace://opencode/alpha");
        backend.SearchRequests.Should().ContainSingle("Enter should flush the pending query, not queue a duplicate");
    }

    [Fact]
    public async Task Reader_never_shows_the_previous_sessions_body_under_the_new_title()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('g'));
        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("beta body");
        browser.Press(Key('J'));
        browser.Screen.Should().Contain("Alpha").And.NotContain("beta body");
        await browser.SettleAsync();
        browser.Screen.Should().Contain("alpha body");
    }

    [Fact]
    public async Task Old_transcript_errors_do_not_overwrite_a_newer_request_for_the_same_session()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = new TaskCompletionSource<SessionTranscript>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.TranscriptHandler = (reference, options, _) =>
        {
            if (options.IncludeToolOutput)
            {
                started.TrySetResult();
                return old.Task;
            }

            return Task.FromResult(backend.Transcripts.Single(item => item.Summary.Ref == reference));
        };

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('x'));
        var settling = browser.SettleAsync();
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        browser.Press(Key('x'));
        browser.TickForTest();
        old.SetException(new IOException("obsolete transcript error"));
        await settling;
        browser.Screen.Should().Contain("beta body").And.NotContain("obsolete transcript error");
    }

    [Fact]
    public async Task Refresh_reloads_the_selected_transcript_when_its_indexed_version_changes()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        browser.Screen.Should().Contain("beta body");
        var previous = backend.Transcripts[1];
        backend.Transcripts[1] = previous with
        {
            Summary = previous.Summary with { UpdatedAt = previous.Summary.UpdatedAt.AddMinutes(1), ContentHash = "v2" },
            Turns = [new Turn { Index = 0, Role = TurnRole.User, Blocks = [new TextBlock("fresh body after refresh")] }]
        };
        browser.Press(new KeyEvent(KeyKind.Character, 'r', Ctrl: true));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("fresh body after refresh").And.NotContain("beta body");
    }
}
