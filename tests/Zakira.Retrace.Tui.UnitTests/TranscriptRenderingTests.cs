using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

public sealed class TranscriptRenderingTests
{
    private static SessionTranscript Transcript(string text) =>
        FakeBackend.MakeTranscript("opencode", "rich-preview", "Readable conversation", "/a/very/long/workspace/path/that/must/wrap/in/a/narrow/terminal", text);

    private static string Text(IEnumerable<TranscriptLine> lines) => string.Join('\n', lines.Select(line => string.Concat(line.Spans.Select(span => span.Text))));

    [Fact]
    public void Rich_preview_renders_headings_emphasis_links_lists_and_quotes()
    {
        var lines = TranscriptFormatter.Format(Transcript("""
            # Implementation plan
            Use **strong** and *emphasized* text with `inline_code`.
            - [x] first task
            1. numbered task
            > quoted context
            See [documentation](https://example.test/docs).
            """), new TranscriptFormatOptions(80, false, false, []));
        var text = Text(lines);
        text.Should().Contain("Implementation plan").And.NotContain("# Implementation");
        text.Should().NotContain("**strong**").And.NotContain("`inline_code`");
        text.Should().Contain("• [x] first task").And.Contain("1. numbered task").And.Contain("│ quoted context");
        text.Should().Contain("documentation (https://example.test/docs)");
        lines.SelectMany(line => line.Spans).Should().Contain(span => span.Text.Contains("strong") && span.Style.Attributes.HasFlag(TermAttr.Bold));
        lines.SelectMany(line => line.Spans).Should().Contain(span => span.Text.Contains("emphasized") && span.Style.Attributes.HasFlag(TermAttr.Italic));
        lines.SelectMany(line => line.Spans).Should().Contain(span => span.Text == "documentation" && span.Style.Attributes.HasFlag(TermAttr.Underline));
    }

    [Fact]
    public void Fenced_code_preserves_indentation_language_and_unicode_and_highlights_search_terms()
    {
        var lines = TranscriptFormatter.Format(Transcript("""
            ```csharp
            public void Retry()
            {
                var name = "世界 😀";
                Retry();
            }
            ```
            """), new TranscriptFormatOptions(48, false, false, ["Retry"]));
        var text = Text(lines);
        text.Should().Contain("┌─ csharp").And.Contain("│     var name").And.Contain("世界 😀").And.Contain("└─");
        text.Should().NotContain("```csharp").And.NotContain("�");
        lines.Should().Contain(line => line.HasMatch && line.Spans.Any(span => span.Text.Contains("Retry")));
        lines.Should().AllSatisfy(line => line.Spans.Sum(span => TextWidth.Of(span.Text)).Should().BeLessThanOrEqualTo(48));
    }

    [Fact]
    public void Tables_wrap_cell_content_and_remain_readable_when_too_narrow_for_columns()
    {
        var transcript = Transcript("""
            | Provider | Result |
            | :--- | ---: |
            | OpenCode | searchable |
            | Copilot CLI | ready |
            """);
        var wide = TranscriptFormatter.Format(transcript, new TranscriptFormatOptions(50, false, false, []));
        Text(wide).Should().Contain("Provider").And.Contain("Result").And.Contain("OpenCode").And.Contain("searchable").And.Contain("┼");
        var narrow = TranscriptFormatter.Format(transcript, new TranscriptFormatOptions(12, false, false, []));
        narrow.Should().AllSatisfy(line => line.Spans.Sum(span => TextWidth.Of(span.Text)).Should().BeLessThanOrEqualTo(12));
        Text(narrow).Should().Contain("Provider:");
    }

