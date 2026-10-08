using System.Globalization;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Text;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

/// <summary>One rendered line of a transcript: styled spans plus where it came from.</summary>
/// <param name="Spans">The text, in order.</param>
/// <param name="TurnIndex">Turn this line belongs to, or -1 for the header.</param>
/// <param name="IsTurnStart">Whether this line is the first of its turn, for jumping between turns.</param>
/// <param name="HasMatch">Whether a search term was highlighted on this line.</param>
public sealed record TranscriptLine(IReadOnlyList<StyledSpan> Spans, int TurnIndex, bool IsTurnStart, bool HasMatch);

/// <summary>What the formatter should include and highlight.</summary>
/// <param name="Width">Column width to wrap to.</param>
/// <param name="ShowToolOutput">Include captured tool output under each tool call.</param>
/// <param name="ShowReasoning">Include reasoning blocks.</param>
/// <param name="HighlightTerms">Search terms to highlight, case-insensitively.</param>
/// <param name="MaxToolOutputCharacters">Truncate each tool output to this many characters; 0 means unbounded.</param>
public sealed record TranscriptFormatOptions(
    int Width,
    bool ShowToolOutput,
    bool ShowReasoning,
    IReadOnlyList<string> HighlightTerms,
    int MaxToolOutputCharacters = 4000);

/// <summary>
/// Lays a transcript out as terminal lines.
/// </summary>
/// <remarks>
/// Each turn opens with a coloured dot and the role, and every line of its body hangs off a thin
/// rule in the same colour, so the eye can follow who said what down a long conversation without
/// reading the labels. Wrapping happens here rather than at draw time because the reader needs
/// line counts to scroll and to jump between turns and matches.
/// </remarks>
public static class TranscriptFormatter
{
    private const string Rule = "\u258f ";

