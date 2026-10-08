using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

/// <summary>
/// Drives the browser headlessly: keys in, frames out. These cover the flows the tool exists for —
/// find a session, read it, resume it — without a terminal.
/// </summary>
public sealed class SessionBrowserTests
{
    private static FakeBackend NewBackend()
    {
        var backend = new FakeBackend();
        backend.Transcripts.Add(FakeBackend.MakeTranscript("opencode", "ses_alpha", "Fix retry logic in HttpClient", @"W:\Github\Alpha", "The retries never back off", "I will add exponential backoff to the retry loop"));
        backend.Transcripts.Add(FakeBackend.MakeTranscript("copilot-cli", "beta-guid", "Investigate flaky integration test", @"W:\Github\Beta", "Why is the integration test flaky?", "The test depends on wall-clock time"));
        backend.Transcripts.Add(FakeBackend.MakeTranscript("opencode", "ses_gamma", "Write the TUI browser", @"W:\Github\Gamma", "Let us build a terminal browser", "A cell buffer makes panes easy"));
        return backend;
    }

    private static async Task<SessionBrowser> StartAsync(FakeBackend backend, BrowserOptions? options = null, int width = 140, int height = 40)
    {
        var browser = SessionBrowser.CreateHeadless(backend, options ?? new BrowserOptions(), width, height);
        browser.Start();
        await browser.SettleAsync();
        return browser;
    }

    private static KeyEvent Key(char character) => new(KeyKind.Character, character);

    private static KeyEvent Ctrl(char character) => new(KeyKind.Character, character, Ctrl: true);

    [Fact]
    public async Task Initial_frame_lists_recent_sessions_with_a_preview()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        var screen = browser.Screen;

        screen.Should().Contain("Sessions \u00b7 3");
        screen.Should().Contain("Fix retry logic in HttpClient");
        screen.Should().Contain("Investigate flaky integration test");
        screen.Should().Contain("Write the TUI browser");