    [Fact]
    public void Tool_output_and_diffs_are_opt_in_and_have_distinct_addition_and_deletion_styles()
    {
        var transcript = Transcript("Inspect the patch") with
        {
            Turns = [new Turn
            {
                Index = 0,
                Role = TurnRole.Assistant,
                Blocks =
                [
                    new PatchBlock { Path = "src/Retry.cs", Additions = 1, Deletions = 1, Diff = "@@ -1 +1 @@\n-old value\n+new value" },
                    new ToolCallBlock { ToolName = "bash", Title = "test", Output = "\u001b[31moutput\u001b[0m\n\tindented output" }
                ]
            }]
        };
        Text(TranscriptFormatter.Format(transcript, new TranscriptFormatOptions(50, false, false, []))).Should().NotContain("old value");
        var shown = TranscriptFormatter.Format(transcript, new TranscriptFormatOptions(50, true, false, []));
        Text(shown).Should().Contain("-old value").And.Contain("+new value").And.Contain("    indented output").And.NotContain("\u001b").And.NotContain("[31m");
        var added = shown.SelectMany(line => line.Spans).Single(span => span.Text == "+new value");
        var removed = shown.SelectMany(line => line.Spans).Single(span => span.Text == "-old value");
        added.Style.Foreground.Should().NotBe(removed.Style.Foreground);
    }

    [Fact]
    public void Literal_preview_keeps_markdown_markers_and_unclosed_fences_keep_their_contents()
    {
        var plain = TranscriptFormatter.Format(Transcript("# Heading\n**bold** and `code`\n    var x = 1;"), new TranscriptFormatOptions(60, false, false, [], RichText: false));
        Text(plain).Should().Contain("# Heading").And.Contain("**bold** and `code`").And.Contain("    var x = 1;");
        Text(TranscriptFormatter.Format(Transcript("```text\nstill visible"), new TranscriptFormatOptions(60, false, false, []))).Should().Contain("still visible");
    }

    [Fact]
    public void Span_wrapping_keeps_long_words_and_style_boundaries_inside_the_column_budget()
    {
        var spans = new StyledSpan[]
        {
            new("bold", Style.Plain.Bold()), new("word ", Style.Plain), new("中中中中中 e\u0301abcdefghi", Style.Plain.Italic())
        };
        var lines = TranscriptFormatter.WrapSpans(spans, 5).ToArray();
        lines.Should().AllSatisfy(line => line.Sum(span => TextWidth.Of(span.Text)).Should().BeLessThanOrEqualTo(5));
        string.Concat(lines.SelectMany(line => line).Select(span => span.Text)).Replace(" ", "", StringComparison.Ordinal)
            .Should().Be("boldword中中中中中e\u0301abcdefghi");
    }

    [Fact]
    public void Empty_highlight_terms_do_not_prevent_progress()
    {
        var result = TranscriptFormatter.Highlight("retry", Style.Plain, ["", "retry"]);
        result.HasMatch.Should().BeTrue();
    }

    [Fact]
    public void Long_unclosed_markup_stays_literal_and_does_not_drop_text()
    {
        var ticks = new string('`', 8000);
        var brackets = new string('[', 8000);
        var lines = TranscriptFormatter.Format(Transcript("literal " + ticks + brackets), new TranscriptFormatOptions(60, false, false, []));
        Text(lines).Count(character => character == '`').Should().Be(8000);
        Text(lines).Count(character => character == '[').Should().Be(8000);
    }

    [Fact]
    public async Task Browser_can_toggle_rich_and_literal_preview_without_losing_the_session()
    {
        var backend = new FakeBackend();
        backend.Transcripts.Add(Transcript("# Heading\n**bold** and `code`"));
        using var browser = SessionBrowser.CreateHeadless(backend, new BrowserOptions(), 140, 30);
        browser.Start();
        await browser.SettleAsync();
        browser.Screen.Should().NotContain("**bold**");
        browser.Press(new KeyEvent(KeyKind.Escape));
        browser.Press(new KeyEvent(KeyKind.Character, 'v'));
        browser.Screen.Should().Contain("**bold**");
        browser.Press(new KeyEvent(KeyKind.Enter));
        browser.Press(new KeyEvent(KeyKind.Character, 'v'));
        browser.Screen.Should().NotContain("**bold**").And.Contain("Heading");
    }
}
