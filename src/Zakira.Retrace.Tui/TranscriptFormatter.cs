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
/// <param name="RichText">Render Markdown, code syntax accents and diffs. False keeps literal prose.</param>
public sealed record TranscriptFormatOptions(
    int Width,
    bool ShowToolOutput,
    bool ShowReasoning,
    IReadOnlyList<string> HighlightTerms,
    int MaxToolOutputCharacters = 4000,
    bool RichText = true);

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
        var width = Math.Max(1, options.Width);
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
        Fact("updated", summary.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

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
            foreach (var wrapped in WrapSpans([new StyledSpan($"\u2026 {Math.Max(0, transcript.TotalTurns - (transcript.NextTurnIndex ?? 0)):N0} more turn(s). Scroll down to load them.", Theme.Warn)], width))
            {
                lines.Add(new TranscriptLine(wrapped, transcript.Turns.Count > 0 ? transcript.Turns[^1].Index : -1, false, false));
            }
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
                if (term.Length == 0)
                {
                    continue;
                }

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
        => StyledText.Wrap(spans, width);

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

        var firstHeaderLine = true;
        foreach (var wrapped in WrapSpans(head, width))
        {
            lines.Add(new TranscriptLine(wrapped, turn.Index, firstHeaderLine, false));
            firstHeaderLine = false;
        }

        var wroteAnything = false;

        foreach (var block in turn.Blocks)
        {
            switch (block)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    if (options.RichText)
                    {
                        AppendRich(lines, MarkdownFormatter.Format(text.Text, Math.Max(1, width - Rule.Length), Theme.Body, options.HighlightTerms), turn.Index, ruleStyle, width);
                    }
                    else
                    {
                        AppendParagraphs(lines, text.Text, Theme.Body, turn.Index, options, width, ruleStyle, preserveLines: true);
                    }

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

                    AppendWrapped(lines, spans, turn.Index, matched, width);

                    if (options.ShowToolOutput && !string.IsNullOrWhiteSpace(tool.InputJson))
                    {
                        AppendCode(lines, tool.InputJson, "json", turn.Index, options, width, ruleStyle);
                    }

                    if (options.ShowToolOutput && !string.IsNullOrWhiteSpace(tool.Output))
                    {
                        var output = options.MaxToolOutputCharacters > 0 && tool.Output.Length > options.MaxToolOutputCharacters
                            ? tool.Output[..options.MaxToolOutputCharacters] + $"\n\u2026 [{tool.Output.Length - options.MaxToolOutputCharacters:N0} more characters]"
                            : tool.Output;

                        AppendCode(lines, output, string.Empty, turn.Index, options, width, ruleStyle);
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

                    AppendWrapped(lines, spans, turn.Index, matched, width);
                    if (options.ShowToolOutput && !string.IsNullOrWhiteSpace(patch.Diff))
                    {
                        AppendCode(lines, patch.Diff, "diff", turn.Index, options, width, ruleStyle);
                    }

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
        var inner = Math.Max(1, width - Rule.Length - indent);

        foreach (var rawLine in MarkdownFormatter.Clean(text).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var source = preserveLines ? MarkdownFormatter.ExpandTabs(rawLine.TrimEnd()) : TextUtilities.Flatten(rawLine);

            if (source.Length == 0)
            {
                lines.Add(new TranscriptLine([new StyledSpan(Rule.TrimEnd(), ruleStyle)], turnIndex, false, false));
                continue;
            }

            var wrappedLines = preserveLines
                ? StyledText.Wrap([new StyledSpan(source, style)], inner, wordWrap: false).Select(line => string.Concat(line.Select(span => span.Text)))
                : TextWidth.Wrap(source, inner);
            foreach (var wrapped in wrappedLines)
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

    private static void AppendRich(List<TranscriptLine> lines, IEnumerable<IReadOnlyList<StyledSpan>> content, int turnIndex, Style ruleStyle, int width)
    {
        foreach (var spans in content)
        {
            var prefix = width > Rule.Length ? Rule : string.Empty;
            var matched = spans.Any(span => span.Style.Background == Theme.Match.Background);
            lines.Add(new TranscriptLine([new StyledSpan(prefix, ruleStyle), .. spans], turnIndex, false, matched));
        }
    }

    private static void AppendWrapped(List<TranscriptLine> lines, IReadOnlyList<StyledSpan> spans, int turnIndex, bool matched, int width)
    {
        foreach (var wrapped in WrapSpans(spans, width))
        {
            lines.Add(new TranscriptLine(wrapped, turnIndex, false, matched && wrapped.Any(span => span.Style.Background == Theme.Match.Background)));
        }
    }

    private static void AppendCode(List<TranscriptLine> lines, string text, string language, int turnIndex, TranscriptFormatOptions options, int width, Style ruleStyle)
    {
        if (options.MaxToolOutputCharacters > 0 && text.Length > options.MaxToolOutputCharacters)
        {
            text = text[..options.MaxToolOutputCharacters] + "\n… (output truncated)";
        }

        foreach (var line in MarkdownFormatter.Clean(text).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            AppendRich(lines, MarkdownFormatter.CodeLine(line, options.RichText ? language : string.Empty, Math.Max(1, width - Rule.Length), options.HighlightTerms), turnIndex, ruleStyle, width);
        }
    }

    private static TranscriptLine Header(string text, Style style, int width) =>
        new([new StyledSpan(TextWidth.Clip(TextUtilities.Flatten(text), width), style)], -1, false, false);

    private static TranscriptLine Blank(int turnIndex) => new([], turnIndex, false, false);
}