    /// <summary>Formats a whole transcript.</summary>
    public static IReadOnlyList<TranscriptLine> Format(SessionTranscript transcript, TranscriptFormatOptions options)
    {
        var lines = new List<TranscriptLine>();
        var width = Math.Max(10, options.Width);
        var summary = transcript.Summary;

        lines.Add(Header(summary.Title, Theme.Heading, width));
        lines.Add(Header(summary.Ref.Uri, Theme.Uri, width));

        var facts = new List<StyledSpan>();
        void Fact(string label, string value)
        {
            if (facts.Count > 0)
            {
                facts.Add(new StyledSpan("  \u00b7  ", Theme.TranscriptMeta));
            }

            facts.Add(new StyledSpan(label + " ", Theme.TranscriptMeta));
            facts.Add(new StyledSpan(value, Theme.TranscriptMetaValue));
        }

        if (summary.Workspace?.Path is { Length: > 0 } path)
        {
            Fact("dir", path);
        }

        if (summary.Workspace?.Branch is { Length: > 0 } branch)
        {
            Fact("branch", branch);
        }

        if (summary.Agent is { Length: > 0 } agent)
        {
            Fact("agent", agent);
        }

        if (summary.Models.Count > 0)
        {
            Fact("model", string.Join(", ", summary.Models));
        }

        Fact("created", summary.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        if (summary.Stats.MessageCount is { } messages and > 0)
        {
            Fact("messages", messages.ToString("N0", CultureInfo.InvariantCulture));
        }

        if (summary.Tags.Count > 0)
        {
            Fact("tags", string.Join(" ", summary.Tags.Select(tag => "#" + tag)));
        }

        foreach (var wrapped in WrapSpans(facts, width))
        {
            lines.Add(new TranscriptLine(wrapped, -1, false, false));
        }

        lines.Add(Blank(-1));

        foreach (var turn in transcript.Turns)
        {
            AppendTurn(lines, turn, options, width);
        }

        if (transcript.IsTruncated)
        {
            lines.Add(new TranscriptLine(
                [new StyledSpan($"\u2026 {transcript.TotalTurns - (transcript.NextTurnIndex ?? 0):N0} more turn(s). Scroll down to load them.", Theme.Warn)],
                transcript.Turns.Count > 0 ? transcript.Turns[^1].Index : -1,
                false,
                false));
        }

        return lines;
    }

    /// <summary>Splits a search query into highlightable terms, dropping quotes and tiny tokens.</summary>
    public static IReadOnlyList<string> TermsOf(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        return [.. query
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim('"', '\'', '*', '(', ')', ','))
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Breaks text into spans, highlighting every occurrence of the terms.</summary>
    public static (IReadOnlyList<StyledSpan> Spans, bool HasMatch) Highlight(string text, Style style, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || text.Length == 0)
        {
            return ([new StyledSpan(text, style)], false);
        }

        var spans = new List<StyledSpan>();
        var position = 0;
        var any = false;

        while (position < text.Length)
        {
            var bestIndex = -1;
            var bestLength = 0;

            foreach (var term in terms)
            {
                var index = text.IndexOf(term, position, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (bestIndex < 0 || index < bestIndex || (index == bestIndex && term.Length > bestLength)))
                {
                    bestIndex = index;
                    bestLength = term.Length;
                }
            }

            if (bestIndex < 0)
            {
                spans.Add(new StyledSpan(text[position..], style));
                break;
            }

            if (bestIndex > position)
            {
                spans.Add(new StyledSpan(text[position..bestIndex], style));
            }

            spans.Add(new StyledSpan(text.Substring(bestIndex, bestLength), Theme.Match));
            any = true;
            position = bestIndex + bestLength;
        }

        return (spans, any);
    }

    /// <summary>Word-wraps a run of spans to a width, keeping each word's style.</summary>
    public static IEnumerable<IReadOnlyList<StyledSpan>> WrapSpans(IReadOnlyList<StyledSpan> spans, int width)
    {
        var line = new List<StyledSpan>();
        var used = 0;

        foreach (var span in spans)
        {
            var words = span.Text.Split(' ');
            for (var index = 0; index < words.Length; index++)
            {
                var word = words[index] + (index < words.Length - 1 ? " " : string.Empty);
                var wordWidth = TextWidth.Of(word);
                if (wordWidth == 0)
                {
                    continue;
                }

                if (used > 0 && used + TextWidth.Of(word.TrimEnd()) > width)
                {
                    yield return TrimEnd(line);
                    line = [];
                    used = 0;
                }

                line.Add(new StyledSpan(word, span.Style));
                used += wordWidth;
            }
        }

        if (line.Count > 0)
        {
            yield return TrimEnd(line);
        }
    }

    private static List<StyledSpan> TrimEnd(List<StyledSpan> line)
    {
        if (line.Count > 0)
        {
            var last = line[^1];
            line[^1] = last with { Text = last.Text.TrimEnd() };
        }

        return line;
    }

    private static void AppendTurn(List<TranscriptLine> lines, Turn turn, TranscriptFormatOptions options, int width)
    {
        var roleColor = Theme.RoleColor(turn.Role);
        var roleStyle = Style.Fg(roleColor).Bold();
        var ruleStyle = Style.Fg(roleColor).Dim();

        var head = new List<StyledSpan>
        {
            new("\u25cf ", Style.Fg(roleColor)),
            new(TextUtilities.RoleLabel(turn.Role), roleStyle)
        };

        if (turn.Model is { Length: > 0 })
        {
            head.Add(new StyledSpan("  " + turn.Model, Theme.TranscriptMeta));
        }

        if (turn.Timestamp is { } stamp)
        {
            head.Add(new StyledSpan("  " + stamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Theme.TranscriptMeta));
        }

        head.Add(new StyledSpan($"  #{turn.Index}", Style.Fg(Theme.Faint)));

        lines.Add(new TranscriptLine(head, turn.Index, true, false));

        var wroteAnything = false;

        foreach (var block in turn.Blocks)
        {
            switch (block)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    AppendParagraphs(lines, text.Text, Theme.Body, turn.Index, options, width, ruleStyle);
                    wroteAnything = true;
                    break;

                case ReasoningBlock reasoning when options.ShowReasoning && !string.IsNullOrWhiteSpace(reasoning.Text):
                    lines.Add(new TranscriptLine([new StyledSpan(Rule, ruleStyle), new StyledSpan("reasoning", Theme.Reasoning.Bold())], turn.Index, false, false));
                    AppendParagraphs(lines, reasoning.Text, Theme.Reasoning, turn.Index, options, width, ruleStyle, indent: 2);
                    wroteAnything = true;
                    break;

                case ToolCallBlock tool:
                {
                    var label = string.IsNullOrWhiteSpace(tool.Title) ? tool.ToolName : $"{tool.ToolName}: {tool.Title}";
                    var status = tool.Status is { Length: > 0 } ? $"  {tool.Status}" : string.Empty;
                    var (labelSpans, matched) = Highlight(TextUtilities.Flatten(label), Theme.Secondary, options.HighlightTerms);
                    var spans = new List<StyledSpan> { new(Rule, ruleStyle), new("\u2192 ", Theme.ToolMarker) };
                    spans.AddRange(labelSpans);
                    if (status.Length > 0)
                    {
                        spans.Add(new StyledSpan(status, Theme.TranscriptMeta));
                    }

                    lines.Add(new TranscriptLine(spans, turn.Index, false, matched));

                    if (options.ShowToolOutput && !string.IsNullOrWhiteSpace(tool.Output))
                    {
                        var output = options.MaxToolOutputCharacters > 0 && tool.Output.Length > options.MaxToolOutputCharacters
                            ? tool.Output[..options.MaxToolOutputCharacters] + $"\n\u2026 [{tool.Output.Length - options.MaxToolOutputCharacters:N0} more characters]"
                            : tool.Output;

                        AppendParagraphs(lines, output, Theme.ToolOutput, turn.Index, options, width, ruleStyle, indent: 4, preserveLines: true);
                    }

                    wroteAnything = true;
                    break;
                }

                case PatchBlock patch:
                {
                    var counts = patch.Additions > 0 || patch.Deletions > 0 ? $"  +{patch.Additions} \u2212{patch.Deletions}" : string.Empty;
                    var (pathSpans, matched) = Highlight(patch.Path, Theme.Secondary, options.HighlightTerms);
                    var spans = new List<StyledSpan> { new(Rule, ruleStyle), new("\u00b1 ", Theme.PatchMarker) };
                    spans.AddRange(pathSpans);
                    if (counts.Length > 0)
                    {
                        spans.Add(new StyledSpan(counts, Theme.TranscriptMeta));
                    }

                    lines.Add(new TranscriptLine(spans, turn.Index, false, matched));
                    wroteAnything = true;
                    break;
                }
            }
        }

        if (!wroteAnything)
        {
            lines.Add(new TranscriptLine([new StyledSpan(Rule, ruleStyle), new StyledSpan("(no text)", Theme.Dim)], turn.Index, false, false));
        }

        lines.Add(Blank(turn.Index));
    }

