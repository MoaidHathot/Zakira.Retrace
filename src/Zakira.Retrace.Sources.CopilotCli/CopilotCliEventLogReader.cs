using System.Text.Json;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Json;

namespace Zakira.Retrace.Sources.CopilotCli;

/// <summary>
/// Reads a Copilot CLI session's append-only event log to recover tool calls.
/// </summary>
/// <remarks>
/// <para>
/// The SQLite store records only the prose halves of each exchange: <c>user_message</c> and
/// <c>assistant_response</c>. Tool invocations exist solely in the JSONL event log written
/// alongside it, so this reader is the only path to them. The log is optional and frequently
/// absent — Copilot CLI prunes it far more aggressively than the database — which is why it is
/// treated as enrichment rather than as the primary source.
/// </para>
/// <para>Event shapes consumed here:</para>
/// <code>
/// {"type":"assistant.message","data":{"messageId":"…","content":"…","toolRequests":[{"toolCallId":"…","name":"…","arguments":{…}}]}}
/// {"type":"tool.execution_start","data":{"toolCallId":"…","toolName":"…","arguments":{…}}}
/// {"type":"tool.execution_complete","data":{"toolCallId":"…","success":true,"result":{"content":"…"}}}
/// </code>
/// </remarks>
internal static class CopilotCliEventLogReader
{
    /// <summary>
    /// Refuses to parse a log beyond this size. A runaway session log can reach hundreds of
    /// megabytes, and tool call enrichment is never worth stalling a command for.
    /// </summary>
    private const long MaxLogBytes = 32L * 1024 * 1024;

    /// <summary>
    /// Reads tool calls out of a session's event log, keyed by turn index where one can be inferred.
    /// Returns an empty list when the log is missing, oversized, or unparseable.
    /// </summary>
    public static async Task<IReadOnlyList<ToolCallBlock>> ReadToolCallsAsync(
        string logPath,
        int maxOutputCharacters,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(logPath))
        {
            return [];
        }

        var info = new FileInfo(logPath);
        if (info.Length > MaxLogBytes)
        {
            return [];
        }

        var starts = new Dictionary<string, (string ToolName, string? Arguments)>(StringComparer.Ordinal);
        var completions = new Dictionary<string, (bool Success, string? Output)>(StringComparer.Ordinal);
        var order = new List<string>();

        try
        {
            await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                using var document = RetraceJson.TryParse(line);
                if (document is null)
                {
                    continue;
                }

                var root = document.RootElement;
                var type = root.GetStringOrNull("type");
                var data = root.Path("data");
                if (data is not { ValueKind: JsonValueKind.Object } payload)
                {
                    continue;
                }

                switch (type)
                {
                    case "tool.execution_start":
                    {
                        var callId = payload.GetStringOrNull("toolCallId");
                        if (callId is null)
                        {
                            continue;
                        }

                        if (!starts.ContainsKey(callId))
                        {
                            order.Add(callId);
                        }

                        starts[callId] = (payload.GetStringOrNull("toolName") ?? "tool", payload.Path("arguments").ToRawJson());
                        break;
                    }

                    case "tool.execution_complete":
                    {
                        var callId = payload.GetStringOrNull("toolCallId");
                        if (callId is null)
                        {
                            continue;
                        }

                        var output = payload.Path("result")?.GetStringOrNull("content")
                            ?? payload.Path("result").ToDisplayText();

                        if (output is not null && maxOutputCharacters > 0 && output.Length > maxOutputCharacters)
                        {
                            output = string.Concat(
                                output.AsSpan(0, maxOutputCharacters),
                                $"\n… [{output.Length - maxOutputCharacters:N0} more characters]");
                        }

                        completions[callId] = (payload.GetBoolOrNull("success") ?? true, output);
                        break;
                    }
                }
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        var blocks = new List<ToolCallBlock>(order.Count);
        foreach (var callId in order)
        {
            var start = starts[callId];
            completions.TryGetValue(callId, out var completion);

            blocks.Add(new ToolCallBlock
            {
                ToolName = start.ToolName,
                InputJson = start.Arguments,
                Output = completion.Output,
                Status = completions.ContainsKey(callId) ? (completion.Success ? "completed" : "error") : "running",
                CallId = callId
            });
        }

        return blocks;
    }
}
