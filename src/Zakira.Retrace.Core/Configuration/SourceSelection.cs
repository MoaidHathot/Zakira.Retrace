using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Core.Configuration;

/// <summary>Resolves provider names used by the CLI and browser to canonical source ids.</summary>
public static class SourceSelection
{
    /// <summary>
    /// Accepts repeated or comma-separated names, case-insensitively. <c>copilot</c> selects both
    /// Copilot providers, <c>vscode</c> selects Copilot in VS Code, and <c>all</c> selects every id.
    /// Unknown names are rejected so a typo cannot silently broaden or empty a search.
    /// </summary>
    public static IReadOnlyList<string> Resolve(IEnumerable<string> names, IEnumerable<string> availableIds)
    {
        var available = availableIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var requested = names.ToArray();
        var tokens = requested.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray();
        if (requested.Length > 0 && tokens.Length == 0)
        {
            throw new RetraceException("A provider id is required. Use opencode, copilot-cli, copilot-vscode, or all.");
        }

        foreach (var name in tokens)
        {
            var matches = available.Where(id => name.ToLowerInvariant() switch
            {
                "all" => true,
                "copilot" => id is "copilot-cli" or "copilot-vscode",
                "vscode" => id.Equals("copilot-vscode", StringComparison.OrdinalIgnoreCase),
                _ => id.Equals(name, StringComparison.OrdinalIgnoreCase)
            }).ToArray();

            if (matches.Length == 0 && !name.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var choices = available.Length == 0 ? "none" : string.Join(", ", available);
                throw new RetraceException($"Unknown or disabled provider '{name}'. Available: {choices}. Run `retrace sources` to inspect providers.");
            }

            selected.UnionWith(matches);
        }

        return [.. available.Where(selected.Contains)];
    }
}
