using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Tui.UnitTests;

public sealed class BrowserQueryTests
{
    private static readonly string[] Providers = ["opencode", "copilot-cli", "copilot-vscode"];
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Provider_facets_accept_aliases_commas_repetition_and_exclusions()
    {
        var parsed = BrowserQuery.Parse("provider:OPENCODE,VSCode provider:copilot-cli -source:vscode retry backoff", Providers, Now);
        parsed.Providers.Should().Equal("opencode", "copilot-cli");
        parsed.Text.Should().Be("retry backoff");

        BrowserQuery.Parse("provider:copilot", Providers, Now).Providers.Should().Equal("copilot-cli", "copilot-vscode");
        BrowserQuery.Parse("-provider:copilot", Providers, Now).Providers.Should().Equal("opencode");
        BrowserQuery.Parse("provider:none", Providers, Now).Providers.Should().BeEmpty();
    }

    [Fact]
    public void Quoted_phrases_urls_and_paths_remain_literal_search_text()
    {
        const string text = "\"provider:opencode\" https://example.test C:\\repo \"exact phrase\"";
        var parsed = BrowserQuery.Parse(text + " provider:\"copilot-cli\"", Providers, Now);
        parsed.Text.Should().Be(text);
        parsed.Providers.Should().Equal("copilot-cli");
        BrowserQuery.RemoveFacets(text + " since:week").Should().Be(text);
    }

    [Fact]
    public void Relative_windows_and_inclusive_absolute_dates_have_predictable_bounds()
    {
        BrowserQuery.Parse("since:7d", Providers, Now).Since.Should().Be(Now.AddDays(-7));
        BrowserQuery.Parse("last:month", Providers, Now).Since.Should().Be(Now.AddMonths(-1));
        var parsed = BrowserQuery.Parse("since:2026-10-01 until:2026-10-31", Providers, Now);
        parsed.Since!.Value.Date.Should().Be(new DateTime(2026, 10, 1));
        parsed.Until!.Value.Date.Should().Be(new DateTime(2026, 10, 31));
        parsed.Until.Value.TimeOfDay.Should().Be(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1));
        parsed.Text.Should().BeEmpty("facets alone browse matching sessions rather than searching for the facet words");
    }

    [Theory]
    [InlineData("provider:")]
    [InlineData("provider:copliot")]
    [InlineData("since:999999999999999999999d")]
    [InlineData("since:2026-11-20 until:2026-11-01")]
    [InlineData("sort:banana")]
    [InlineData("group:banana")]
    public void Invalid_facets_are_actionable_errors_instead_of_silent_broadening(string text)
    {
        var action = () => BrowserQuery.Parse(text, Providers, Now);
        action.Should().Throw<RetraceException>();
    }

    [Fact]
    public void Sort_group_and_metadata_facets_can_be_combined()
    {
        var parsed = BrowserQuery.Parse("retry sort:oldest group:week repo:\"My Project\" agent:build model:claude tag:bugfix tag:#http", Providers, Now);
        parsed.Sort.Should().Be(BrowserSort.Oldest);
        parsed.Group.Should().Be(BrowserGroup.Week);
        parsed.Repository.Should().Be("My Project");
        parsed.Agent.Should().Be("build");
        parsed.Model.Should().Be("claude");
        parsed.Tags.Should().Equal("bugfix", "http");
        parsed.Text.Should().Be("retry");
    }
}
