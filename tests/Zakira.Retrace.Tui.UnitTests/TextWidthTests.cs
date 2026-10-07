using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

/// <summary>Column arithmetic and wrapping, which every pane depends on to stay aligned.</summary>
public sealed class TextWidthTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("abc", 3)]
    [InlineData("caf\u00e9", 4)]
    [InlineData("\u4e2d\u6587", 4)]
    [InlineData("a\u0301", 1)]
    [InlineData("\U0001F600", 2)]
    [InlineData("x\u200by", 2)]
    public void Width_counts_terminal_cells_not_chars(string text, int expected) =>
        TextWidth.Of(text).Should().Be(expected);

    [Fact]
    public void Clip_cuts_at_a_cell_boundary_and_appends_an_ellipsis()
    {
        TextWidth.Clip("hello world", 8).Should().Be("hello w\u2026");
        TextWidth.Clip("hello", 8).Should().Be("hello");
        TextWidth.Clip("\u4e2d\u6587\u4e2d\u6587", 5).Should().Be("\u4e2d\u6587\u2026");
    }

    [Fact]
    public void Fit_pads_to_the_exact_width()
    {
        var fitted = TextWidth.Fit("ab", 5);

        fitted.Should().Be("ab   ");
        TextWidth.Of(TextWidth.Fit("\u4e2d\u6587\u4e2d\u6587", 5)).Should().Be(5);
    }

    [Fact]
    public void Wrap_breaks_on_spaces_and_never_exceeds_the_width()
    {
        var lines = TextWidth.Wrap("the quick brown fox jumps over the lazy dog", 10).ToArray();

        lines.Should().AllSatisfy(line => TextWidth.Of(line).Should().BeLessThanOrEqualTo(10));
        string.Join(' ', lines).Should().Be("the quick brown fox jumps over the lazy dog");
    }

    [Fact]
    public void Wrap_splits_a_word_longer_than_the_width()
    {
        var lines = TextWidth.Wrap("abcdefghijklmnop", 5).ToArray();

        lines.Should().Equal("abcde", "fghij", "klmno", "p");
    }

    [Fact]
    public void Wrap_preserves_an_empty_paragraph_as_one_blank_line() =>
        TextWidth.Wrap(string.Empty, 20).Should().Equal(string.Empty);
}
