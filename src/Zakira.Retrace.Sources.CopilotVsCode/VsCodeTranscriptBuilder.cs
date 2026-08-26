using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Text;

namespace Zakira.Retrace.Sources.CopilotVsCode;

/// <summary>
/// Converts a loaded Copilot Chat session document into the shared model.
/// </summary>
/// <remarks>
/// <para>
/// A session is <c>{ version, sessionId, creationDate, lastMessageDate, customTitle?, requests[] }</c>.
/// Each request is one user prompt plus the whole assistant reply, where the reply is a flat array
/// of heterogeneous parts:
/// </para>
/// <list type="bullet">
///   <item><description>no <c>kind</c> but a <c>value</c> string — rendered markdown</description></item>
///   <item><description><c>kind: "thinking"</c> — model reasoning</description></item>
///   <item><description><c>kind: "toolInvocationSerialized"</c> — a tool call, with
///   <c>invocationMessage.value</c> as its label and <c>toolSpecificData</c> as its payload</description></item>
///   <item><description><c>kind: "textEditGroup"</c> — an edit applied to <c>uri</c></description></item>
///   <item><description><c>kind: "inlineReference"</c>, <c>"codeblockUri"</c>,
///   <c>"mcpServersStarting"</c>, <c>"undoStop"</c>, <c>"prepareToolInvocation"</c> — presentation
///   scaffolding with no content worth indexing</description></item>
/// </list>
/// </remarks>
internal static class VsCodeTranscriptBuilder
{
    /// <summary>Reads only the fields needed for a listing, without walking response parts.</summary>
    public static SessionSummary? BuildSummary(
        JsonObject document,
        string sessionId,
        WorkspaceInfo workspace,
        DateTimeOffset fileModified,
        bool includeEmpty)
    {
        var requests = document["requests"] as JsonArray;
        var requestCount = requests?.Count ?? 0;

        if (requestCount == 0 && !includeEmpty)
        {
            return null;
        }

        var created = ReadEpoch(document["creationDate"]) ?? fileModified;
        var updated = ReadEpoch(document["lastMessageDate"])
            ?? ReadLastRequestTimestamp(requests)
            ?? fileModified;

        var firstPrompt = requests is { Count: > 0 } ? ReadPromptText(requests[0]) : null;
        var customTitle = document["customTitle"]?.GetValue<string>();

        var models = new List<string>();
        var agent = (string?)null;

        if (requests is not null)
        {
            foreach (var request in requests)
            {
                if (request is not JsonObject requestObject)
                {
                    continue;
                }

                var model = NormalizeModelId(requestObject["modelId"]?.ToString());
                if (!string.IsNullOrWhiteSpace(model) && !models.Contains(model, StringComparer.OrdinalIgnoreCase))
                {
                    models.Add(model);
                }

                agent ??= NormalizeAgentId((requestObject["agent"] as JsonObject)?["id"]?.ToString());
            }
        }

        // Fall back to the model recorded on the composer's input state, which is present even for
        // a session where nothing was sent yet.
        if (models.Count == 0
            && ((document["inputState"] as JsonObject)?["selectedModel"] as JsonObject) is { } selected)
        {
            var model = NormalizeModelId((selected["metadata"] as JsonObject)?["id"]?.ToString() ?? selected["identifier"]?.ToString());
            if (!string.IsNullOrWhiteSpace(model))
            {
                models.Add(model);
            }
        }

        agent ??= ((document["inputState"] as JsonObject)?["mode"] as JsonObject)?["id"]?.ToString();

        return new SessionSummary
        {
            Ref = new SessionRef(CopilotVsCodeSessionSource.SourceId, sessionId),
            Title = !string.IsNullOrWhiteSpace(customTitle) ? customTitle : TextUtilities.DeriveTitle(firstPrompt),
            Preview = TextUtilities.Preview(firstPrompt, 200),
            Workspace = workspace,
            Agent = agent,
            Models = models,
            CreatedAt = created,
            UpdatedAt = updated,
            Stats = new SessionStats { MessageCount = requestCount },
            // VS Code rewrites the whole file on change, so size plus modification time is a
            // reliable and very cheap change token.
            ContentHash = $"{fileModified.ToUnixTimeMilliseconds()}:{requestCount}"
        };
    }

