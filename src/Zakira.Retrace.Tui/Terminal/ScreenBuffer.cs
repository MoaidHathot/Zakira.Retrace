using System.Text;

namespace Zakira.Retrace.Tui.Terminal;

/// <summary>The 16 ANSI colours plus the terminal's default. Chosen over truecolor so the browser follows the user's terminal theme, as lazygit does.</summary>
public enum TermColor
{
    /// <summary>
    /// The terminal's default foreground or background. Deliberately zero so that a
    /// <see langword="default"/> <see cref="Style"/> means "no colour" rather than black.
    /// </summary>
    Default = 0,

    /// <summary>ANSI 0.</summary>
    Black,

    /// <summary>ANSI 1.</summary>
    Red,

    /// <summary>ANSI 2.</summary>
    Green,

    /// <summary>ANSI 3.</summary>
    Yellow,

    /// <summary>ANSI 4.</summary>
    Blue,

    /// <summary>ANSI 5.</summary>
    Magenta,

    /// <summary>ANSI 6.</summary>
    Cyan,

    /// <summary>ANSI 7.</summary>
    White,

    /// <summary>ANSI 8, the bright black most themes render as grey.</summary>
    BrightBlack,

    /// <summary>ANSI 9.</summary>
    BrightRed,

    /// <summary>ANSI 10.</summary>
    BrightGreen,

    /// <summary>ANSI 11.</summary>
    BrightYellow,

    /// <summary>ANSI 12.</summary>
    BrightBlue,

    /// <summary>ANSI 13.</summary>
    BrightMagenta,

    /// <summary>ANSI 14.</summary>
    BrightCyan,

    /// <summary>ANSI 15.</summary>
    BrightWhite
}

/// <summary>Text attributes.</summary>
[Flags]
public enum TermAttr
{
    /// <summary>Plain.</summary>
    None = 0,

    /// <summary>Bold.</summary>
    Bold = 1,

    /// <summary>Dim / faint.</summary>
    Dim = 2,

    /// <summary>Italic.</summary>
    Italic = 4,

    /// <summary>Underline.</summary>
    Underline = 8,

    /// <summary>Reverse video.</summary>
    Reverse = 16
}

/// <summary>A complete cell style.</summary>
/// <param name="Foreground">Foreground colour.</param>
/// <param name="Background">Background colour.</param>
/// <param name="Attributes">Attributes.</param>
public readonly record struct Style(TermColor Foreground = TermColor.Default, TermColor Background = TermColor.Default, TermAttr Attributes = TermAttr.None)
{
    /// <summary>No colour, no attributes.</summary>
    public static Style Plain => default;

    /// <summary>Returns this style with a different foreground.</summary>
    public Style WithFg(TermColor color) => this with { Foreground = color };

    /// <summary>Returns this style with a different background.</summary>
    public Style WithBg(TermColor color) => this with { Background = color };

    /// <summary>Returns this style with extra attributes.</summary>
    public Style With(TermAttr attributes) => this with { Attributes = Attributes | attributes };

    /// <summary>Bold variant.</summary>
    public Style Bold() => With(TermAttr.Bold);

    /// <summary>Dim variant.</summary>
    public Style Dim() => With(TermAttr.Dim);

    /// <summary>Convenience: a foreground-only style.</summary>
    public static Style Fg(TermColor color) => new(color);
}

/// <summary>One screen cell: a grapheme (one or more UTF-16 units) and its style.</summary>
internal struct Cell(string text, Style style)
{
    /// <summary>The text occupying the cell; an empty string marks the tail of a wide character.</summary>
    public string Text = text;

    /// <summary>Style.</summary>
    public Style Style = style;

    /// <summary>A blank cell in the given style.</summary>
    public static Cell Blank(Style style) => new(" ", style);
}

/// <summary>A rectangle of cells, in screen coordinates.</summary>
/// <param name="Left">First column.</param>
/// <param name="Top">First row.</param>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
public readonly record struct Rect(int Left, int Top, int Width, int Height)
{
    /// <summary>One past the last column.</summary>
    public int Right => Left + Width;

    /// <summary>One past the last row.</summary>
    public int Bottom => Top + Height;

    /// <summary>Whether the rectangle has any cells.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Whether a cell lies inside.</summary>
    public bool Contains(int row, int column) => row >= Top && row < Bottom && column >= Left && column < Right;

    /// <summary>Shrinks by one cell on every side; what a box's interior is.</summary>
    public Rect Inset(int amount = 1) => new(Left + amount, Top + amount, Math.Max(0, Width - 2 * amount), Math.Max(0, Height - 2 * amount));
}

