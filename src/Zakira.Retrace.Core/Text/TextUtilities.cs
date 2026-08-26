using System.Text;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Core.Text;

/// <summary>
/// Shared text handling for previews, titles, and snippets.
/// </summary>
public static class TextUtilities
{
    /// <summary>
    /// Collapses runs of whitespace into single spaces and trims. Session text is full of newlines
    /// and indentation that would otherwise wreck single-line list output.
    /// </summary>
    public static string Flatten(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Truncates to a character budget, appending an ellipsis when anything was cut.</summary>
    public static string Truncate(string? value, int maxCharacters, string suffix = "\u2026")
    {
        if (string.IsNullOrEmpty(value) || maxCharacters <= 0 || value.Length <= maxCharacters)
        {
            return value ?? string.Empty;
        }

        // Prefer breaking at a word boundary within the last 20% of the budget, so a truncated
        // preview does not end mid-token.
        var window = Math.Max(1, maxCharacters / 5);
        var cut = value.LastIndexOf(' ', maxCharacters - 1, Math.Min(window, maxCharacters));
        if (cut <= 0)
        {
            cut = maxCharacters;
        }

        return string.Concat(value.AsSpan(0, cut).TrimEnd(), suffix);
    }

    /// <summary>Flattens and truncates in one step, for list and search previews.</summary>
    public static string Preview(string? value, int maxCharacters) => Truncate(Flatten(value), maxCharacters);

    /// <summary>
    /// Derives a session title from its opening user message, for harnesses that do not generate one.
    /// </summary>
    public static string DeriveTitle(string? firstUserMessage, int maxCharacters = 80)
    {
        var flattened = Flatten(firstUserMessage);
        if (flattened.Length == 0)
        {
            return "(untitled session)";
        }

        // Stop at the first sentence boundary when there is one reasonably early, so a title reads
        // like a title rather than the first N characters of a paragraph.
        var stop = flattened.AsSpan(0, Math.Min(flattened.Length, maxCharacters)).IndexOfAny(['.', '?', '!', '\n']);
        if (stop > 12)
        {
            return flattened[..(stop + 1)].TrimEnd();
        }

        return Truncate(flattened, maxCharacters);
    }

    /// <summary>
    /// Splits text into overlapping chunks on paragraph, then sentence, then hard boundaries.
    /// </summary>
    /// <remarks>
    /// The overlap exists so a phrase spanning a boundary still matches. Splitting prefers natural
    /// breaks because a chunk cut mid-sentence embeds poorly: the vector ends up representing a
    /// fragment rather than an idea.
    /// </remarks>
    public static IEnumerable<string> SplitIntoChunks(string text, int maxCharacters, int overlapCharacters, int minCharacters)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= maxCharacters)
        {
            if (trimmed.Length >= minCharacters)
            {
                yield return trimmed;
            }

            yield break;
        }

        var position = 0;
        while (position < trimmed.Length)
        {
            var remaining = trimmed.Length - position;
            if (remaining <= maxCharacters)
            {
                var tail = trimmed[position..].Trim();
                if (tail.Length >= minCharacters)
                {
                    yield return tail;
                }

                yield break;
            }

            var window = trimmed.AsSpan(position, maxCharacters);
            var cut = FindBreak(window);
            var chunk = trimmed.Substring(position, cut).Trim();
            if (chunk.Length >= minCharacters)
            {
                yield return chunk;
            }

            var advance = Math.Max(1, cut - overlapCharacters);
            position += advance;
        }
    }

    private static int FindBreak(ReadOnlySpan<char> window)
    {
        // Search the trailing third for a boundary, best first. Anything earlier would waste too
        // much of the budget.
        var floor = window.Length * 2 / 3;

        var paragraph = window.LastIndexOf("\n\n".AsSpan());
        if (paragraph >= floor)
        {
            return paragraph + 2;
        }

        for (var i = window.Length - 1; i >= floor; i--)
        {
            if (window[i] is '.' or '!' or '?' && (i + 1 >= window.Length || char.IsWhiteSpace(window[i + 1])))
            {
                return i + 1;
            }
        }

        var newline = window.LastIndexOf('\n');
        if (newline >= floor)
        {
            return newline + 1;
        }

        var space = window.LastIndexOf(' ');
        return space >= floor ? space + 1 : window.Length;
    }

    /// <summary>
    /// Normalises a path for comparison: forward slashes, no trailing separator, lower-cased on
    /// Windows. OpenCode stores forward slashes even on Windows, so raw comparison fails.
    /// </summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        return OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
    }

    /// <summary>Whether <paramref name="candidate"/> is at or below <paramref name="root"/>.</summary>
    public static bool IsUnder(string? candidate, string? root)
    {
        var normalizedRoot = NormalizePath(root);
        if (normalizedRoot.Length == 0)
        {
            return true;
        }

        var normalizedCandidate = NormalizePath(candidate);
        return normalizedCandidate.Length != 0
            && (normalizedCandidate.Equals(normalizedRoot, StringComparison.Ordinal)
                || normalizedCandidate.StartsWith(normalizedRoot + "/", StringComparison.Ordinal));
    }

    /// <summary>Renders an instant as "3 days ago", or an ISO date when it is far in the past.</summary>
    public static string ToRelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var delta = now - value;

        if (delta < TimeSpan.Zero)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 1)
        {
            return "just now";
        }

        if (delta.TotalHours < 1)
        {
            var minutes = (int)delta.TotalMinutes;
            return $"{minutes}m ago";
        }

        if (delta.TotalDays < 1)
        {
            var hours = (int)delta.TotalHours;
            return $"{hours}h ago";
        }

        if (delta.TotalDays < 30)
        {
            var days = (int)delta.TotalDays;
            return $"{days}d ago";
        }

        if (delta.TotalDays < 365)
        {
            var months = (int)(delta.TotalDays / 30);
            return $"{months}mo ago";
        }

        return value.ToString("yyyy-MM-dd");
    }

    /// <summary>Renders a role as a short, fixed-width label for transcript output.</summary>
    public static string RoleLabel(TurnRole role) => role switch
    {
        TurnRole.User => "user",
        TurnRole.Assistant => "assistant",
        TurnRole.Tool => "tool",
        TurnRole.System => "system",
        TurnRole.Info => "info",
        _ => "?"
    };
}
