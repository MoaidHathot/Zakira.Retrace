namespace Zakira.Retrace.Abstractions;

/// <summary>
/// Renders an executable plus arguments as a single copy-pasteable line.
/// </summary>
/// <remarks>
/// Quoting is deliberately conservative: any argument containing whitespace or a double quote gets
/// wrapped, and embedded quotes are doubled. That form is accepted by cmd, PowerShell, and POSIX
/// shells alike, which matters because the printed command is meant to be pasted, not parsed.
/// </remarks>
public static class ResumeCommandFormatter
{
    /// <summary>Characters that force an argument to be quoted, across cmd, PowerShell, and POSIX shells.</summary>
    private static readonly System.Buffers.SearchValues<char> QuoteTriggers =
        System.Buffers.SearchValues.Create(" \t\"'&|<>^");

    /// <summary>Formats a command line.</summary>
    public static string Format(string executable, IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return Quote(executable);
        }

        return $"{Quote(executable)} {string.Join(' ', arguments.Select(Quote))}";
    }

    private static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        var needsQuotes = value.AsSpan().ContainsAny(QuoteTriggers);
        if (!needsQuotes)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