        // The newest session is selected and previewed, transcript included.
        screen.Should().Contain("retrace://opencode/ses_alpha");
        screen.Should().Contain("The retries never back off");
        screen.Should().Contain("3 sessions");
    }

    [Fact]
    public async Task Typing_searches_as_you_go_and_shows_matches()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        browser.Type("flaky");
        await browser.SettleAsync();

        backend.SearchQueries.Should().Contain("flaky");
        var screen = browser.Screen;
        screen.Should().Contain("Matches \u00b7 1");
        screen.Should().Contain("Investigate flaky integration test");
        screen.Should().NotContain("Fix retry logic");
        screen.Should().Contain("1 match");
    }

    [Fact]
    public async Task Debounce_collapses_keystrokes_into_one_search()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        browser.Type("retry");
        await browser.SettleAsync();

        // Five keystrokes, one query: the debounce is what keeps a fast typist from launching five searches.
        backend.SearchQueries.Should().Equal("retry");
    }

    [Fact]
    public async Task Searches_never_overlap_and_the_latest_text_always_wins()
    {
        var backend = NewBackend();
        backend.Delay = TimeSpan.FromMilliseconds(120);
        using var browser = await StartAsync(backend);

        // Force the first search to start, then type more while it is in flight.
        browser.Type("in");
        browser.ForceDueQueriesForTest();
        browser.Type("tegration");
        browser.ForceDueQueriesForTest();
        browser.Type(" test");
        await browser.SettleAsync();

        // One search was in flight; the two later edits coalesced into exactly one follow-up
        // instead of a search per keystroke piling onto the database.
        backend.SearchQueries.Should().Equal("in", "integration test");
        backend.MaxConcurrentSearches.Should().Be(1);
        browser.Screen.Should().Contain("Matches \u00b7 1");
    }

    [Fact]
    public async Task The_frame_keeps_animating_while_a_search_is_in_flight()
    {
        var backend = NewBackend();
        backend.Delay = TimeSpan.FromMilliseconds(400);
        using var browser = await StartAsync(backend);

        browser.Type("flaky");
        browser.ForceDueQueriesForTest();

        // No key arrives during the wait; the loop alone must mark frames dirty as the spinner turns.
        var redraws = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(330);
        while (DateTime.UtcNow < deadline)
        {
            if (browser.TickForTest())
            {
                redraws++;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        redraws.Should().BeGreaterThanOrEqualTo(3, "the spinner advances every 80 ms whether or not the user types");
        await browser.SettleAsync();
    }

    [Fact]
    public async Task Startup_warms_the_model_and_tops_up_the_index_in_the_background()
    {
        var backend = NewBackend();
        backend.TopUpChanges = true;
        using var browser = await StartAsync(backend);

        backend.WarmUpCalls.Should().Be(1);
        backend.TopUpCalls.Should().Be(1);
        browser.Screen.Should().Contain("index topped up");
        browser.Screen.Should().Contain("3 indexed", "the header shows the index size once it is known");
    }

    [Fact]
    public async Task Startup_skips_the_warm_up_when_semantic_search_is_unavailable()
    {
        var backend = NewBackend();
        backend.SemanticAvailable = false;
        using var browser = await StartAsync(backend);

        backend.WarmUpCalls.Should().Be(0);
        browser.Screen.Should().Contain("keyword", "the header says what kind of search will actually run");
    }

    [Fact]
    public async Task Clearing_the_query_returns_to_the_recent_list()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        browser.Type("flaky");
        await browser.SettleAsync();
        browser.Press(Ctrl('u'));
        await browser.SettleAsync();

        browser.Screen.Should().Contain("Sessions \u00b7 3");
    }

    [Fact]
    public async Task Enter_opens_the_reader_and_q_returns()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('j'));
        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();

        var reader = browser.Screen;
        reader.Should().Contain("Investigate flaky integration test");
        reader.Should().Contain("The test depends on wall-clock time");
        reader.Should().NotContain("Sessions \u00b7 3", "the reader takes the whole screen");

        browser.Press(Key('q'));
        browser.Screen.Should().Contain("Sessions \u00b7 3");
    }

    [Fact]
    public async Task Tool_output_is_hidden_until_toggled()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        browser.Screen.Should().NotContain("SECRET_TOOL_OUTPUT");
        browser.Screen.Should().Contain("bash: run tests");

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('x'));
        await browser.SettleAsync();

        browser.Screen.Should().Contain("SECRET_TOOL_OUTPUT");
    }

    [Fact]
    public async Task Resume_shows_the_command_and_exits_with_it_on_confirm()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('r'));
        await browser.SettleAsync();

        var confirmFrame = browser.Screen;
        confirmFrame.Should().Contain("Resume session");
        confirmFrame.Should().Contain("opencode --session ses_alpha");
        confirmFrame.Should().Contain(@"W:\Github\Alpha");
        browser.IsRunning.Should().BeTrue();

        browser.Press(new KeyEvent(KeyKind.Enter));

        browser.IsRunning.Should().BeFalse();
        browser.Result.Exit.Should().Be(BrowserExit.Resume);
        browser.Result.Resume!.Executable.Should().Be("opencode");
        browser.Result.Resume.Arguments.Should().Equal("--session", "ses_alpha");
        browser.Result.Resume.WorkingDirectory.Should().Be(@"W:\Github\Alpha");
        browser.Result.Session!.Ref.NativeId.Should().Be("ses_alpha");
    }

    [Fact]
    public async Task Resume_can_be_cancelled()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('R'));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("--fork");

        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.IsRunning.Should().BeTrue();
        browser.Screen.Should().NotContain("Resume session");
    }

    [Fact]
    public async Task Copy_keys_put_the_right_text_on_the_clipboard()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);
        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.Press(Key('y'));
        browser.LastCopied.Should().Be("retrace://opencode/ses_alpha");

        browser.Press(Key('Y'));
        browser.LastCopied.Should().Be("ses_alpha");

        browser.Press(Key('d'));
        browser.LastCopied.Should().Be(@"W:\Github\Alpha");

        browser.Press(Key('c'));
        await browser.SettleAsync();
        browser.LastCopied.Should().Be("opencode --session ses_alpha");
        browser.Screen.Should().Contain("copied resume command");
    }

    [Fact]
    public async Task Pick_mode_prints_the_directory_and_exits_on_enter()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend, new BrowserOptions { Pick = PickKind.Directory });

        browser.Type("TUI");
        await browser.SettleAsync();
        browser.Press(new KeyEvent(KeyKind.Enter));

        browser.IsRunning.Should().BeFalse();
        browser.Result.Exit.Should().Be(BrowserExit.Print);
        browser.Result.Output.Should().Be(@"W:\Github\Gamma");
    }

    [Fact]
    public async Task Pick_command_resolves_the_resume_command_before_exiting()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend, new BrowserOptions { Pick = PickKind.Command });

        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();

        browser.Result.Exit.Should().Be(BrowserExit.Print);
        browser.Result.Output.Should().Be("opencode --session ses_alpha");
    }

    [Fact]
    public async Task Tagging_updates_the_backend_and_the_row()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);
        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.Press(Key('t'));
        browser.Screen.Should().Contain("Tags");
        browser.Type("bugfix http");
        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();

        backend.TagChanges.Should().ContainSingle();
        backend.TagChanges[0].Add.Should().Equal("bugfix", "http");
        browser.Screen.Should().Contain("#bugfix #http");
    }

    [Fact]
    public async Task Source_filter_cycles_through_the_enabled_sources()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);
        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.Press(Key('s'));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Sessions \u00b7 2");
        browser.Screen.Should().Contain("source opencode");

        browser.Press(Key('s'));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Sessions \u00b7 1");
        browser.Screen.Should().Contain("source copilot-cli");

        browser.Press(Key('s'));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Sessions \u00b7 3");
    }

    [Fact]
    public async Task Missing_index_is_explained_and_fixed_by_refresh()
    {
        var backend = NewBackend();
        backend.ThrowIndexNotBuilt = true;
        using var browser = await StartAsync(backend);

        browser.Type("retry");
        await browser.SettleAsync();
        browser.Screen.Should().Contain("No index yet");

        browser.Press(Ctrl('r'));
        await browser.SettleAsync();

        backend.RefreshCalls.Should().Be(1);
        browser.Screen.Should().Contain("Matches \u00b7 1");
    }

    [Fact]
    public async Task Help_overlay_opens_and_closes()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);
        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.Press(Key('?'));
        browser.Screen.Should().Contain("resume in its harness");

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Screen.Should().NotContain("resume in its harness");
    }

    [Fact]
    public async Task Info_overlay_shows_the_directory_and_uri()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);
        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.Press(Key('i'));

        var screen = browser.Screen;
        screen.Should().Contain("directory");
        screen.Should().Contain(@"W:\Github\Alpha");
        screen.Should().Contain("retrace://opencode/ses_alpha");
    }

    [Fact]
    public async Task Narrow_terminal_hides_the_preview_but_keeps_the_list_usable()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend, width: 80, height: 24);

        var screen = browser.Screen;
        screen.Should().Contain("Sessions \u00b7 3");
        screen.Should().NotContain("Preview");

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("The retries never back off");
    }

    [Fact]
    public async Task Initial_query_starts_in_search_results()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend, new BrowserOptions { InitialQuery = "backoff" });

        browser.Screen.Should().Contain("Matches \u00b7 1");
        browser.Screen.Should().Contain("Fix retry logic in HttpClient");
    }

    [Fact]
    public async Task Quit_returns_a_quit_result()
    {
        var backend = NewBackend();
        using var browser = await StartAsync(backend);
        browser.Press(new KeyEvent(KeyKind.Escape));

        browser.Press(Key('q'));

        browser.IsRunning.Should().BeFalse();
        browser.Result.Exit.Should().Be(BrowserExit.Quit);
    }

    [Fact]
    public async Task Stale_results_from_a_superseded_query_are_discarded()
    {
        var backend = NewBackend();
        backend.Delay = TimeSpan.FromMilliseconds(30);
        using var browser = await StartAsync(backend);

        browser.Type("flaky");
        await browser.SettleAsync();
        browser.Type(" nothing-matches-this");
        await browser.SettleAsync();

        browser.Screen.Should().Contain("Matches \u00b7 0");
        browser.Screen.Should().Contain("No matches for");
    }

    [Fact]
    public void Transcript_formatter_highlights_terms_and_marks_turn_starts()
    {
        var transcript = FakeBackend.MakeTranscript("opencode", "ses_x", "Title here", @"W:\X", "first user line", "assistant says retry twice");

        var lines = TranscriptFormatter.Format(transcript, new TranscriptFormatOptions(60, false, false, ["retry"]));

        lines.Should().Contain(line => line.IsTurnStart && line.TurnIndex == 0);
        lines.Should().Contain(line => line.IsTurnStart && line.TurnIndex == 1);
        lines.Should().Contain(line => line.HasMatch && line.Spans.Any(span => span.Text == "retry"));
        lines[0].Spans[0].Text.Should().Be("Title here");
    }
}
