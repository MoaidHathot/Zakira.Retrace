using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

public sealed class BrowserFilterTests
{
    private static FakeBackend Backend()
    {
        var backend = new FakeBackend { SourceIds = ["opencode", "copilot-cli", "copilot-vscode"] };
        var now = DateTimeOffset.UtcNow;
        Add("opencode", "alpha", "Alpha", now.AddDays(-2));
        Add("copilot-cli", "beta", "Beta", now.AddDays(-12));
        Add("copilot-vscode", "gamma", "Gamma", now.AddDays(-60));
        return backend;

        void Add(string provider, string id, string title, DateTimeOffset updated)
        {
            var transcript = FakeBackend.MakeTranscript(provider, id, title, Path.GetTempPath(), "retry question", "retry answer");
            backend.Transcripts.Add(transcript with { Summary = transcript.Summary with { UpdatedAt = updated } });
        }
    }

    private static async Task<SessionBrowser> Start(FakeBackend backend, BrowserOptions? options = null, int width = 140, int height = 30)
    {
        var browser = SessionBrowser.CreateHeadless(backend, options ?? new BrowserOptions(), width, height);
        browser.Start();
        await browser.SettleAsync();
        return browser;
    }

    private static KeyEvent Key(char value) => new(KeyKind.Character, value);

    private static KeyEvent Function(int value) => new(KeyKind.Function, Number: value);

    [Fact]
    public async Task Provider_only_queries_browse_and_provider_text_queries_search_with_the_same_filter()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        browser.Type("provider:copilot");
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Sessions · 2");
        backend.SearchRequests.Should().BeEmpty();
        backend.ListRequests[^1].SourceIds.Should().Equal("copilot-cli", "copilot-vscode");

