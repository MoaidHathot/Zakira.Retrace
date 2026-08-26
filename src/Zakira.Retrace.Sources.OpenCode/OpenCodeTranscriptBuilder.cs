using System.Text.Json;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Json;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Sources.OpenCode;

/// <summary>
/// Converts OpenCode's message and part JSON into the shared transcript model.
/// </summary>
/// <remarks>
/// OpenCode stores a message as metadata only; the content lives in separate part rows keyed by
/// message id. Part shapes observed in the wild:
/// <list type="bullet">
///   <item><description><c>text</c>: <c>{ type, text, time { start, end } }</c></description></item>
///   <item><description><c>reasoning</c>: <c>{ type, text, metadata }</c></description></item>
///   <item><description><c>tool</c>: <c>{ type, callID, tool, state { status, input, output, title, metadata, time } }</c></description></item>
///   <item><description><c>patch</c>: <c>{ type, hash, files[] }</c></description></item>
/// </list>
/// </remarks>
internal static class OpenCodeTranscriptBuilder
{
    /// <summary>Builds a transcript from raw rows.</summary>
    public static SessionTranscript Build(
        SessionSummary summary,
        IReadOnlyList<OpenCodeMessage> messages,
        IReadOnlyDictionary<string, List<string>> partsByMessage,
        TranscriptOptions options)
    {
        var turns = new List<Turn>();
        var files = new Dictionary<string, TouchedFile>(StringComparer.OrdinalIgnoreCase);
        var models = new List<string>();
        var toolCalls = 0;
        var characters = 0;
        var truncated = false;
        int? nextTurn = null;
        string? firstUserText = null;

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];

            if (options.FromTurn is { } from && index < from)
            {
                continue;
            }

            if (options.ToTurn is { } to && index > to)
            {
                nextTurn ??= index;
                truncated = true;
                break;
            }

            using var document = RetraceJson.TryParse(message.Data);
            var root = document?.RootElement ?? default;

            var role = ParseRole(root.GetStringOrNull("role"));
            var model = root.GetStringOrNull("modelID") ?? root.Path("model")?.GetStringOrNull("modelID");
            if (!string.IsNullOrWhiteSpace(model) && !models.Contains(model, StringComparer.OrdinalIgnoreCase))
            {
                models.Add(model);
            }

            var timestamp = RetraceJson.FromEpochMilliseconds(root.Path("time")?.GetInt64OrNull("created"))
                ?? message.TimeCreated;

            var blocks = new List<ContentBlock>();
            if (partsByMessage.TryGetValue(message.Id, out var parts))
            {
                foreach (var part in parts)
                {
                    var block = ParsePart(part, options, files, ref toolCalls);
                    if (block is not null)
                    {
                        blocks.Add(block);
                    }
                }
            }

            if (blocks.Count == 0)
            {
                continue;
            }

            var turn = new Turn
            {
                Index = index,
                Role = role,
                Timestamp = timestamp,
                Model = model,
                Agent = root.GetStringOrNull("agent") ?? root.GetStringOrNull("mode"),
                Blocks = blocks
            };

            if (role == TurnRole.User && firstUserText is null)
            {
                // Only the first text block. OpenCode expands an `@file` attachment into extra text
                // parts on the same user message ("Called the Read tool with…", then the file
                // body), so concatenating them would make every preview and derived title start
                // with tool chatter instead of what the user actually asked.
                firstUserText = blocks.OfType<TextBlock>().FirstOrDefault()?.Text;
            }

            if (options.MaxCharacters > 0)
            {
                characters += EstimateSize(turn);
                if (characters > options.MaxCharacters && turns.Count > 0)
                {
                    nextTurn = index;
                    truncated = true;
                    break;
                }
            }