    /// <summary>Expands the full conversation.</summary>
    public static SessionTranscript Build(JsonObject document, SessionSummary summary, TranscriptOptions options)
    {
        var requests = document["requests"] as JsonArray ?? [];
        var turns = new List<Turn>();
        var files = new Dictionary<string, TouchedFile>(StringComparer.OrdinalIgnoreCase);
        var references = new List<SessionReference>();
        var toolCalls = 0;
        var characters = 0;
        var truncated = false;
        int? nextTurn = null;

        for (var index = 0; index < requests.Count; index++)
        {
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

            if (requests[index] is not JsonObject request)
            {
                continue;
            }

            var timestamp = ReadEpoch(request["timestamp"]);
            var model = NormalizeModelId(request["modelId"]?.ToString());
            var agent = NormalizeAgentId((request["agent"] as JsonObject)?["id"]?.ToString());

            var prompt = ReadPromptText(request);
            if (!string.IsNullOrWhiteSpace(prompt))
            {
                turns.Add(new Turn
                {
                    Index = turns.Count,
                    Role = TurnRole.User,
                    Timestamp = timestamp,
                    Blocks = [new TextBlock(prompt)]
                });

                characters += prompt.Length;
            }

            var blocks = ReadResponseBlocks(request["response"] as JsonArray, options, files, ref toolCalls);
            if (blocks.Count > 0)
            {
                turns.Add(new Turn
                {
                    Index = turns.Count,
                    Role = TurnRole.Assistant,
                    Timestamp = timestamp,
                    Model = model,
                    Agent = agent,
                    Blocks = blocks
                });

                characters += blocks.Sum(EstimateSize);
            }

            CollectReferences(request["contentReferences"] as JsonArray, index, files, references);

            if (options.MaxCharacters > 0 && characters > options.MaxCharacters && index < requests.Count - 1)
            {
                nextTurn = index + 1;
                truncated = true;
                break;
            }
        }

        return new SessionTranscript
        {
            Summary = summary with
            {
                Stats = summary.Stats with { ToolCallCount = toolCalls }
            },
            Turns = turns,
            Files = [.. files.Values.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)],
            References = references,
            TotalTurns = requests.Count,
            NextTurnIndex = nextTurn,
            IsTruncated = truncated
        };
    }

    private static List<ContentBlock> ReadResponseBlocks(
        JsonArray? response,
        TranscriptOptions options,
        Dictionary<string, TouchedFile> files,
        ref int toolCalls)
    {
        var blocks = new List<ContentBlock>();
        if (response is null)
        {
            return blocks;
        }

        // Markdown arrives as many small parts as the answer streams in. Merging adjacent runs
        // produces one readable block per answer instead of hundreds of fragments.
        var markdown = new System.Text.StringBuilder();

        void FlushMarkdown()
        {
            if (markdown.Length == 0)
            {
                return;
            }

            var text = markdown.ToString().Trim();
            markdown.Clear();
            if (text.Length > 0)
            {
                blocks.Add(new TextBlock(text));
            }
        }

        foreach (var part in response)
        {
            if (part is not JsonObject element)
            {
                continue;
            }

            var kind = element["kind"]?.ToString();

            if (kind is null or "markdownContent")
            {
                var value = ReadValueString(element["value"]);
                if (!string.IsNullOrEmpty(value))
                {
                    markdown.Append(value);
                }

                continue;
            }

            switch (kind)
            {
                case "thinking":
                {
                    if (!options.IncludeReasoning)
                    {
                        continue;
                    }

                    var text = ReadValueString(element["value"]) ?? ReadValueString(element["text"]);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        FlushMarkdown();
                        blocks.Add(new ReasoningBlock(text));
                    }

                    break;
                }

                case "toolInvocationSerialized" or "toolInvocation":
                {
                    toolCalls++;
                    FlushMarkdown();
                    blocks.Add(ReadToolInvocation(element, options, files));
                    break;
                }

                case "textEditGroup":
                {
                    var uri = ReadUri(element["uri"]);
                    if (uri is not null)
                    {
                        files.TryAdd(uri, new TouchedFile { Path = uri, Tool = "edit" });
                        FlushMarkdown();
                        blocks.Add(new PatchBlock { Path = uri });
                    }

                    break;
                }

                case "inlineReference":
                {
                    var uri = ReadUri((element["inlineReference"] as JsonObject)?["uri"] ?? element["inlineReference"]);
                    if (uri is not null)
                    {
                        files.TryAdd(uri, new TouchedFile { Path = uri, Tool = "reference" });
                    }

                    break;
                }

                default:
                    // Presentation scaffolding: mcpServersStarting, undoStop, codeblockUri,
                    // prepareToolInvocation, progressMessage, and whatever a future build adds.
                    break;
            }
        }

        FlushMarkdown();
        return blocks;
    }

    private static ToolCallBlock ReadToolInvocation(JsonObject element, TranscriptOptions options, Dictionary<string, TouchedFile> files)
    {
        var label = ReadValueString(element["invocationMessage"]) ?? ReadValueString(element["pastTenseMessage"]);
        var toolId = element["toolId"]?.ToString();
        var specific = element["toolSpecificData"] as JsonObject;

        if (specific is not null && ReadUri(specific["uri"]) is { } uri)
        {
            files.TryAdd(uri, new TouchedFile { Path = uri, Tool = toolId ?? "tool" });
        }

        string? output = null;
        if (options.IncludeToolOutput)
        {
            output = ReadValueString(element["resultDetails"]) ?? specific?.ToJsonString();

            if (output is not null && options.MaxToolOutputCharacters > 0 && output.Length > options.MaxToolOutputCharacters)
            {
                output = string.Concat(
                    output.AsSpan(0, options.MaxToolOutputCharacters),
                    $"\n… [{output.Length - options.MaxToolOutputCharacters:N0} more characters]");
            }
        }

        return new ToolCallBlock
        {
            // toolId is a fully qualified extension id such as "copilot_searchCodebase"; the
            // invocation message is what the user actually saw.
            ToolName = toolId ?? "tool",
            Title = label,
            Output = output,
            Status = element["isComplete"]?.GetValue<bool>() switch
            {
                true => "completed",
                false => "running",
                _ => null
            },
            CallId = element["toolCallId"]?.ToString()
        };
    }

    private static void CollectReferences(
        JsonArray? contentReferences,
        int turnIndex,
        Dictionary<string, TouchedFile> files,
        List<SessionReference> references)
    {
        if (contentReferences is null)
        {
            return;
        }

        foreach (var entry in contentReferences)
        {
            if (entry is not JsonObject reference)
            {
                continue;
            }

            var value = reference["reference"];
            var uri = ReadUri(value) ?? ReadUri((value as JsonObject)?["uri"]);
            if (uri is null)
            {
                continue;
            }

            if (uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                references.Add(new SessionReference { Type = "url", Value = uri, TurnIndex = turnIndex });
            }
            else
            {
                files.TryAdd(uri, new TouchedFile { Path = uri, Tool = "reference", TurnIndex = turnIndex });
            }
        }
    }

    private static string? ReadPromptText(JsonNode? request)
    {
        if (request is not JsonObject requestObject || requestObject["message"] is not { } message)
        {
            return null;
        }

        if (message is JsonValue)
        {
            return message.ToString();
        }

        return (message as JsonObject)?["text"]?.ToString();
    }

    /// <summary>
    /// Reads a value that is either a bare string or VS Code's <c>{ value, supportThemeIcons, … }</c>
    /// markdown envelope.
    /// </summary>
    private static string? ReadValueString(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonObject obj => obj["value"]?.ToString(),
        _ => null
    };

    private static string? ReadUri(JsonNode? node)
    {
        var raw = node switch
        {
            null => null,
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonObject obj => obj["fsPath"]?.ToString() ?? obj["external"]?.ToString() ?? BuildUriFromParts(obj),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? VsCodeWorkspaceMap.ToLocalPath(raw)
            : raw;
    }

    private static string? BuildUriFromParts(JsonObject obj)
    {
        // A serialised VS Code URI is { scheme, authority, path, query, fragment }.
        var scheme = obj["scheme"]?.ToString();
        var path = obj["path"]?.ToString();
        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase)
            ? VsCodeWorkspaceMap.ToLocalPath("file://" + path)
            : $"{scheme}://{obj["authority"]}{path}";
    }

    private static DateTimeOffset? ReadEpoch(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.GetValueKind() == JsonValueKind.Number && node.GetValue<long>() is var epoch and > 0)
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(epoch);
        }

        return node.GetValueKind() == JsonValueKind.String
            && DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : null;
    }

    private static DateTimeOffset? ReadLastRequestTimestamp(JsonArray? requests)
    {
        if (requests is null)
        {
            return null;
        }

        for (var index = requests.Count - 1; index >= 0; index--)
        {
            if (requests[index] is JsonObject request && ReadEpoch(request["timestamp"]) is { } timestamp)
            {
                return timestamp;
            }
        }

        return null;
    }

    /// <summary>Strips the <c>copilot/</c> vendor prefix VS Code puts on model identifiers.</summary>
    private static string? NormalizeModelId(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var separator = modelId.IndexOf('/', StringComparison.Ordinal);
        return separator >= 0 && separator < modelId.Length - 1 ? modelId[(separator + 1)..] : modelId;
    }

    /// <summary>
    /// Reduces <c>github.copilot.editsAgent</c> to <c>editsAgent</c>, which is what the mode is
    /// actually called in the UI.
    /// </summary>
    private static string? NormalizeAgentId(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        var separator = agentId.LastIndexOf('.');
        return separator >= 0 && separator < agentId.Length - 1 ? agentId[(separator + 1)..] : agentId;
    }

    private static int EstimateSize(ContentBlock block) => block switch
    {
        TextBlock text => text.Text.Length,
        ReasoningBlock reasoning => reasoning.Text.Length,
        ToolCallBlock tool => (tool.Output?.Length ?? 0) + (tool.Title?.Length ?? 0),
        PatchBlock patch => patch.Path.Length + (patch.Diff?.Length ?? 0),
        _ => 0
    };
}