        browser.Type(" retry");
        await browser.SettleAsync();
        backend.SearchRequests.Should().ContainSingle();
        backend.SearchRequests[0].Text.Should().Be("retry");
        backend.SearchRequests[0].Filter.SourceIds.Should().Equal("copilot-cli", "copilot-vscode");
        browser.Screen.Should().Contain("Matches · 2");
    }

    [Fact]
    public async Task Picker_toggles_multiple_providers_and_none_never_means_all()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        browser.Press(Function(2));
        browser.Screen.Should().Contain("Providers & filters");
        browser.Press(Key(' ')); // remove OpenCode
        browser.Press(new KeyEvent(KeyKind.Down));
        browser.Press(Key(' ')); // remove CLI
        await browser.SettleAsync();
        backend.ListRequests[^1].SourceIds.Should().Equal("copilot-vscode");
        browser.Press(Key('n'));
        var requests = backend.ListRequests.Count;
        await browser.SettleAsync();
        backend.ListRequests.Should().HaveCount(requests, "empty selection should not reach the API where empty means all");
        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Screen.Should().Contain("No providers selected");

        browser.Press(Function(2));
        browser.Press(Key('a'));
        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Sessions · 3");
    }

    [Fact]
    public async Task Initial_cli_selection_can_be_broadened_and_query_facets_stay_in_sync_with_the_picker()
    {
        var backend = Backend();
        using var browser = await Start(backend, new BrowserOptions { Filter = new SessionFilter { SourceIds = ["copilot-cli", "copilot-vscode"] } });
        browser.Screen.Should().Contain("Sessions · 2");
        browser.Press(Function(2));
        browser.Press(Key('a'));
        browser.Press(new KeyEvent(KeyKind.Escape));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Sessions · 3");

        browser.Type("provider:opencode retry");
        await browser.SettleAsync();
        browser.Press(Function(2));
        browser.Press(Key('a'));
        browser.Press(new KeyEvent(KeyKind.Escape));
        await browser.SettleAsync();
        backend.SearchRequests[^1].Filter.SourceIds.Should().HaveCount(3);
        backend.SearchRequests[^1].Text.Should().Be("retry");
        browser.Screen.Should().NotContain("provider:opencode retry");
    }

    [Fact]
    public async Task Week_month_and_all_time_controls_filter_before_fetching()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        browser.Press(Function(3));
        browser.Press(new KeyEvent(KeyKind.Right)); // day
        browser.Press(new KeyEvent(KeyKind.Right)); // week
        browser.Press(new KeyEvent(KeyKind.Escape));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("last 7 days").And.Contain("Sessions · 1");
        backend.ListRequests[^1].Since.Should().NotBeNull();

        browser.Press(Function(3));
        browser.Press(new KeyEvent(KeyKind.Right)); // month
        browser.Press(new KeyEvent(KeyKind.Escape));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("last month").And.Contain("Sessions · 2");

        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(Key('0'));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("all time").And.Contain("Sessions · 3");
    }

    [Fact]
    public async Task Sorting_changes_browse_order_before_the_list_limit_and_group_headers_are_not_selectable()
    {
        var backend = Backend();
        using var browser = await Start(backend, new BrowserOptions { InitialQuery = "sort:oldest", ListLimit = 1 });
        backend.ListRequests[^1].Sort.Should().Be(SessionSortOrder.Oldest);
        browser.Press(Key('y'));
        browser.LastCopied.Should().Be("retrace://copilot-vscode/gamma");

        using var grouped = await Start(backend, new BrowserOptions { InitialQuery = "retry group:provider sort:title" });
        grouped.Screen.Should().Contain("─ copilot-cli · 1").And.Contain("─ copilot-vscode · 1").And.Contain("─ opencode · 1");
        grouped.Press(Key('g'));
        grouped.Press(Key('y'));
        grouped.LastCopied.Should().Be("retrace://copilot-cli/beta");
        grouped.Press(Key('j'));
        grouped.Press(Key('y'));
        grouped.LastCopied.Should().Be("retrace://copilot-vscode/gamma");
        grouped.Press(Key('G'));
        grouped.Press(Key('y'));
        grouped.LastCopied.Should().Be("retrace://opencode/alpha");
    }

    [Fact]
    public async Task Invalid_facets_do_not_query_the_backend_and_recover_after_editing()
    {
        var backend = Backend();
        using var browser = await Start(backend);
        browser.Type("retry provider:typo");
        await browser.SettleAsync();
        backend.SearchRequests.Should().BeEmpty();
        browser.Screen.Should().Contain("Unknown or disabled provider");
        browser.Press(new KeyEvent(KeyKind.Character, 'u', Ctrl: true));
        browser.Type("retry provider:opencode");
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Matches · 1");
    }

    [Fact]
    public async Task Narrow_screen_keeps_provider_and_time_controls_visible()
    {
        var backend = Backend();
        using var browser = await Start(backend, new BrowserOptions { InitialQuery = "provider:opencode since:7d" }, width: 60, height: 18);
        browser.Screen.Should().Contain("F2 providers: opencode").And.Contain("F3 filters");
        browser.Press(Function(3));
        browser.Press(new KeyEvent(KeyKind.End));
        browser.Screen.Should().Contain("Reset all filters");
        browser.Press(Key(' '));
        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();
        browser.Screen.Should().Contain("Sessions · 3");
    }

    [Fact]
    public async Task Mouse_opens_the_provider_controls_and_group_headers_do_not_open_sessions()
    {
        var backend = Backend();
        using var browser = await Start(backend, new BrowserOptions { InitialQuery = "group:provider" });
        browser.Press(new KeyEvent(KeyKind.MouseClick, Row: 3, Column: 4));
        browser.Screen.Should().Contain("Providers & filters");
        browser.Press(new KeyEvent(KeyKind.Escape));

        var screen = browser.Screen.Split('\n');
        var groupRow = Array.FindIndex(screen, line => line.Contains("─ copilot-cli · 1", StringComparison.Ordinal));
        groupRow.Should().BeGreaterThan(0);
        browser.Press(new KeyEvent(KeyKind.MouseClick, Row: groupRow, Column: 4));
        browser.Screen.Should().Contain("Sessions · 3");
        browser.Press(new KeyEvent(KeyKind.MouseClick, Row: groupRow + 1, Column: 4));
        browser.Press(Key('y'));
        browser.LastCopied.Should().Be("retrace://copilot-cli/beta");
    }

    [Fact]
    public async Task Small_terminal_help_can_scroll_to_query_syntax_and_return_to_the_reader()
    {
        using var browser = await Start(Backend(), width: 80, height: 24);
        browser.Press(new KeyEvent(KeyKind.Enter));
        await browser.SettleAsync();
        browser.Press(Key('?'));
        browser.Press(new KeyEvent(KeyKind.End));
        browser.Screen.Should().Contain("since: until:").And.Contain("indexing");
        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Screen.Should().NotContain("Sessions · 3").And.Contain("retry question");
    }

    [Fact]
    public async Task Ordering_and_grouping_existing_search_results_do_not_repeat_database_or_model_work()
    {
        var backend = Backend();
        using var browser = await Start(backend, new BrowserOptions { InitialQuery = "retry" });
        var requests = backend.SearchRequests.Count;
        browser.Press(Key('O'));
        browser.Screen.Should().Contain("order: newest");
        browser.Press(Key('b'));
        browser.Screen.Should().Contain("group: provider");
        browser.Screen.Should().Contain("─ opencode · 1");
        await browser.SettleAsync();
        backend.SearchRequests.Should().HaveCount(requests);
    }

    [Fact]
    public async Task All_disabled_providers_show_enablement_guidance_and_do_no_search_or_model_work()
    {
        var backend = new FakeBackend { SourceIds = [] };
        using var browser = await Start(backend, width: 80);
        browser.Screen.Should().Contain("No enabled providers").And.Contain("retrace sources enable");
        backend.ListRequests.Should().BeEmpty();
        backend.SearchRequests.Should().BeEmpty();
        backend.WarmUpCalls.Should().Be(0);
    }
}
