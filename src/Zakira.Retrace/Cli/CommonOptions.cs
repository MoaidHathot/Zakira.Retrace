using System.CommandLine;
using System.Globalization;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Cli;

/// <summary>
/// Options shared across commands.
/// </summary>
/// <remarks>
/// These are declared once and marked recursive so every subcommand inherits them. Declaring the
/// same flag separately per command is how a CLI ends up with <c>--json</c> working on some verbs
/// and not others.
/// </remarks>
internal static class CommonOptions
{
    /// <summary>Overrides the config file path.</summary>
    public static Option<string?> Config { get; } = new("--config")
    {
        Description = "Path to retrace.json. Defaults to $XDG_CONFIG_HOME/Zakira.Retrace/retrace.json.",
        Recursive = true
    };

    /// <summary>Output format.</summary>
    public static Option<string> Output { get; } = new("--output", "-o")
    {
        Description = "Output format: text (default), json, or ndjson.",
        DefaultValueFactory = _ => "text",
        Recursive = true
    };

    /// <summary>Raises log verbosity.</summary>
    public static Option<bool> Verbose { get; } = new("--verbose", "-v")
    {
        Description = "Log diagnostic detail to stderr.",
        Recursive = true
    };

    /// <summary>Suppresses non-essential output.</summary>
    public static Option<bool> Quiet { get; } = new("--quiet", "-q")
    {
        Description = "Suppress progress and summary lines.",
        Recursive = true
    };

    /// <summary>Forces or disables ANSI colour.</summary>
    public static Option<string?> Color { get; } = new("--color")
    {
        Description = "ANSI colour: auto (default), always, or never.",
        Recursive = true
    };

    /// <summary>Reads the requested output format, defaulting to text on an unrecognised value.</summary>
    public static OutputFormat GetFormat(ParseResult parseResult) =>
        parseResult.GetValue(Output)?.Trim().ToLowerInvariant() switch
        {
            "json" => OutputFormat.Json,
            "ndjson" => OutputFormat.Ndjson,
            "markdown" or "md" => OutputFormat.Markdown,
            _ => OutputFormat.Text
        };
}

/// <summary>
/// Filter options shared by <c>list</c> and <c>search</c>.
/// </summary>
/// <remarks>
/// Held as one bundle so the two commands cannot drift apart, and so a user who learns the filter
/// flags for one already knows them for the other.
/// </remarks>
internal sealed class FilterOptions
{
    /// <summary>Restricts to specific sources.</summary>
    public Option<string[]> Source { get; } = new("--source", "-s")
    {
        Description = "Restrict to these sources (opencode, copilot-cli, copilot-vscode). Repeatable.",
        AllowMultipleArgumentsPerToken = true
    };

    /// <summary>Restricts to a workspace directory.</summary>
    public Option<string?> Workspace { get; } = new("--workspace", "-w")
    {
        Description = "Only sessions whose working directory is at or below this path."
    };

    /// <summary>Shorthand for the current directory.</summary>
    public Option<bool> Here { get; } = new("--here")
    {
        Description = "Shorthand for --workspace <current directory>."
    };

    /// <summary>Restricts to a repository.</summary>
    public Option<string?> Repository { get; } = new("--repo")
    {
        Description = "Only sessions whose repository or path contains this value."
    };

    /// <summary>Restricts to a branch.</summary>
    public Option<string?> Branch { get; } = new("--branch")
    {
        Description = "Only sessions recorded on this branch."
    };

    /// <summary>Lower time bound.</summary>
    public Option<string?> Since { get; } = new("--since")
    {
        Description = "Only sessions updated since this point. Accepts a date (2026-01-31) or a duration (7d, 12h, 30m)."
    };

    /// <summary>Upper time bound.</summary>
    public Option<string?> Until { get; } = new("--until")
    {
        Description = "Only sessions updated before this point. Same formats as --since."
    };

    /// <summary>Restricts to an agent or mode.</summary>
    public Option<string?> Agent { get; } = new("--agent")
    {
        Description = "Only sessions run with this agent or mode."
    };

    /// <summary>Restricts to a model.</summary>
    public Option<string?> Model { get; } = new("--model")
    {
        Description = "Only sessions that used a model whose id contains this value."
    };