            turns.Add(turn);
        }

        var enriched = summary with
        {
            Preview = summary.Preview ?? TextUtilities.Preview(firstUserText, 200),
            Models = models.Count > 0 ? models : summary.Models,
            Stats = summary.Stats with
            {
                MessageCount = summary.Stats.MessageCount ?? messages.Count,
                ToolCallCount = toolCalls
            }
        };

        return new SessionTranscript
        {
            Summary = enriched,
            Turns = turns,
            Files = files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray(),
            References = [],
            TotalTurns = messages.Count,
            NextTurnIndex = nextTurn,
            IsTruncated = truncated
        };
    }

    private static ContentBlock? ParsePart(
        string partJson,
        TranscriptOptions options,
        Dictionary<string, TouchedFile> files,
        ref int toolCalls)
    {
        using var document = RetraceJson.TryParse(partJson);
        if (document is null)
        {
            return null;
        }

        var root = document.RootElement;
        var type = root.GetStringOrNull("type");

        switch (type)
        {
            case "text":
            {
                var text = root.GetStringOrNull("text");
                return string.IsNullOrWhiteSpace(text) ? null : new TextBlock(text);
            }

            case "reasoning":
            {
                if (!options.IncludeReasoning)
                {
                    return null;
                }

                var text = root.GetStringOrNull("text");
                return string.IsNullOrWhiteSpace(text) ? null : new ReasoningBlock(text);
            }

            case "tool":
            {
                toolCalls++;
                return ParseToolPart(root, options, files);
            }

            case "patch":
            {
                return ParsePatchPart(root, options, files);
            }

            default:
                return null;
        }
    }

    private static ToolCallBlock ParseToolPart(JsonElement root, TranscriptOptions options, Dictionary<string, TouchedFile> files)
    {
        var toolName = root.GetStringOrNull("tool") ?? "tool";
        var state = root.Path("state");

        var status = state?.GetStringOrNull("status");
        var title = state?.GetStringOrNull("title");
        var input = state?.Path("input");
        var output = options.IncludeToolOutput ? state?.Path("output").ToDisplayText() : null;

        if (output is not null && options.MaxToolOutputCharacters > 0 && output.Length > options.MaxToolOutputCharacters)
        {
            output = string.Concat(
                output.AsSpan(0, options.MaxToolOutputCharacters),
                $"\n… [{output.Length - options.MaxToolOutputCharacters:N0} more characters]");
        }

        RecordFileFromToolInput(input, toolName, files);

        var start = RetraceJson.FromEpochMilliseconds(state?.Path("time")?.GetInt64OrNull("start"));
        var end = RetraceJson.FromEpochMilliseconds(state?.Path("time")?.GetInt64OrNull("end"));

        return new ToolCallBlock
        {
            ToolName = toolName,
            Title = title,
            InputJson = input.ToRawJson(),
            Output = output,
            Status = status,
            Duration = start is not null && end is not null ? end - start : null,
            CallId = root.GetStringOrNull("callID")
        };
    }

    private static PatchBlock? ParsePatchPart(JsonElement root, TranscriptOptions options, Dictionary<string, TouchedFile> files)
    {
        if (!root.TryGetProperty("files", out var fileList) || fileList.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? firstPath = null;
        foreach (var entry in fileList.EnumerateArray())
        {
            var path = entry.ValueKind == JsonValueKind.String ? entry.GetString() : entry.GetStringOrNull("file");
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            firstPath ??= path;
            files.TryAdd(path, new TouchedFile { Path = path, Tool = "patch" });
        }

        if (firstPath is null)
        {
            return null;
        }

        return options.IncludeDiffs
            ? new PatchBlock { Path = firstPath, Diff = root.GetStringOrNull("patch") }
            : new PatchBlock { Path = firstPath };
    }

    private static void RecordFileFromToolInput(JsonElement? input, string toolName, Dictionary<string, TouchedFile> files)
    {
        if (input is not { ValueKind: JsonValueKind.Object } element)
        {
            return;
        }

        // Different tools name their path argument differently; these are the forms OpenCode's
        // built-in tools emit.
        foreach (var candidate in (string[])["filePath", "path", "file"])
        {
            var value = element.GetStringOrNull(candidate);
            if (!string.IsNullOrWhiteSpace(value))
            {
                files.TryAdd(value, new TouchedFile { Path = value, Tool = toolName });
                return;
            }
        }
    }

    private static TurnRole ParseRole(string? role) => role switch
    {
        "user" => TurnRole.User,
        "assistant" => TurnRole.Assistant,
        "system" => TurnRole.System,
        "tool" => TurnRole.Tool,
        _ => TurnRole.Assistant
    };

    private static int EstimateSize(Turn turn)
    {
        var total = 0;
        foreach (var block in turn.Blocks)
        {
            total += block switch
            {
                TextBlock text => text.Text.Length,
                ReasoningBlock reasoning => reasoning.Text.Length,
                ToolCallBlock tool => (tool.Output?.Length ?? 0) + (tool.InputJson?.Length ?? 0) + tool.ToolName.Length,
                PatchBlock patch => (patch.Diff?.Length ?? 0) + patch.Path.Length,
                _ => 0
            };
        }

        return total;
    }
}