/// <summary>
/// An off-screen frame. Everything draws into this, then <see cref="RenderRows"/> turns it into
/// one ANSI string per row for the terminal to diff against the previous frame.
/// </summary>
/// <remarks>
/// A cell grid, rather than a stream of escape sequences, is what makes a multi-pane layout
/// tractable: panes clip to their rectangle, overlays simply draw on top, and nothing has to
/// know what was underneath. The cost is a few hundred kilobytes of cells, which is nothing.
/// </remarks>
public sealed class ScreenBuffer
{
    private Cell[,] cells;

    /// <summary>Creates a buffer of the given size.</summary>
    public ScreenBuffer(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        cells = new Cell[Height, Width];
        Clear();
    }

    /// <summary>Columns.</summary>
    public int Width { get; private set; }

    /// <summary>Rows.</summary>
    public int Height { get; private set; }

    /// <summary>The whole screen as a rectangle.</summary>
    public Rect Bounds => new(0, 0, Width, Height);

    /// <summary>Resizes, discarding contents.</summary>
    public void Resize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        cells = new Cell[Height, Width];
        Clear();
    }

    /// <summary>Fills every cell with a blank in the default style.</summary>
    public void Clear() => Fill(Bounds, Style.Plain);

    /// <summary>Fills a rectangle with blanks.</summary>
    public void Fill(Rect rect, Style style)
    {
        var clipped = Clip(rect);
        for (var row = clipped.Top; row < clipped.Bottom; row++)
        {
            for (var column = clipped.Left; column < clipped.Right; column++)
            {
                cells[row, column] = Cell.Blank(style);
            }
        }
    }

    /// <summary>
    /// Writes text starting at a cell, clipped to <paramref name="maxWidth"/> columns and to the
    /// buffer. Returns the number of columns written.
    /// </summary>
    public int Write(int row, int column, string? text, Style style, int maxWidth = int.MaxValue)
    {
        if (string.IsNullOrEmpty(text) || row < 0 || row >= Height || column >= Width)
        {
            return 0;
        }

        // Guarded addition: callers pass int.MaxValue to mean "no limit".
        var limit = maxWidth >= Width - column ? Width : Math.Min(Width, column + Math.Max(0, maxWidth));
        var cursor = column;

        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = TextWidth.Of(rune);

            if (runeWidth == 0)
            {
                // Combining marks and joiners attach to the previous cell so emoji sequences stay
                // intact instead of being split across cells by the renderer.
                if (cursor > column && cursor - 1 >= 0 && cursor - 1 < Width)
                {
                    var owner = cursor - 1;
                    while (owner > 0 && cells[row, owner].Text.Length == 0)
                    {
                        owner--;
                    }

                    cells[row, owner].Text += rune.ToString();
                }

                continue;
            }

            if (cursor + runeWidth > limit)
            {
                break;
            }

            if (cursor >= 0)
            {
                cells[row, cursor] = new Cell(rune.ToString(), style);
                if (runeWidth == 2 && cursor + 1 < Width)
                {
                    cells[row, cursor + 1] = new Cell(string.Empty, style);
                }
            }

            cursor += runeWidth;
        }

        return Math.Max(0, cursor - column);
    }

    /// <summary>Writes text inside a rectangle at a relative position, clipped to the rectangle.</summary>
    public int WriteIn(Rect rect, int row, int column, string? text, Style style)
    {
        if (row < 0 || row >= rect.Height || column >= rect.Width)
        {
            return 0;
        }

        return Write(rect.Top + row, rect.Left + column, text, style, rect.Width - column);
    }

    /// <summary>Writes a sequence of styled spans on one row inside a rectangle, clipped. Returns columns used.</summary>
    public int WriteSpans(Rect rect, int row, int column, IEnumerable<StyledSpan> spans)
    {
        var cursor = column;
        foreach (var span in spans)
        {
            if (cursor >= rect.Width)
            {
                break;
            }

            cursor += WriteIn(rect, row, cursor, span.Text, span.Style);
        }

        return cursor - column;
    }

    /// <summary>Restyles every cell on one row of a rectangle; how a selected row is highlighted.</summary>
    public void RestyleRow(Rect rect, int row, Func<Style, Style> transform)
    {
        var absolute = rect.Top + row;
        if (absolute < 0 || absolute >= Height)
        {
            return;
        }

        var clipped = Clip(rect);
        for (var column = clipped.Left; column < clipped.Right; column++)
        {
            cells[absolute, column].Style = transform(cells[absolute, column].Style);
        }
    }

    /// <summary>Draws a single-line box around a rectangle with an optional title in the top border.</summary>
    public void DrawBox(Rect rect, Style style, string? title = null, Style? titleStyle = null, bool rounded = true)
    {
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        var (topLeft, topRight, bottomLeft, bottomRight) = rounded ? ("\u256d", "\u256e", "\u2570", "\u256f") : ("\u250c", "\u2510", "\u2514", "\u2518");

        Write(rect.Top, rect.Left, topLeft, style);
        Write(rect.Top, rect.Right - 1, topRight, style);
        Write(rect.Bottom - 1, rect.Left, bottomLeft, style);
        Write(rect.Bottom - 1, rect.Right - 1, bottomRight, style);

        for (var column = rect.Left + 1; column < rect.Right - 1; column++)
        {
            Write(rect.Top, column, "\u2500", style);
            Write(rect.Bottom - 1, column, "\u2500", style);
        }

        for (var row = rect.Top + 1; row < rect.Bottom - 1; row++)
        {
            Write(row, rect.Left, "\u2502", style);
            Write(row, rect.Right - 1, "\u2502", style);
        }

        if (!string.IsNullOrEmpty(title) && rect.Width > 6)
        {
            var label = " " + TextWidth.Clip(title, rect.Width - 6) + " ";
            Write(rect.Top, rect.Left + 2, label, titleStyle ?? style);
        }
    }

    /// <summary>Draws a vertical scrollbar in the given one-column rectangle.</summary>
    public void DrawScrollbar(Rect track, int totalLines, int visibleLines, int firstLine, Style style)
    {
        if (track.Height <= 0 || totalLines <= visibleLines)
        {
            return;
        }

        var thumbSize = Math.Max(1, (int)Math.Round((double)visibleLines / totalLines * track.Height));
        var maxFirst = Math.Max(1, totalLines - visibleLines);
        var thumbStart = (int)Math.Round((double)Math.Min(firstLine, maxFirst) / maxFirst * (track.Height - thumbSize));

        for (var row = 0; row < track.Height; row++)
        {
            var inThumb = row >= thumbStart && row < thumbStart + thumbSize;
            Write(track.Top + row, track.Left, inThumb ? "\u2588" : "\u2502", inThumb ? style : style.Dim());
        }
    }

    /// <summary>Renders every row to an ANSI string (cursor positioning included) for the terminal.</summary>
    public string[] RenderRows()
    {
        var rows = new string[Height];
        var builder = new StringBuilder(Width * 4);

        for (var row = 0; row < Height; row++)
        {
            builder.Clear();
            builder.Append("\u001b[").Append(row + 1).Append(";1H\u001b[0m");

            Style? current = null;
            for (var column = 0; column < Width; column++)
            {
                ref var cell = ref cells[row, column];
                if (cell.Text.Length == 0)
                {
                    // Tail of a wide character: the glyph before it already covers this cell.
                    continue;
                }

                if (current != cell.Style)
                {
                    AppendSgr(builder, cell.Style);
                    current = cell.Style;
                }

                builder.Append(cell.Text);
            }

            builder.Append("\u001b[0m");
            rows[row] = builder.ToString();
        }

        return rows;
    }

    /// <summary>Plain-text dump of the buffer, for tests and for debugging.</summary>
    public string ToText()
    {
        var builder = new StringBuilder(Width * Height + Height);
        for (var row = 0; row < Height; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                builder.Append(cells[row, column].Text);
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The text of one row, trimmed, for tests.</summary>
    public string RowText(int row)
    {
        var builder = new StringBuilder(Width);
        for (var column = 0; column < Width; column++)
        {
            builder.Append(cells[row, column].Text);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>The style of one cell, for tests.</summary>
    public Style StyleAt(int row, int column) => cells[row, column].Style;

    private Rect Clip(Rect rect)
    {
        var left = Math.Max(0, rect.Left);
        var top = Math.Max(0, rect.Top);
        var right = Math.Min(Width, rect.Right);
        var bottom = Math.Min(Height, rect.Bottom);
        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static void AppendSgr(StringBuilder builder, Style style)
    {
        builder.Append("\u001b[0");

        if (style.Attributes.HasFlag(TermAttr.Bold))
        {
            builder.Append(";1");
        }

        if (style.Attributes.HasFlag(TermAttr.Dim))
        {
            builder.Append(";2");
        }

        if (style.Attributes.HasFlag(TermAttr.Italic))
        {
            builder.Append(";3");
        }

        if (style.Attributes.HasFlag(TermAttr.Underline))
        {
            builder.Append(";4");
        }

        if (style.Attributes.HasFlag(TermAttr.Reverse))
        {
            builder.Append(";7");
        }

        if (style.Foreground != TermColor.Default)
        {
            var code = (int)style.Foreground - 1;
            builder.Append(';').Append(code < 8 ? 30 + code : 90 + (code - 8));
        }

        if (style.Background != TermColor.Default)
        {
            var code = (int)style.Background - 1;
            builder.Append(';').Append(code < 8 ? 40 + code : 100 + (code - 8));
        }

        builder.Append('m');
    }
}

/// <summary>A run of text in one style.</summary>
/// <param name="Text">The text.</param>
/// <param name="Style">Its style.</param>
public readonly record struct StyledSpan(string Text, Style Style);
