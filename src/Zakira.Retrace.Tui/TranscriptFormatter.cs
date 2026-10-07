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
/// The layout mirrors <c>retrace show</c> — a header, then one block per turn with a role label —
/// so a session reads the same whether it is on screen in the browser or piped from the CLI.
/// Wrapping happens here rather than at draw time because the reader needs line counts to scroll
/// and to jump between turns and matches.
/// </remarks>
public static class TranscriptFormatter
{
    private static readonly Style HeaderTitle = Style.Plain.Bold();
    private static readonly Style HeaderUri = Style.Fg(TermColor.Cyan);
    private static readonly Style Meta = Style.Fg(TermColor.BrightBlack);
    private static readonly Style UserRole = Style.Fg(TermColor.Green).Bold();
    private static readonly Style AssistantRole = Style.Fg(TermColor.Cyan).Bold();
    private static readonly Style ToolRole = Style.Fg(TermColor.Yellow).Bold();
    private static readonly Style OtherRole = Style.Fg(TermColor.BrightBlack).Bold();
    private static readonly Style Body = Style.Plain;
    private static readonly Style Reasoning = Style.Fg(TermColor.BrightBlack).With(TermAttr.Italic);
    private static readonly Style ToolMarker = Style.Fg(TermColor.Yellow);
    private static readonly Style ToolOutput = Style.Fg(TermColor.BrightBlack);
    private static readonly Style PatchMarker = Style.Fg(TermColor.Magenta);
    private static readonly Style Match = new(TermColor.Black, TermColor.Yellow, TermAttr.Bold);

