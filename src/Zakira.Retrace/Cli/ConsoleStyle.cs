namespace Zakira.Retrace.Cli;

/// <summary>How command results are rendered.</summary>
public enum OutputFormat
{
    /// <summary>Human-readable, aligned columns, optional colour.</summary>
    Text,

    /// <summary>One indented JSON document.</summary>
    Json,

    /// <summary>One compact JSON object per line, for piping into other tools.</summary>
    Ndjson,

    /// <summary>Markdown, for transcript rendering.</summary>
    Markdown
}

/// <summary>
/// ANSI colour handling.
/// </summary>
/// <remarks>
/// Colour is disabled automatically when output is redirected, when <c>NO_COLOR</c> is set, or when
/// the terminal reports itself as dumb. That matters more than usual here because the same binary
/// serves an MCP server, and escape sequences leaking into a protocol stream are far worse than a
/// plain-looking terminal.
/// </remarks>
public static class ConsoleStyle
{
    private static bool forced;
    private static bool forcedValue;

    /// <summary>Whether colour should be emitted.</summary>
    public static bool ColorEnabled
    {
        get
        {
            if (forced)
            {
                return forcedValue;
            }

            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
            {
                return false;
            }

            if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !Console.IsOutputRedirected;
        }
    }

    /// <summary>Applies the configured colour mode: <c>auto</c>, <c>always</c>, or <c>never</c>.</summary>
    public static void Configure(string? mode)
    {
        switch (mode?.Trim().ToLowerInvariant())
        {
            case "always" or "true" or "yes":
                forced = true;
                forcedValue = true;
                break;

            case "never" or "false" or "no":
                forced = true;
                forcedValue = false;
                break;

            default:
                forced = false;
                break;
        }
    }

    /// <summary>Dim grey, for secondary detail.</summary>
    public static string Dim(string value) => Wrap(value, "\u001b[90m");

    /// <summary>Bold, for headings and identifiers.</summary>
    public static string Bold(string value) => Wrap(value, "\u001b[1m");

    /// <summary>Cyan, for session URIs.</summary>
    public static string Cyan(string value) => Wrap(value, "\u001b[36m");

    /// <summary>Green, for healthy status.</summary>
    public static string Green(string value) => Wrap(value, "\u001b[32m");

    /// <summary>Yellow, for warnings and highlighted matches.</summary>
    public static string Yellow(string value) => Wrap(value, "\u001b[33m");

    /// <summary>Red, for failures.</summary>
    public static string Red(string value) => Wrap(value, "\u001b[31m");

    /// <summary>Magenta, for scores and counts.</summary>
    public static string Magenta(string value) => Wrap(value, "\u001b[35m");

    private static string Wrap(string value, string code) => ColorEnabled ? $"{code}{value}\u001b[0m" : value;

    /// <summary>
    /// Replaces the index's <c>&lt;&lt;</c> / <c>&gt;&gt;</c> highlight markers with colour, or
    /// strips them when colour is off.
    /// </summary>
    public static string RenderHighlights(string value) =>
        ColorEnabled
            ? value.Replace("<<", "\u001b[33m", StringComparison.Ordinal).Replace(">>", "\u001b[0m", StringComparison.Ordinal)
            : value.Replace("<<", string.Empty, StringComparison.Ordinal).Replace(">>", string.Empty, StringComparison.Ordinal);

    /// <summary>Visible width of a string, ignoring any ANSI escape sequences it contains.</summary>
    public static int VisibleLength(string value)
    {
        var length = 0;
        var inEscape = false;

        foreach (var character in value)
        {
            if (inEscape)
            {
                if (character == 'm')
                {
                    inEscape = false;
                }

                continue;
            }

            if (character == '\u001b')
            {
                inEscape = true;
                continue;
            }

            length++;
        }

        return length;
    }

    /// <summary>Pads to a visible width, accounting for embedded escape sequences.</summary>
    public static string PadRightVisible(string value, int width)
    {
        var padding = width - VisibleLength(value);
        return padding > 0 ? value + new string(' ', padding) : value;
    }
}
