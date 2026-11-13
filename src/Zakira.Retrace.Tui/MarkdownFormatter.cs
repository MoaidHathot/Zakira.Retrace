using System.Text;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

/// <summary>
/// Terminal rendering of the Markdown common in coding conversations: headings, emphasis, links,
/// lists, quotes, tables and fenced code. Unrecognised markup stays readable as literal text.
/// </summary>
internal static class MarkdownFormatter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "abstract", "async", "await", "bool", "break", "case", "catch", "class", "const", "continue",
        "def", "default", "delete", "do", "else", "enum", "export", "extends", "false", "finally",
        "float", "for", "foreach", "from", "function", "if", "import", "in", "int", "interface", "is",
        "let", "namespace", "new", "null", "override", "private", "protected", "public", "readonly",
        "record", "return", "sealed", "select", "static", "string", "struct", "switch", "throw",
        "true", "try", "using", "var", "void", "where", "while", "yield", "and", "or", "not", "None",
        "as", "with", "fn", "func", "package", "use", "impl", "match", "mut", "pub"
    };

    public static IEnumerable<IReadOnlyList<StyledSpan>> Format(string text, int width, Style body, IReadOnlyList<string> terms)
    {
        width = Math.Max(1, width);
        var lines = Clean(text).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            var fence = Fence(trimmed);
            if (fence >= 3)
            {
                var marker = trimmed[0];
                var language = trimmed[fence..].Trim().Split(' ')[0].ToLowerInvariant();
                yield return [new StyledSpan(TextWidth.Clip("┌─ " + (language.Length > 0 ? language : "code"), width), Theme.CodeLabel)];
                while (++index < lines.Length)
                {
                    var closing = lines[index].Trim();
                    if (closing.Length >= fence && closing[0] == marker && Fence(closing) >= fence && closing.Trim(marker).Length == 0)
                    {
                        break;
                    }

                    foreach (var line in CodeLine(lines[index], language, width, terms))
                    {
                        yield return line;
                    }
                }

                yield return [new StyledSpan(TextWidth.Clip("└─", width), Theme.CodeLabel)];
                continue;
            }

            if (trimmed.Contains('|') && index + 1 < lines.Length && IsTableRule(lines[index + 1]))
            {
                var table = new List<string[]> { Cells(trimmed) };
                index += 2;
                while (index < lines.Length && lines[index].Contains('|') && !string.IsNullOrWhiteSpace(lines[index]))
                {
                    table.Add(Cells(lines[index++]));
                }

                index--;
                foreach (var line in Table(table, width, body, terms))
                {
                    yield return line;
                }

                continue;
            }

            if (trimmed.Length == 0)
            {
                yield return [];
                continue;
            }

            var heading = 0;
            while (heading < trimmed.Length && trimmed[heading] == '#')
            {
                heading++;
            }

            if (heading is > 0 and <= 6 && heading < trimmed.Length && trimmed[heading] == ' ')
            {
                foreach (var line in Paragraph(trimmed[(heading + 1)..].TrimEnd(' ', '#'), width, Theme.MarkdownHeading, terms))
                {
                    yield return line;
                }

                continue;
            }

            if (index + 1 < lines.Length && (IsRule(lines[index + 1].Trim(), '=') || IsRule(lines[index + 1].Trim(), '-')))
            {
                index++;
                foreach (var line in Paragraph(trimmed, width, Theme.MarkdownHeading, terms))
                {
                    yield return line;
                }

                continue;
            }

            if (IsRule(trimmed, '-') || IsRule(trimmed, '*') || IsRule(trimmed, '_'))
            {
                yield return [new StyledSpan(new string('─', width), Theme.Border)];
                continue;
            }

            var indent = Math.Min(raw.Length - raw.TrimStart().Length, Math.Max(0, width / 3));
            var prefix = new string(' ', indent);
            var style = body;
            var content = trimmed;
            if (trimmed.StartsWith('>'))
            {
                prefix += "│ ";
                content = trimmed[1..].TrimStart();
                style = Theme.Quote;
            }
            else if (trimmed.Length > 1 && trimmed[0] is '-' or '*' or '+' && trimmed[1] == ' ')
            {
                prefix += "• ";
                content = trimmed[2..].TrimStart();
            }
            else
            {
                var numberEnd = 0;
                while (numberEnd < trimmed.Length && char.IsDigit(trimmed[numberEnd]))
                {
                    numberEnd++;
                }

                if (numberEnd > 0 && numberEnd + 1 < trimmed.Length && trimmed[numberEnd] is '.' or ')' && trimmed[numberEnd + 1] == ' ')
                {
                    prefix += trimmed[..(numberEnd + 1)] + " ";
                    content = trimmed[(numberEnd + 2)..];
                }
            }

            foreach (var line in Paragraph(content, width, style, terms, prefix))
            {
                yield return line;
            }
        }
    }

    public static IEnumerable<IReadOnlyList<StyledSpan>> CodeLine(string text, string language, int width, IReadOnlyList<string> terms)
    {
        var spans = Highlight(Syntax(ExpandTabs(Clean(text)), language), terms);
        var prefix = width > 2 ? "│ " : string.Empty;
        foreach (var wrapped in StyledText.Wrap(spans, Math.Max(1, width - prefix.Length), wordWrap: false))
        {
            yield return [new StyledSpan(prefix, Theme.CodeLabel), .. wrapped];
        }
    }

    private static IEnumerable<IReadOnlyList<StyledSpan>> Paragraph(string text, int width, Style style, IReadOnlyList<string> terms, string prefix = "")
    {
        prefix = TextWidth.Clip(prefix, Math.Max(0, width - 1), ellipsis: false);
        var indent = TextWidth.Of(prefix);
        var first = true;
        foreach (var wrapped in StyledText.Wrap(Highlight(Inline(text, style), terms), Math.Max(1, width - indent)))
        {
            yield return [new StyledSpan(first ? prefix : new string(' ', indent), style), .. wrapped];
            first = false;
        }
    }

    private static List<StyledSpan> Highlight(IEnumerable<StyledSpan> spans, IReadOnlyList<string> terms)
    {
        var result = new List<StyledSpan>();
        foreach (var span in spans)
        {
            result.AddRange(TranscriptFormatter.Highlight(span.Text, span.Style, terms).Spans);
        }

        return result;
    }

    private static IEnumerable<StyledSpan> Inline(string text, Style style, int depth = 0)
    {
        if (depth >= 12)
        {
            yield return new StyledSpan(text, style);
            yield break;
        }

        var plain = new StringBuilder();
        var position = 0;
        var nextLinkEnd = text.IndexOf("](", StringComparison.Ordinal);
        while (position < text.Length)
        {
            if (text[position] == '\\' && position + 1 < text.Length && "\\`*_{}[]()#+-.!|>".Contains(text[position + 1]))
            {
                plain.Append(text[position + 1]);
                position += 2;
                continue;
            }

            string? delimiter = null;
            var special = style;
            if (text[position] == '`')
            {
                var count = 1;
                while (position + count < text.Length && text[position + count] == '`')
                {
                    count++;
                }

                delimiter = new string('`', count);
                special = Theme.InlineCode;
            }
            else if (position + 1 < text.Length && text[position] is '*' or '_' && !char.IsWhiteSpace(text[position + 1])
                     && (text[position] != '_' || position == 0 || !char.IsLetterOrDigit(text[position - 1])))
            {
                var count = text[position + 1] == text[position] ? 2 : 1;
                if (count == 2 && position + 2 < text.Length && text[position + 2] == text[position])
                {
                    count = 3;
                }

                delimiter = new string(text[position], count);
                special = count == 3 ? style.Bold().Italic() : count == 2 ? style.Bold() : style.Italic();
            }

            if (delimiter is not null)
            {
                var end = text.IndexOf(delimiter, position + delimiter.Length, StringComparison.Ordinal);
                if (end > position + delimiter.Length)
                {
                    if (plain.Length > 0)
                    {
                        yield return new StyledSpan(plain.ToString(), style);
                        plain.Clear();
                    }

                    var content = text.Substring(position + delimiter.Length, end - position - delimiter.Length);
                    if (delimiter[0] == '`')
                    {
                        yield return new StyledSpan(content, special);
                    }
                    else
                    {
                        foreach (var span in Inline(content, special, depth + 1))
                        {
                            yield return span;
                        }
                    }

                    position = end + delimiter.Length;
                    continue;
                }

                // Consume an unmatched run together. Advancing one character at a time through
                // thousands of backticks would allocate successively shorter delimiter strings.
                plain.Append(delimiter);
                position += delimiter.Length;
                continue;
            }

            if (text[position] == '[' && nextLinkEnd >= 0)
            {
                if (nextLinkEnd < position)
                {
                    nextLinkEnd = text.IndexOf("](", position, StringComparison.Ordinal);
                }

                var labelEnd = nextLinkEnd;
                var urlEnd = labelEnd < 0 ? -1 : text.IndexOf(')', labelEnd + 2);
                if (labelEnd > position && urlEnd > labelEnd + 2)
                {
                    if (plain.Length > 0)
                    {
                        yield return new StyledSpan(plain.ToString(), style);
                        plain.Clear();
                    }

                    yield return new StyledSpan(text[(position + 1)..labelEnd], Theme.Link);
                    yield return new StyledSpan(" (" + text[(labelEnd + 2)..urlEnd] + ")", Theme.Uri);
                    position = urlEnd + 1;
                    continue;
                }
            }

            plain.Append(text[position++]);
        }

        if (plain.Length > 0)
        {
            yield return new StyledSpan(plain.ToString(), style);
        }
    }

    private static IEnumerable<StyledSpan> Syntax(string text, string language)
    {
        if (language.Equals("diff", StringComparison.OrdinalIgnoreCase) || language.Equals("patch", StringComparison.OrdinalIgnoreCase))
        {
            var style = text.StartsWith('+') ? Theme.DiffAdded : text.StartsWith('-') ? Theme.DiffRemoved : text.StartsWith("@@", StringComparison.Ordinal) ? Theme.CodeKeyword : Theme.Code;
            yield return new StyledSpan(text, style);
            yield break;
        }

        var position = 0;
        while (position < text.Length)
        {
            var start = position;
            var style = Theme.Code;
            var character = text[position++];
            if (char.IsHighSurrogate(character) && position < text.Length && char.IsLowSurrogate(text[position]))
            {
                position++;
            }
            else if (character is '"' or '\'')
            {
                style = Theme.CodeString;
                while (position < text.Length)
                {
                    var next = text[position++];
                    if (next == '\\' && position < text.Length)
                    {
                        position++;
                    }
                    else if (next == character)
                    {
                        break;
                    }
                }
            }
            else if ((character == '/' && position < text.Length && text[position] == '/')
                     || (character == '#' && language is "python" or "py" or "bash" or "sh" or "yaml" or "yml" or "powershell" or "ps1")
                     || (character == '-' && position < text.Length && text[position] == '-' && language.Equals("sql", StringComparison.OrdinalIgnoreCase)))
            {
                style = Theme.CodeComment;
                position = text.Length;
            }
            else if (char.IsDigit(character))
            {
                style = Theme.CodeNumber;
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '.' or '_'))
                {
                    position++;
                }
            }
            else if (char.IsLetter(character) || character == '_')
            {
                while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
                {
                    position++;
                }

                if (Keywords.Contains(text[start..position]))
                {
                    style = Theme.CodeKeyword;
                }
            }

            yield return new StyledSpan(text[start..position], style);
        }
    }

    private static int Fence(string line)
    {
        if (line.Length == 0 || line[0] is not ('`' or '~'))
        {
            return 0;
        }

        var length = 1;
        while (length < line.Length && line[length] == line[0])
        {
            length++;
        }

        return length;
    }

    private static bool IsRule(string text, char marker) => text.Count(character => character == marker) >= 3 && text.All(character => character == marker || character == ' ');

    private static string[] Cells(string line) => line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();

    private static bool IsTableRule(string line) => line.Contains('|') && Cells(line).All(cell => cell.Trim(':').Length >= 3 && cell.Trim(':').All(character => character == '-'));

    private static IEnumerable<IReadOnlyList<StyledSpan>> Table(List<string[]> rows, int width, Style body, IReadOnlyList<string> terms)
    {
        var columns = rows.Max(row => row.Length);
        var cellWidth = (width - (columns - 1) * 3) / columns;
        if (cellWidth < 4)
        {
            foreach (var row in rows.Skip(1))
            {
                for (var column = 0; column < Math.Max(columns, row.Length); column++)
                {
                    var title = column < rows[0].Length ? rows[0][column] : "Value";
                    var value = column < row.Length ? row[column] : string.Empty;
                    foreach (var line in Paragraph($"**{title}:** {value}", width, body, terms))
                    {
                        yield return line;
                    }
                }

                yield return [];
            }

            yield break;
        }

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var cells = Enumerable.Range(0, columns).Select(column =>
                StyledText.Wrap(Highlight(Inline(column < row.Length ? row[column] : string.Empty, rowIndex == 0 ? Theme.MarkdownHeading : body), terms), cellWidth).ToArray()).ToArray();
            var height = cells.Max(cell => cell.Length);
            for (var line = 0; line < height; line++)
            {
                var spans = new List<StyledSpan>();
                for (var column = 0; column < columns; column++)
                {
                    if (column > 0)
                    {
                        spans.Add(new StyledSpan(" │ ", Theme.Border));
                    }

                    var content = line < cells[column].Length ? cells[column][line] : [];
                    spans.AddRange(content);
                    spans.Add(new StyledSpan(new string(' ', Math.Max(0, cellWidth - content.Sum(span => TextWidth.Of(span.Text)))), body));
                }

                yield return spans;
            }

            if (rowIndex == 0)
            {
                yield return [new StyledSpan(string.Join("─┼─", Enumerable.Repeat(new string('─', cellWidth), columns)), Theme.Border)];
            }
        }
    }

    public static string ExpandTabs(string line)
    {
        if (!line.Contains('\t'))
        {
            return line;
        }

        var result = new StringBuilder();
        var column = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (rune.Value == '\t')
            {
                var count = 4 - column % 4;
                result.Append(' ', count);
                column += count;
            }
            else
            {
                result.Append(rune);
                column += TextWidth.Of(rune);
            }
        }

        return result.ToString();
    }

    /// <summary>Removes terminal control sequences from captured output while preserving line breaks and tabs.</summary>
    public static string Clean(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\u001b')
            {
                if (index + 1 < text.Length && text[index + 1] == '[')
                {
                    index += 2;
                    while (index < text.Length && text[index] is not (>= '@' and <= '~'))
                    {
                        index++;
                    }
                }
                else if (index + 1 < text.Length && text[index + 1] == ']')
                {
                    index += 2;
                    while (index < text.Length && text[index] != '\a')
                    {
                        if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\')
                        {
                            index++;
                            break;
                        }

                        index++;
                    }
                }

                continue;
            }

            if (!char.IsControl(character) || character is '\n' or '\r' or '\t')
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }
}