    /// <summary>Formats a whole transcript.</summary>
    public static IReadOnlyList<TranscriptLine> Format(SessionTranscript transcript, TranscriptFormatOptions options)
    {
        var lines = new List<TranscriptLine>();
        var width = Math.Max(10, options.Width);
        var summary = transcript.Summary;

        lines.Add(Header(summary.Title, HeaderTitle, width));
        lines.Add(Header(summary.Ref.Uri, HeaderUri, width));

        var facts = new List<string>();
        if (summary.Workspace?.Path is { Length: > 0 } path)
        {
            facts.Add(path);
        }

        if (summary.Workspace?.Branch is { Length: > 0 } branch)
        {
            facts.Add($"branch {branch}");
        }

        if (summary.Agent is { Length: > 0 } agent)
        {
            facts.Add($"agent {agent}");
        }

        if (summary.Models.Count > 0)
        {
            facts.Add(string.Join(", ", summary.Models));
        }

        facts.Add(summary.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        if (summary.Stats.MessageCount is { } messages and > 0)
        {
            facts.Add($"{messages} msg");
        }

        if (summary.Tags.Count > 0)
        {
            facts.Add("#" + string.Join(" #", summary.Tags));
        }

        foreach (var wrapped in TextWidth.Wrap(string.Join("  |  ", facts), width))
        {
            lines.Add(new TranscriptLine([new StyledSpan(wrapped, Meta)], -1, false, false));
        }

        lines.Add(Blank(-1));

        foreach (var turn in transcript.Turns)
        {
            AppendTurn(lines, turn, options, width);
        }

        if (transcript.IsTruncated)
        {
            lines.Add(new TranscriptLine(
                [new StyledSpan($"Transcript truncated at turn {transcript.NextTurnIndex} of {transcript.TotalTurns}. Scroll down to load more.", Style.Fg(TermColor.Yellow))],
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

            spans.Add(new StyledSpan(text.Substring(bestIndex, bestLength), Match));
            any = true;
            position = bestIndex + bestLength;
        }

        return (spans, any);
    }

    private static void AppendTurn(List<TranscriptLine> lines, Turn turn, TranscriptFormatOptions options, int width)
    {
        var (roleLabel, roleStyle) = turn.Role switch
        {
            TurnRole.User => ("user", UserRole),
            TurnRole.Assistant => ("assistant", AssistantRole),
            TurnRole.Tool => ("tool", ToolRole),
            TurnRole.System => ("system", OtherRole),
            _ => ("info", OtherRole)
        };

        var head = new List<StyledSpan>
        {
            new($"[{turn.Index}] ", Style.Plain.Bold()),
            new(roleLabel, roleStyle)
        };

        if (turn.Model is { Length: > 0 })
        {
            head.Add(new StyledSpan("  " + turn.Model, Meta));
        }

        if (turn.Timestamp is { } stamp)
        {
            head.Add(new StyledSpan("  " + stamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture), Meta));
        }

        lines.Add(new TranscriptLine(head, turn.Index, true, false));

        var wroteAnything = false;

        foreach (var block in turn.Blocks)
        {
            switch (block)
            {
                case TextBlock text when !string.IsNullOrWhiteSpace(text.Text):
                    AppendParagraphs(lines, text.Text, Body, turn.Index, options, width, indent: 2);
                    wroteAnything = true;
                    break;

                case ReasoningBlock reasoning when options.ShowReasoning && !string.IsNullOrWhiteSpace(reasoning.Text):
                    lines.Add(new TranscriptLine([new StyledSpan("  reasoning", Reasoning)], turn.Index, false, false));
                    AppendParagraphs(lines, reasoning.Text, Reasoning, turn.Index, options, width, indent: 4);
                    wroteAnything = true;
                    break;

                case ToolCallBlock tool:
                {
                    var label = string.IsNullOrWhiteSpace(tool.Title) ? tool.ToolName : $"{tool.ToolName}: {tool.Title}";
                    var status = tool.Status is { Length: > 0 } ? $" ({tool.Status})" : string.Empty;
                    var (labelSpans, matched) = Highlight(TextUtilities.Flatten(label), Body, options.HighlightTerms);
                    var spans = new List<StyledSpan> { new("  \u2192 ", ToolMarker) };
                    spans.AddRange(labelSpans);
                    if (status.Length > 0)
                    {
                        spans.Add(new StyledSpan(status, Meta));
                    }

                    lines.Add(new TranscriptLine(spans, turn.Index, false, matched));

                    if (options.ShowToolOutput && !string.IsNullOrWhiteSpace(tool.Output))
                    {
                        var output = options.MaxToolOutputCharacters > 0 && tool.Output.Length > options.MaxToolOutputCharacters
                            ? tool.Output[..options.MaxToolOutputCharacters] + $"\n\u2026 [{tool.Output.Length - options.MaxToolOutputCharacters:N0} more characters]"
                            : tool.Output;

                        AppendParagraphs(lines, output, ToolOutput, turn.Index, options, width, indent: 6, preserveLines: true);
                    }

                    wroteAnything = true;
                    break;
                }

                case PatchBlock patch:
                {
                    var counts = patch.Additions > 0 || patch.Deletions > 0 ? $" (+{patch.Additions}/-{patch.Deletions})" : string.Empty;
                    var (pathSpans, matched) = Highlight(patch.Path, Body, options.HighlightTerms);
                    var spans = new List<StyledSpan> { new("  \u00b1 ", PatchMarker) };
                    spans.AddRange(pathSpans);
                    if (counts.Length > 0)
                    {
                        spans.Add(new StyledSpan(counts, Meta));
                    }

                    lines.Add(new TranscriptLine(spans, turn.Index, false, matched));
                    wroteAnything = true;
                    break;
                }
            }
        }

        if (!wroteAnything)
        {
            lines.Add(new TranscriptLine([new StyledSpan("  (no text)", Meta)], turn.Index, false, false));
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
        int indent,
        bool preserveLines = false)
    {
        var prefix = new string(' ', indent);
        var inner = Math.Max(4, width - indent);

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var source = preserveLines ? rawLine.TrimEnd().Replace('\t', ' ') : TextUtilities.Flatten(rawLine);

            if (source.Length == 0)
            {
                lines.Add(Blank(turnIndex));
                continue;
            }

            foreach (var wrapped in TextWidth.Wrap(source, inner))
            {
                var (spans, matched) = Highlight(wrapped, style, options.HighlightTerms);
                var withPrefix = new List<StyledSpan>(spans.Count + 1) { new(prefix, Style.Plain) };
                withPrefix.AddRange(spans);
                lines.Add(new TranscriptLine(withPrefix, turnIndex, false, matched));
            }
        }
    }

    private static TranscriptLine Header(string text, Style style, int width) =>
        new([new StyledSpan(TextWidth.Clip(TextUtilities.Flatten(text), width), style)], -1, false, false);

    private static TranscriptLine Blank(int turnIndex) => new([], turnIndex, false, false);
}
