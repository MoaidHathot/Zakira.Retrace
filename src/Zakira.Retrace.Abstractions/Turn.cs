namespace Zakira.Retrace.Abstractions;

/// <summary>Who produced a turn.</summary>
public enum TurnRole
{
    /// <summary>A message typed by the human.</summary>
    User,

    /// <summary>A message produced by the model.</summary>
    Assistant,

    /// <summary>A tool result injected back into the conversation.</summary>
    Tool,

    /// <summary>System or developer instructions.</summary>
    System,

    /// <summary>Harness bookkeeping: session start, MCP connection notices, model switches.</summary>
    Info
}

/// <summary>
/// One unit of conversation. A turn holds an ordered list of heterogeneous blocks because a single
/// assistant response routinely mixes prose, reasoning, tool calls, and patches.
/// </summary>
public sealed record Turn
{
    /// <summary>Zero-based position of this turn within the session.</summary>
    public required int Index { get; init; }

    /// <summary>Who produced it.</summary>
    public required TurnRole Role { get; init; }

    /// <summary>When it was produced, when the harness recorded a timestamp.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Model that produced this turn, for assistant turns.</summary>
    public string? Model { get; init; }

    /// <summary>Agent or mode active for this turn.</summary>
    public string? Agent { get; init; }

    /// <summary>Ordered content.</summary>
    public IReadOnlyList<ContentBlock> Blocks { get; init; } = [];

    /// <summary>
    /// Concatenated plain text of every <see cref="TextBlock"/> in this turn. This is what the
    /// indexer embeds; tool output is deliberately excluded because it is both the bulk of the
    /// corpus and the least semantically useful part of it.
    /// </summary>
    public string PlainText =>
        string.Join("\n\n", Blocks.OfType<TextBlock>().Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
}

/// <summary>Base type for the heterogeneous content inside a <see cref="Turn"/>.</summary>
public abstract record ContentBlock;

/// <summary>Prose written by the user or the model.</summary>
public sealed record TextBlock(string Text) : ContentBlock;

/// <summary>
/// Model reasoning, kept separate from <see cref="TextBlock"/> so it can be hidden by default in
/// rendered transcripts without losing it from the index.
/// </summary>
public sealed record ReasoningBlock(string Text) : ContentBlock;

/// <summary>A single tool invocation and its result.</summary>
public sealed record ToolCallBlock : ContentBlock
{
    /// <summary>Tool name as the harness recorded it, for example <c>bash</c> or <c>str_replace</c>.</summary>
    public required string ToolName { get; init; }

    /// <summary>Short human-facing description of the call, when the harness produced one.</summary>
    public string? Title { get; init; }

    /// <summary>Raw JSON arguments, verbatim.</summary>
    public string? InputJson { get; init; }

    /// <summary>Captured output. Often very large, so renderers truncate and the indexer never embeds it.</summary>
    public string? Output { get; init; }

    /// <summary>Terminal status, such as <c>completed</c>, <c>error</c>, or <c>cancelled</c>.</summary>
    public string? Status { get; init; }

    /// <summary>Wall-clock duration, when both start and end were recorded.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Harness-native call identifier, useful for correlating with trajectory logs.</summary>
    public string? CallId { get; init; }
}

/// <summary>A file edit the session applied.</summary>
public sealed record PatchBlock : ContentBlock
{
    /// <summary>Path of the edited file, as recorded.</summary>
    public required string Path { get; init; }

    /// <summary>Unified diff, when the harness stored one.</summary>
    public string? Diff { get; init; }

    /// <summary>Lines added.</summary>
    public int Additions { get; init; }

    /// <summary>Lines removed.</summary>
    public int Deletions { get; init; }
}
