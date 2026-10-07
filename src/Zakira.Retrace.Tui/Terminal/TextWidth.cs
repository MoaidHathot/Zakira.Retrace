using System.Globalization;
using System.Text;

namespace Zakira.Retrace.Tui.Terminal;

/// <summary>
/// Display-column arithmetic for terminal text.
/// </summary>
/// <remarks>
/// A terminal cell is not a <see cref="char"/>. CJK ideographs and most emoji occupy two cells,
/// combining marks occupy none, and a single emoji can be several UTF-16 code units. Everything
/// that aligns columns or clips to a pane width has to go through here, or the first session
/// title containing an emoji shifts every column after it.
/// </remarks>
public static class TextWidth
{
    /// <summary>Display width of a single code point.</summary>
    public static int Of(Rune rune)
    {
        var value = rune.Value;

        if (value < 0x20 || value == 0x7f)
        {
            return 0;
        }

        if (value < 0x300)
        {
            return 1;
        }

        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return 0;
        }

        // Zero-width joiner and variation selectors glue emoji sequences together.
        if (value == 0x200d || value is >= 0xfe00 and <= 0xfe0f || value is >= 0xe0100 and <= 0xe01ef)
        {
            return 0;
        }

        return IsWide(value) ? 2 : 1;
    }

    /// <summary>Display width of a string.</summary>
    public static int Of(ReadOnlySpan<char> text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += Of(rune);
        }

        return width;
    }

    /// <summary>Display width of a string.</summary>
    public static int Of(string? text) => text is null ? 0 : Of(text.AsSpan());

    /// <summary>
    /// Returns the longest prefix that fits in <paramref name="maxWidth"/> columns, appending an
    /// ellipsis when anything was cut and there is room for one.
    /// </summary>
    public static string Clip(string? text, int maxWidth, bool ellipsis = true)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 0)
        {
            return string.Empty;
        }

        if (Of(text) <= maxWidth)
        {
            return text;
        }

        var budget = ellipsis && maxWidth > 1 ? maxWidth - 1 : maxWidth;
        var builder = new StringBuilder(text.Length);
        var width = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = Of(rune);
            if (width + runeWidth > budget)
            {
                break;
            }

            builder.Append(rune.ToString());
            width += runeWidth;
        }

        if (ellipsis && maxWidth > 1)
        {
            builder.Append('\u2026');
        }

        return builder.ToString();
    }

    /// <summary>Pads with spaces on the right to exactly <paramref name="width"/> columns, clipping when longer.</summary>
    public static string Fit(string? text, int width)
    {
        var clipped = Clip(text, width);
        var padding = width - Of(clipped);
        return padding > 0 ? clipped + new string(' ', padding) : clipped;
    }

    /// <summary>
    /// Word-wraps a single paragraph to a column width. Lines never exceed the width; a word longer
    /// than the width is split at a cell boundary.
    /// </summary>
    public static IEnumerable<string> Wrap(string paragraph, int width)
    {
        if (width <= 0)
        {
            yield break;
        }

        if (paragraph.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }

        var line = new StringBuilder();
        var lineWidth = 0;

        foreach (var word in paragraph.Split(' '))
        {
            var wordWidth = Of(word);

            if (lineWidth == 0)
            {
                if (wordWidth <= width)
                {
                    line.Append(word);
                    lineWidth = wordWidth;
                    continue;
                }

                foreach (var piece in HardSplit(word, width))
                {
                    if (Of(piece) == width)
                    {
                        yield return piece;
                    }
                    else
                    {
                        line.Append(piece);
                        lineWidth = Of(piece);
                    }
                }

                continue;
            }

            if (lineWidth + 1 + wordWidth <= width)
            {
                line.Append(' ').Append(word);
                lineWidth += 1 + wordWidth;
                continue;
            }

            yield return line.ToString();
            line.Clear();
            lineWidth = 0;

            if (wordWidth <= width)
            {
                line.Append(word);
                lineWidth = wordWidth;
                continue;
            }

            foreach (var piece in HardSplit(word, width))
            {
                if (Of(piece) == width)
                {
                    yield return piece;
                }
                else
                {
                    line.Append(piece);
                    lineWidth = Of(piece);
                }
            }
        }

        if (lineWidth > 0 || line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static IEnumerable<string> HardSplit(string word, int width)
    {
        var builder = new StringBuilder();
        var current = 0;

        foreach (var rune in word.EnumerateRunes())
        {
            var runeWidth = Of(rune);
            if (current + runeWidth > width && builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
                current = 0;
            }

            builder.Append(rune.ToString());
            current += runeWidth;
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    /// <summary>East Asian Wide / Fullwidth ranges plus the emoji blocks terminals render double-width.</summary>
    private static bool IsWide(int value) =>
        value is >= 0x1100 and <= 0x115f
            or 0x2329 or 0x232a
            or >= 0x231a and <= 0x231b
            or >= 0x23e9 and <= 0x23ec
            or 0x23f0 or 0x23f3
            or >= 0x25fd and <= 0x25fe
            or >= 0x2614 and <= 0x2615
            or >= 0x2648 and <= 0x2653
            or 0x267f or 0x2693 or 0x26a1
            or >= 0x26aa and <= 0x26ab
            or >= 0x26bd and <= 0x26be
            or >= 0x26c4 and <= 0x26c5
            or 0x26ce or 0x26d4 or 0x26ea
            or >= 0x26f2 and <= 0x26f3
            or 0x26f5 or 0x26fa or 0x26fd or 0x2705
            or >= 0x270a and <= 0x270b
            or 0x2728 or 0x274c or 0x274e
            or >= 0x2753 and <= 0x2755
            or 0x2757
            or >= 0x2795 and <= 0x2797
            or 0x27b0 or 0x27bf
            or >= 0x2b1b and <= 0x2b1c
            or 0x2b50 or 0x2b55
            or >= 0x2e80 and <= 0x303e
            or >= 0x3041 and <= 0x33ff
            or >= 0x3400 and <= 0x4dbf
            or >= 0x4e00 and <= 0x9fff
            or >= 0xa000 and <= 0xa4cf
            or >= 0xa960 and <= 0xa97f
            or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff
            or >= 0xfe10 and <= 0xfe19
            or >= 0xfe30 and <= 0xfe6f
            or >= 0xff00 and <= 0xff60
            or >= 0xffe0 and <= 0xffe6
            or >= 0x16fe0 and <= 0x16fe4
            or >= 0x17000 and <= 0x18aff
            or >= 0x1b000 and <= 0x1b16f
            or 0x1f004 or 0x1f0cf or 0x1f18e
            or >= 0x1f191 and <= 0x1f19a
            or >= 0x1f200 and <= 0x1f251
            or >= 0x1f300 and <= 0x1f64f
            or >= 0x1f680 and <= 0x1f6ff
            or >= 0x1f7e0 and <= 0x1f7eb
            or >= 0x1f90c and <= 0x1f9ff
            or >= 0x1fa70 and <= 0x1faff
            or >= 0x20000 and <= 0x3fffd;
}
