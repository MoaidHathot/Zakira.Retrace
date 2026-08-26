using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Core.Index;

/// <summary>
/// Splits a transcript into indexable passages.
/// </summary>
/// <remarks>
/// <para>
/// The single most important decision here is which passages get vectors. Tool output and diffs are
/// the overwhelming majority of stored bytes across every supported harness, and they are also the
/// least useful thing to embed: a vector of a build log or a unified diff sits in a dense, noisy
/// region of the space and mostly returns false positives.
/// </para>
/// <para>
/// So tool output is indexed for keyword search — where "which session ran that command" is a real
/// and frequent question — but marked non-embeddable. That keeps the vector table proportional to
/// the amount of actual conversation rather than to the amount of captured stdout, which is the
/// difference between a manageable index and one larger than the sources it came from.
/// </para>
/// </remarks>
public sealed class SessionChunker(ChunkPolicy policy)
{
    /// <summary>Uses the default policy.</summary>
    public SessionChunker() : this(ChunkPolicy.Default)
    {
    }

    /// <summary>Splits a transcript into passages.</summary>
    public IEnumerable<SessionChunk> Chunk(SessionTranscript transcript)
    {
        var ordinal = 0;

        // The title and opening prompt are indexed as their own passage so a session stays findable
        // by what it was about even when the body is all tool output.
        var header = BuildHeader(transcript.Summary);
        if (header.Length >= policy.MinCharacters)
        {
            yield return new SessionChunk
            {
                Ordinal = ordinal++,
                TurnIndex = 0,
                Role = TurnRole.System,
                Timestamp = transcript.Summary.CreatedAt,
                Text = header,
                IsEmbeddable = true
            };
        }

        foreach (var turn in transcript.Turns)
        {
            foreach (var chunk in ChunkTurn(turn, ref ordinal))
            {
                yield return chunk;
            }
        }
    }

    private List<SessionChunk> ChunkTurn(Turn turn, ref int ordinal)
    {
        var results = new List<SessionChunk>();
        var prose = new System.Text.StringBuilder();

        foreach (var block in turn.Blocks)
        {
            switch (block)
            {
                case TextBlock textBlock:
                    Append(prose, textBlock.Text);
                    break;

                case ReasoningBlock reasoning when policy.IncludeReasoning:
                    Append(prose, reasoning.Text);
                    break;

                case ToolCallBlock tool when policy.IncludeToolOutput:
                    results.Add(BuildToolChunk(turn, tool, ordinal++));
                    break;

                case ToolCallBlock tool:
                    // Even with output excluded, the tool name and title are worth a keyword entry:
                    // they are how "did I ever run the migration script here" gets answered.
                    Append(prose, $"[{tool.ToolName}] {tool.Title}");
                    break;

                case PatchBlock patch:
                    Append(prose, $"[edit] {patch.Path}");
                    break;
            }
        }

        var text = prose.ToString().Trim();
        if (text.Length > 0)
        {
            foreach (var piece in TextUtilities.SplitIntoChunks(text, policy.MaxCharacters, policy.OverlapCharacters, policy.MinCharacters))
            {
                results.Add(new SessionChunk
                {
                    Ordinal = ordinal++,
                    TurnIndex = turn.Index,
                    Role = turn.Role,
                    Timestamp = turn.Timestamp,
                    Text = piece,
                    IsEmbeddable = true
                });
            }
        }

        return results;
    }

    private SessionChunk BuildToolChunk(Turn turn, ToolCallBlock tool, int ordinal)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append('[').Append(tool.ToolName).Append(']');

        if (!string.IsNullOrWhiteSpace(tool.Title))
        {
            builder.Append(' ').Append(TextUtilities.Flatten(tool.Title));
        }

        if (!string.IsNullOrWhiteSpace(tool.InputJson))
        {
            builder.Append('\n').Append(Cap(tool.InputJson, policy.MaxToolOutputCharacters / 2));
        }

        if (!string.IsNullOrWhiteSpace(tool.Output))
        {
            builder.Append('\n').Append(Cap(tool.Output, policy.MaxToolOutputCharacters));
        }

        return new SessionChunk
        {
            Ordinal = ordinal,
            TurnIndex = turn.Index,
            Role = TurnRole.Tool,
            Timestamp = turn.Timestamp,
            Text = builder.ToString(),
            IsEmbeddable = false
        };
    }

    private static string BuildHeader(SessionSummary summary)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(summary.Title);

        if (!string.IsNullOrWhiteSpace(summary.Preview) && !summary.Title.Equals(summary.Preview, StringComparison.Ordinal))
        {
            builder.Append('\n').Append(summary.Preview);
        }

        if (summary.Workspace?.Label is { Length: > 0 } label)
        {
            builder.Append('\n').Append(label);
        }

        return builder.ToString().Trim();
    }

    private static void Append(System.Text.StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("\n\n");
        }

        builder.Append(value.Trim());
    }

    private static string Cap(string value, int max) =>
        max > 0 && value.Length > max ? value[..max] : value;
}
