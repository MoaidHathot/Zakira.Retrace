using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

/// <summary>The cell grid and its ANSI rendering.</summary>
public sealed class ScreenBufferTests
{
    private static readonly Color Red = Color.FromAnsi(AnsiColor.Red);
    private static readonly Color Blue = Color.FromAnsi(AnsiColor.Blue);

    [Fact]
    public void Write_clips_to_the_buffer_and_to_the_requested_width()
    {
        var buffer = new ScreenBuffer(10, 2);

        var written = buffer.Write(0, 7, "abcdef", Style.Plain);

        written.Should().Be(3);
        buffer.RowText(0).Should().Be("       abc");

        buffer.Write(1, 0, "abcdef", Style.Plain, maxWidth: 2);
        buffer.RowText(1).Should().Be("ab");
    }

    [Fact]
    public void Wide_characters_occupy_two_cells_and_render_once()
    {
        var buffer = new ScreenBuffer(6, 1);

        buffer.Write(0, 0, "\u4e2dx", Style.Plain);

        buffer.RowText(0).Should().Be("\u4e2dx");
        var rendered = buffer.RenderRows()[0];
        rendered.Should().Contain("\u4e2dx");
        rendered.Count(character => character == '\u4e2d').Should().Be(1);
    }

    [Fact]
    public void A_wide_character_that_does_not_fit_is_dropped_rather_than_split()
    {
        var buffer = new ScreenBuffer(3, 1);

        buffer.Write(0, 0, "ab\u4e2d", Style.Plain);

        buffer.RowText(0).Should().Be("ab");
    }

    [Fact]
    public void Combining_marks_attach_to_the_previous_cell()
    {
        var buffer = new ScreenBuffer(4, 1);

        buffer.Write(0, 0, "e\u0301x", Style.Plain);

        buffer.RowText(0).Should().Be("e\u0301x");
        buffer.RenderRows()[0].Should().Contain("e\u0301x");
    }

    [Fact]
    public void Styles_become_sgr_sequences_only_when_they_change()
    {
        var buffer = new ScreenBuffer(6, 1);
        buffer.Write(0, 0, "ab", Style.Fg(Red));
        buffer.Write(0, 2, "cd", Style.Fg(Red));
        buffer.Write(0, 4, "ef", new Style(Color.Default, Blue, TermAttr.Bold));

        var rendered = buffer.RenderRows()[0];

        rendered.Should().Contain("\u001b[0;31mabcd");
        rendered.Should().Contain("\u001b[0;1;44mef");
        rendered.Should().StartWith("\u001b[1;1H");
    }

    [Fact]
    public void Rgb_colours_render_at_every_depth()
    {
        var buffer = new ScreenBuffer(2, 1);
        buffer.Write(0, 0, "ab", Style.Fg(Color.Hex("#7aa2f7", AnsiColor.BrightBlue)));

        buffer.RenderRows(ColorDepth.TrueColor)[0].Should().Contain("\u001b[0;38;2;122;162;247m");
        buffer.RenderRows(ColorDepth.Ansi256)[0].Should().MatchRegex(@"\u001b\[0;38;5;\d+m");
        buffer.RenderRows(ColorDepth.Ansi16)[0].Should().Contain("\u001b[0;94m", "the palette names its own sixteen-colour fallback");
        buffer.RenderRows(ColorDepth.None)[0].Should().NotContain(";38;", "NO_COLOR means no colour at all");
    }

    [Fact]
    public void A_default_style_has_no_colour()
    {
        default(Style).Foreground.IsDefault.Should().BeTrue();
        default(Color).IsDefault.Should().BeTrue();
        Color.Hex("#000000", AnsiColor.Black).IsDefault.Should().BeFalse("black is a colour, not the absence of one");
    }

    [Fact]
    public void Box_draws_corners_edges_and_titles_on_both_sides()
    {
        var buffer = new ScreenBuffer(20, 4);

        buffer.DrawBox(new Rect(0, 0, 20, 4), Style.Plain, [new StyledSpan("Hi", Style.Plain)], [new StyledSpan("v1", Style.Plain)]);

        buffer.RowText(0).Should().StartWith("\u256d\u2500 Hi \u2500");
        buffer.RowText(0).Should().EndWith("\u2500 v1 \u2500\u256e");
        buffer.RowText(3).Should().Be("\u2570" + new string('\u2500', 18) + "\u256f");
        buffer.RowText(1).Should().Be("\u2502" + new string(' ', 18) + "\u2502");
    }

    [Fact]
    public void RestyleRow_changes_style_without_touching_text()
    {
        var buffer = new ScreenBuffer(5, 2);
        var rect = new Rect(0, 0, 5, 2);
        buffer.WriteIn(rect, 1, 0, "abc", Style.Plain);

        buffer.RestyleRow(rect, 1, style => style.WithBg(Blue));

        buffer.RowText(1).Should().Be("abc");
        buffer.StyleAt(1, 0).Background.Should().Be(Blue);
        buffer.StyleAt(1, 4).Background.Should().Be(Blue);
        buffer.StyleAt(0, 0).Background.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Shadow_darkens_the_cells_right_of_and_below_a_card()
    {
        var buffer = new ScreenBuffer(10, 6);
        var shadow = Color.Hex("#0d0f17", AnsiColor.Black);

        buffer.DrawShadow(new Rect(1, 1, 5, 3), shadow);

        buffer.StyleAt(2, 6).Background.Should().Be(shadow, "the column right of the card");
        buffer.StyleAt(4, 3).Background.Should().Be(shadow, "the row below the card");
        buffer.StyleAt(1, 6).Background.IsDefault.Should().BeTrue("the shadow is offset by one row");
        buffer.StyleAt(2, 2).Background.IsDefault.Should().BeTrue("the card interior is untouched");
    }

    [Fact]
    public void Scrollbar_marks_the_visible_window()
    {
        var buffer = new ScreenBuffer(1, 10);

        buffer.DrawScrollbar(new Rect(0, 0, 1, 10), totalLines: 100, visibleLines: 10, firstLine: 0, Style.Plain, Style.Plain);
        buffer.RowText(0).Should().Be("\u2503");
        buffer.RowText(9).Should().Be("\u2502");

        buffer.DrawScrollbar(new Rect(0, 0, 1, 10), totalLines: 100, visibleLines: 10, firstLine: 90, Style.Plain, Style.Plain);
        buffer.RowText(9).Should().Be("\u2503");
    }

    [Fact]
    public void Nearest_256_colour_prefers_the_grey_ramp_for_greys_and_the_cube_for_colours()
    {
        Color.Hex("#808080", AnsiColor.White).ToAnsi256().Should().BeInRange(232, 255);
        Color.Hex("#ff0000", AnsiColor.Red).ToAnsi256().Should().Be(196);
        Color.Hex("#000000", AnsiColor.Black).ToAnsi256().Should().Be(16);
    }
}
