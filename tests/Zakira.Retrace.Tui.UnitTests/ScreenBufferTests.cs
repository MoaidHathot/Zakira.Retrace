using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

/// <summary>The cell grid and its ANSI rendering.</summary>
public sealed class ScreenBufferTests
{
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
        buffer.Write(0, 0, "ab", Style.Fg(TermColor.Red));
        buffer.Write(0, 2, "cd", Style.Fg(TermColor.Red));
        buffer.Write(0, 4, "ef", new Style(TermColor.Default, TermColor.Blue, TermAttr.Bold));

        var rendered = buffer.RenderRows()[0];

        rendered.Should().Contain("\u001b[0;31mabcd");
        rendered.Should().Contain("\u001b[0;1;44mef");
        rendered.Should().StartWith("\u001b[1;1H");
    }

    [Fact]
    public void Box_draws_corners_edges_and_a_title()
    {
        var buffer = new ScreenBuffer(12, 4);

        buffer.DrawBox(new Rect(0, 0, 12, 4), Style.Plain, "Hi");

        buffer.RowText(0).Should().Be("\u256d\u2500 Hi \u2500\u2500\u2500\u2500\u2500\u256e");
        buffer.RowText(3).Should().Be("\u2570\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u256f");
        buffer.RowText(1).Should().Be("\u2502          \u2502");
    }

    [Fact]
    public void RestyleRow_changes_style_without_touching_text()
    {
        var buffer = new ScreenBuffer(5, 2);
        var rect = new Rect(0, 0, 5, 2);
        buffer.WriteIn(rect, 1, 0, "abc", Style.Plain);

        buffer.RestyleRow(rect, 1, style => style.WithBg(TermColor.Blue));

        buffer.RowText(1).Should().Be("abc");
        buffer.StyleAt(1, 0).Background.Should().Be(TermColor.Blue);
        buffer.StyleAt(1, 4).Background.Should().Be(TermColor.Blue);
        buffer.StyleAt(0, 0).Background.Should().Be(TermColor.Default);
    }

    [Fact]
    public void Scrollbar_marks_the_visible_window()
    {
        var buffer = new ScreenBuffer(1, 10);

        buffer.DrawScrollbar(new Rect(0, 0, 1, 10), totalLines: 100, visibleLines: 10, firstLine: 0, Style.Plain);
        buffer.RowText(0).Should().Be("\u2588");
        buffer.RowText(9).Should().Be("\u2502");

        buffer.DrawScrollbar(new Rect(0, 0, 1, 10), totalLines: 100, visibleLines: 10, firstLine: 90, Style.Plain);
        buffer.RowText(9).Should().Be("\u2588");
    }
}
