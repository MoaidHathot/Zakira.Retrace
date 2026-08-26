using System.Globalization;
using System.Text;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Cli;

/// <summary>
/// Renders results as text for a terminal.
/// </summary>
/// <remarks>
/// The consistent shape across list and search output is deliberate: one line identifying the
/// session, then indented detail. It keeps output greppable and lets a user scan a long result set
/// by the first column alone.
/// </remarks>
internal static class TextRenderer
{
    /// <summary>Renders a session list.</summary>
    public static void RenderSessions(TextWriter writer, IReadOnlyList<SessionSummary> sessions, int previewCharacters, bool relativeDates)
    {
        if (sessions.Count == 0)
        {
            writer.WriteLine(ConsoleStyle.Dim("No sessions matched."));
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var idWidth = sessions.Max(session => session.Ref.ShortForm.Length);
        var whenWidth = sessions.Max(session => FormatDate(session.UpdatedAt, now, relativeDates).Length);

        foreach (var session in sessions)
        {
            var id = ConsoleStyle.PadRightVisible(ConsoleStyle.Cyan(session.Ref.ShortForm), idWidth + 2);
            var when = ConsoleStyle.PadRightVisible(ConsoleStyle.Dim(FormatDate(session.UpdatedAt, now, relativeDates)), whenWidth + 2);
            var title = TextUtilities.Preview(session.Title, 72);

            writer.WriteLine($"{id}{when}{title}");

            var detail = BuildDetailLine(session);
            if (detail.Length > 0)
            {
                writer.WriteLine($"{new string(' ', idWidth + 2)}{ConsoleStyle.Dim(detail)}");
            }

            if (previewCharacters > 0 && !string.IsNullOrWhiteSpace(session.Preview))
            {
                var preview = TextUtilities.Preview(session.Preview, previewCharacters);
                if (!preview.Equals(session.Title, StringComparison.Ordinal))
                {
                    writer.WriteLine($"{new string(' ', idWidth + 2)}{ConsoleStyle.Dim(preview)}");
                }
            }
        }

        writer.WriteLine();
        writer.WriteLine(ConsoleStyle.Dim($"{sessions.Count} session(s)."));
    }

    /// <summary>Renders search results with their snippets.</summary>
    public static void RenderSearchHits(TextWriter writer, IReadOnlyList<SearchHit> hits, bool relativeDates, bool showScores)
    {
        if (hits.Count == 0)
        {
            writer.WriteLine(ConsoleStyle.Dim("No matches."));
            return;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var hit in hits)
        {
            var session = hit.Session;
            var header = new StringBuilder();
            header.Append(ConsoleStyle.Cyan(session.Ref.ShortForm));
            header.Append("  ");
            header.Append(ConsoleStyle.Dim(FormatDate(session.UpdatedAt, now, relativeDates)));
            header.Append("  ");
            header.Append(TextUtilities.Preview(session.Title, 68));

            if (showScores)
            {
                header.Append("  ");
                header.Append(ConsoleStyle.Magenta($"[{hit.Score:F4} lex={hit.LexicalScore:F4} sem={hit.SemanticScore:F4}]"));
            }

            writer.WriteLine(header.ToString());

            var detail = BuildDetailLine(session);
            if (detail.Length > 0)
            {
                writer.WriteLine($"  {ConsoleStyle.Dim(detail)}");
            }

            foreach (var snippet in hit.Snippets)
            {
                var text = snippet.Highlighted is null
                    ? TextUtilities.Preview(snippet.Text, 220)
                    : ConsoleStyle.RenderHighlights(TextUtilities.Flatten(snippet.Highlighted));

                writer.WriteLine($"    {ConsoleStyle.Dim(TextUtilities.RoleLabel(snippet.Role) + ":")} {text}");
            }

            writer.WriteLine();
        }

        writer.WriteLine(ConsoleStyle.Dim($"{hits.Count} match(es). Use `retrace show <id>` to read one."));
    }

    /// <summary>Renders a transcript.</summary>
    public static void RenderTranscript(TextWriter writer, SessionTranscript transcript, bool includeToolOutput, int maxToolOutput)
    {
        var summary = transcript.Summary;

        writer.WriteLine(ConsoleStyle.Bold(summary.Title));
        writer.WriteLine(ConsoleStyle.Cyan(summary.Ref.Uri));

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

        facts.Add(summary.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

        writer.WriteLine(ConsoleStyle.Dim(string.Join("  |  ", facts)));
        writer.WriteLine();

        foreach (var turn in transcript.Turns)
        {
            var role = turn.Role switch
            {
                TurnRole.User => ConsoleStyle.Green("user"),
                TurnRole.Assistant => ConsoleStyle.Cyan("assistant"),
                TurnRole.Tool => ConsoleStyle.Yellow("tool"),
                TurnRole.System => ConsoleStyle.Dim("system"),
                _ => ConsoleStyle.Dim("info")
            };

            var stamp = turn.Timestamp?.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var suffix = stamp is null ? string.Empty : "  " + ConsoleStyle.Dim(stamp);
            var model = turn.Model is null ? string.Empty : "  " + ConsoleStyle.Dim(turn.Model);

            writer.WriteLine($"{ConsoleStyle.Bold($"[{turn.Index}]")} {role}{model}{suffix}");

            foreach (var block in turn.Blocks)
            {
                RenderBlock(writer, block, includeToolOutput, maxToolOutput);
            }

            writer.WriteLine();
        }

        if (transcript.IsTruncated)
        {
            writer.WriteLine(ConsoleStyle.Yellow(
                $"Output truncated at turn {transcript.NextTurnIndex} of {transcript.TotalTurns}. "
                + $"Continue with --turns {transcript.NextTurnIndex}- or raise --max-chars."));
        }
    }

    private static void RenderBlock(TextWriter writer, ContentBlock block, bool includeToolOutput, int maxToolOutput)
    {
        switch (block)
        {
            case TextBlock text:
                writer.WriteLine(Indent(text.Text));
                break;

            case ReasoningBlock reasoning:
                writer.WriteLine(ConsoleStyle.Dim(Indent(reasoning.Text)));
                break;

            case ToolCallBlock tool:
            {
                var label = string.IsNullOrWhiteSpace(tool.Title) ? tool.ToolName : $"{tool.ToolName}: {tool.Title}";
                var status = tool.Status is null ? string.Empty : $" ({tool.Status})";
                writer.WriteLine($"  {ConsoleStyle.Yellow("→")} {label}{ConsoleStyle.Dim(status)}");

                if (includeToolOutput && !string.IsNullOrWhiteSpace(tool.Output))
                {
                    var output = maxToolOutput > 0 && tool.Output.Length > maxToolOutput
                        ? tool.Output[..maxToolOutput] + $"\n… [{tool.Output.Length - maxToolOutput:N0} more characters]"
                        : tool.Output;

                    writer.WriteLine(ConsoleStyle.Dim(Indent(output, "    ")));
                }

                break;
            }

            case PatchBlock patch:
            {
                var counts = patch.Additions > 0 || patch.Deletions > 0
                    ? $" (+{patch.Additions}/-{patch.Deletions})"
                    : string.Empty;
                writer.WriteLine($"  {ConsoleStyle.Magenta("±")} {patch.Path}{ConsoleStyle.Dim(counts)}");

                if (!string.IsNullOrWhiteSpace(patch.Diff))
                {
                    writer.WriteLine(ConsoleStyle.Dim(Indent(patch.Diff, "    ")));
                }

                break;
            }
        }
    }

    /// <summary>Renders a transcript as Markdown, for export.</summary>
    public static string RenderTranscriptMarkdown(SessionTranscript transcript, bool includeToolOutput)
    {
        var summary = transcript.Summary;
        var builder = new StringBuilder();

        builder.Append("# ").AppendLine(summary.Title);
        builder.AppendLine();

        // A YAML front-matter block makes an exported transcript useful to note tools such as
        // Obsidian without any post-processing.
        builder.AppendLine("```yaml");
        builder.Append("uri: ").AppendLine(summary.Ref.Uri);
        builder.Append("source: ").AppendLine(summary.Ref.SourceId);
        builder.Append("created: ").AppendLine(summary.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("updated: ").AppendLine(summary.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));

        if (summary.Workspace?.Path is { Length: > 0 } path)
        {
            builder.Append("workspace: ").AppendLine(path);
        }

        if (summary.Workspace?.Branch is { Length: > 0 } branch)
        {
            builder.Append("branch: ").AppendLine(branch);
        }

        if (summary.Agent is { Length: > 0 } agent)
        {
            builder.Append("agent: ").AppendLine(agent);
        }

        if (summary.Models.Count > 0)
        {
            builder.Append("models: [").Append(string.Join(", ", summary.Models)).AppendLine("]");
        }

        builder.AppendLine("```");
        builder.AppendLine();

        foreach (var turn in transcript.Turns)
        {
            var stamp = turn.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            builder.Append("## ").Append(TextUtilities.RoleLabel(turn.Role));

            if (turn.Model is { Length: > 0 })
            {
                builder.Append(" · ").Append(turn.Model);
            }

            if (stamp is not null)
            {
                builder.Append(" · ").Append(stamp);
            }

            builder.AppendLine();
            builder.AppendLine();

            foreach (var block in turn.Blocks)
            {
                switch (block)
                {
                    case TextBlock text:
                        builder.AppendLine(text.Text).AppendLine();
                        break;

                    case ReasoningBlock reasoning:
                        builder.AppendLine("> [!NOTE] Reasoning");
                        foreach (var line in reasoning.Text.Split('\n'))
                        {
                            builder.Append("> ").AppendLine(line.TrimEnd());
                        }

                        builder.AppendLine();
                        break;

                    case ToolCallBlock tool:
                        builder.Append("**Tool** `").Append(tool.ToolName).Append('`');
                        if (!string.IsNullOrWhiteSpace(tool.Title))
                        {
                            builder.Append(" — ").Append(tool.Title);
                        }

                        builder.AppendLine().AppendLine();

                        if (includeToolOutput && !string.IsNullOrWhiteSpace(tool.Output))
                        {
                            builder.AppendLine("```").AppendLine(tool.Output.TrimEnd()).AppendLine("```").AppendLine();
                        }

                        break;

                    case PatchBlock patch:
                        builder.Append("**Edit** `").Append(patch.Path).Append('`').AppendLine().AppendLine();
                        if (!string.IsNullOrWhiteSpace(patch.Diff))
                        {
                            builder.AppendLine("```diff").AppendLine(patch.Diff.TrimEnd()).AppendLine("```").AppendLine();
                        }

                        break;
                }
            }
        }

        if (transcript.Files.Count > 0)
        {
            builder.AppendLine("## Files touched").AppendLine();
            foreach (var file in transcript.Files)
            {
                builder.Append("- `").Append(file.Path).AppendLine("`");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string BuildDetailLine(SessionSummary session)
    {
        var parts = new List<string>();

        if (session.Workspace?.Label is { Length: > 0 } label && label != "(unknown)")
        {
            parts.Add(label);
        }

        if (session.Agent is { Length: > 0 } agent)
        {
            parts.Add(agent);
        }

        if (session.Models.Count > 0)
        {
            parts.Add(session.Models[0]);
        }

        if (session.Stats.MessageCount is { } messages and > 0)
        {
            parts.Add($"{messages} msg");
        }

        if (session.Stats.FilesChanged is { } files and > 0)
        {
            parts.Add($"{files} file(s) changed");
        }

        if (session.Tags.Count > 0)
        {
            parts.Add("#" + string.Join(" #", session.Tags));
        }

        return string.Join("  ·  ", parts);
    }

    private static string FormatDate(DateTimeOffset value, DateTimeOffset now, bool relative) =>
        relative
            ? TextUtilities.ToRelativeTime(value, now)
            : value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Indent(string value, string prefix = "  ") =>
        string.Join('\n', value.Split('\n').Select(line => prefix + line.TrimEnd()));
}