    /// <summary>Restricts to tagged sessions.</summary>
    public Option<string[]> Tag { get; } = new("--tag")
    {
        Description = "Only sessions carrying every one of these tags. Repeatable.",
        AllowMultipleArgumentsPerToken = true
    };

    /// <summary>Minimum message count.</summary>
    public Option<int?> MinMessages { get; } = new("--min-messages")
    {
        Description = "Drop sessions with fewer than this many messages."
    };

    /// <summary>Includes archived sessions.</summary>
    public Option<bool> IncludeArchived { get; } = new("--include-archived")
    {
        Description = "Include sessions the harness marked archived."
    };

    /// <summary>Bypasses the index.</summary>
    public Option<bool> Live { get; } = new("--live")
    {
        Description = "Read the sources directly instead of the index. Always current, slower."
    };

    /// <summary>Adds every filter option to a command.</summary>
    public void AddTo(Command command)
    {
        command.Options.Add(Source);
        command.Options.Add(Workspace);
        command.Options.Add(Here);
        command.Options.Add(Repository);
        command.Options.Add(Branch);
        command.Options.Add(Since);
        command.Options.Add(Until);
        command.Options.Add(Agent);
        command.Options.Add(Model);
        command.Options.Add(Tag);
        command.Options.Add(MinMessages);
        command.Options.Add(IncludeArchived);
        command.Options.Add(Live);
    }

    /// <summary>Builds a filter from parsed arguments.</summary>
    public SessionFilter Build(ParseResult parseResult, int limit, SessionSortOrder sort)
    {
        var workspace = parseResult.GetValue(Workspace);
        if (parseResult.GetValue(Here))
        {
            workspace = Directory.GetCurrentDirectory();
        }

        return new SessionFilter
        {
            SourceIds = parseResult.GetValue(Source) ?? [],
            WorkspacePath = string.IsNullOrWhiteSpace(workspace) ? null : Path.GetFullPath(workspace),
            Repository = parseResult.GetValue(Repository),
            Branch = parseResult.GetValue(Branch),
            Since = ParseTimeBound(parseResult.GetValue(Since), isLowerBound: true),
            Until = ParseTimeBound(parseResult.GetValue(Until), isLowerBound: false),
            Agent = parseResult.GetValue(Agent),
            Model = parseResult.GetValue(Model),
            Tags = parseResult.GetValue(Tag) ?? [],
            MinMessages = parseResult.GetValue(MinMessages),
            IncludeArchived = parseResult.GetValue(IncludeArchived),
            Limit = limit,
            Sort = sort
        };
    }

    /// <summary>Whether the caller asked to bypass the index.</summary>
    public bool IsLive(ParseResult parseResult) => parseResult.GetValue(Live);

    /// <summary>
    /// Parses a time bound as either an absolute date or a relative duration such as <c>7d</c>.
    /// </summary>
    /// <remarks>
    /// Relative durations are what people actually type when looking for recent work, and typing
    /// "what did I do in the last three days" as an absolute date is friction with no upside.
    /// </remarks>
    public static DateTimeOffset? ParseTimeBound(string? value, bool isLowerBound)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Length > 1 && char.IsDigit(trimmed[0]))
        {
            var unit = char.ToLowerInvariant(trimmed[^1]);
            if (unit is 'd' or 'h' or 'm' or 'w'
                && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                var span = unit switch
                {
                    'w' => TimeSpan.FromDays(amount * 7),
                    'd' => TimeSpan.FromDays(amount),
                    'h' => TimeSpan.FromHours(amount),
                    _ => TimeSpan.FromMinutes(amount)
                };

                return DateTimeOffset.UtcNow - span;
            }
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            // A bare date as an upper bound means "through the end of that day"; without this,
            // `--until 2026-01-31` would silently exclude everything on the 31st.
            var isDateOnly = trimmed.Length <= 10 && !trimmed.Contains(':', StringComparison.Ordinal);
            return !isLowerBound && isDateOnly ? parsed.AddDays(1).AddTicks(-1) : parsed;
        }

        throw new RetraceException($"Could not parse '{value}' as a date or duration. Try 2026-01-31, or 7d / 12h / 30m.");
    }
}