    private static void AppendParagraphs(
        List<TranscriptLine> lines,
        string text,
        Style style,
        int turnIndex,
        TranscriptFormatOptions options,
        int width,
        Style ruleStyle,
        int indent = 0,
        bool preserveLines = false)
    {
        var prefix = indent > 0 ? new string(' ', indent) : string.Empty;
        var inner = Math.Max(4, width - Rule.Length - indent);

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var source = preserveLines ? rawLine.TrimEnd().Replace('\t', ' ') : TextUtilities.Flatten(rawLine);

            if (source.Length == 0)
            {
                lines.Add(new TranscriptLine([new StyledSpan(Rule.TrimEnd(), ruleStyle)], turnIndex, false, false));
                continue;
            }

            foreach (var wrapped in TextWidth.Wrap(source, inner))
            {
                var (spans, matched) = Highlight(wrapped, style, options.HighlightTerms);
                var withPrefix = new List<StyledSpan>(spans.Count + 2) { new(Rule, ruleStyle) };
                if (prefix.Length > 0)
                {
                    withPrefix.Add(new StyledSpan(prefix, Style.Plain));
                }

                withPrefix.AddRange(spans);
                lines.Add(new TranscriptLine(withPrefix, turnIndex, false, matched));
            }
        }
    }

    private static TranscriptLine Header(string text, Style style, int width) =>
        new([new StyledSpan(TextWidth.Clip(TextUtilities.Flatten(text), width), style)], -1, false, false);

    private static TranscriptLine Blank(int turnIndex) => new([], turnIndex, false, false);
}
