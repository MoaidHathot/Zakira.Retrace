namespace Zakira.Retrace.Abstractions;

/// <summary>Controls how much of a session is materialised by <see cref="ISessionSource.GetAsync"/>.</summary>
public sealed record TranscriptOptions
{
    /// <summary>
    /// Include captured tool output. Off by default: tool output is the bulk of every session store
    /// and is rarely what a reader or an agent actually wants.
    /// </summary>
    public bool IncludeToolOutput { get; init; }

    /// <summary>Include model reasoning blocks.</summary>
    public bool IncludeReasoning { get; init; }

    /// <summary>Include unified diffs on patch blocks.</summary>
    public bool IncludeDiffs { get; init; }

    /// <summary>First turn to return, inclusive.</summary>
    public int? FromTurn { get; init; }

    /// <summary>Last turn to return, inclusive.</summary>
    public int? ToTurn { get; init; }

    /// <summary>
    /// Approximate character budget for the whole transcript. When exceeded, turns stop being added
    /// and <see cref="SessionTranscript.NextTurnIndex"/> reports where to continue.
    /// <c>0</c> means unbounded.
    /// </summary>
    public int MaxCharacters { get; init; }

    /// <summary>Truncate any single tool output to this many characters. <c>0</c> means unbounded.</summary>
    public int MaxToolOutputCharacters { get; init; } = 2000;

    /// <summary>Everything, unbounded. What the CLI uses for `export`.</summary>
    public static TranscriptOptions Full { get; } = new()
    {
        IncludeToolOutput = true,
        IncludeReasoning = true,
        IncludeDiffs = true,
        MaxCharacters = 0,
        MaxToolOutputCharacters = 0
    };

    /// <summary>Prose only, unbounded. What indexing and the default `show` use.</summary>
    public static TranscriptOptions Default { get; } = new();
}

/// <summary>Controls how a resume command is constructed.</summary>
public sealed record ResumeOptions
{
    /// <summary>Branch the session instead of continuing it in place, where the harness supports it.</summary>
    public bool Fork { get; init; }

    /// <summary>Override the agent the resumed session starts with.</summary>
    public string? Agent { get; init; }

    /// <summary>Override the model the resumed session starts with.</summary>
    public string? Model { get; init; }

    /// <summary>Defaults: continue in place, inheriting the recorded agent and model.</summary>
    public static ResumeOptions Default { get; } = new();
}

/// <summary>
/// A ready-to-run command that reopens a session in its own harness.
/// </summary>
/// <remarks>
/// Retrace never resumes a session itself; it only describes how. This type is what
/// <c>retrace resume</c> prints and what the <c>session-resume-command</c> MCP tool returns. The
/// MCP path never executes it, so an agent that asks about a session cannot silently launch an
/// interactive process on the user's machine.
/// </remarks>
public sealed record ResumeCommand
{
    /// <summary>Executable to run, resolved from PATH by the caller.</summary>
    public required string Executable { get; init; }

    /// <summary>Arguments, already split. Not shell-escaped.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Directory the command must run in, which is the session's original working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// A copy-pasteable single-line rendering, with quoting applied where arguments contain spaces.
    /// </summary>
    public required string DisplayCommand { get; init; }

    /// <summary>
    /// Caveats worth showing the user, such as a harness that cannot reattach to a specific session
    /// and can only reopen the surrounding workspace.
    /// </summary>
    public string? Notes { get; init; }

    /// <summary>Whether running this command actually restores conversation state.</summary>
    public bool RestoresConversation { get; init; } = true;
}
