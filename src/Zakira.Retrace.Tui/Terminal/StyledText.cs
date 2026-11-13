using System.Text;

namespace Zakira.Retrace.Tui.Terminal;

/// <summary>Column-aware wrapping that preserves styles, long words, and code indentation.</summary>
public static class StyledText
{
    private sealed record Glyph(string Text, int Width, Style Style, bool Space);

    /// <summary>Wraps a single line of spans. Code uses hard wrapping to preserve every space.</summary>
    public static IEnumerable<IReadOnlyList<StyledSpan>> Wrap(IEnumerable<StyledSpan> spans, int width, bool wordWrap = true)
    {
        if (width <= 0)
        {
            yield break;
        }

        var glyphs = new List<Glyph>();
        foreach (var span in spans)
        {
            foreach (var rune in span.Text.EnumerateRunes())
            {
                if (Rune.IsControl(rune))
                {
                    continue;
                }

                var cells = TextWidth.Of(rune);
                if (cells == 0 && glyphs.Count > 0)
                {
                    glyphs[^1] = glyphs[^1] with { Text = glyphs[^1].Text + rune };
                }
                else if (cells > 0)
                {
                    // A two-column glyph cannot fit a one-column pane. Use one replacement cell
                    // rather than overflow into another pane or loop forever trying to wrap it.
                    glyphs.Add(new Glyph(cells > width ? "�" : rune.ToString(), Math.Min(cells, width), span.Style, Rune.IsWhiteSpace(rune)));
                }
            }
        }

        if (glyphs.Count == 0)
        {
            yield return [];
            yield break;
        }

        var start = 0;
        while (start < glyphs.Count)
        {
            var end = start;
            var used = 0;
            var lastSpace = -1;
            while (end < glyphs.Count && used + glyphs[end].Width <= width)
            {
                used += glyphs[end].Width;
                if (glyphs[end].Space)
                {
                    lastSpace = end;
                }

                end++;
            }

            var next = end;
            if (wordWrap && end < glyphs.Count && !glyphs[end].Space && lastSpace > start)
            {
                end = lastSpace;
                next = lastSpace + 1;
            }

            if (wordWrap)
            {
                while (end > start && glyphs[end - 1].Space)
                {
                    end--;
                }

                while (next < glyphs.Count && glyphs[next].Space)
                {
                    next++;
                }
            }

            yield return Combine(glyphs, start, end);
            start = next;
        }
    }

    private static List<StyledSpan> Combine(List<Glyph> glyphs, int start, int end)
    {
        var result = new List<StyledSpan>();
        var text = new StringBuilder();
        Style? style = null;
        for (var index = start; index < end; index++)
        {
            var glyph = glyphs[index];
            if (style is { } previous && previous != glyph.Style)
            {
                result.Add(new StyledSpan(text.ToString(), previous));
                text.Clear();
            }

            style = glyph.Style;
            text.Append(glyph.Text);
        }

        if (style is { } last)
        {
            result.Add(new StyledSpan(text.ToString(), last));
        }

        return result;
    }
}
