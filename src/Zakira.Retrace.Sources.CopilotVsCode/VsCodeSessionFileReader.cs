using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zakira.Retrace.Sources.CopilotVsCode;

/// <summary>
/// Loads a Copilot Chat session file into a single mutable JSON document.
/// </summary>
/// <remarks>
/// <para>Two on-disk formats are in circulation and both must be supported:</para>
/// <list type="bullet">
///   <item><description><c>.json</c> — the whole session as one object. Simply parsed.</description></item>
///   <item><description><c>.jsonl</c> — an append-only delta log. Line one is
///   <c>{"kind":0,"v":{…}}</c>, a full snapshot. Every later line is
///   <c>{"kind":1|2,"k":[…path…],"v":…}</c>, where <c>kind 1</c> assigns <c>v</c> at the path and
///   <c>kind 2</c> appends the elements of <c>v</c> to the array at that path. Path segments are
///   property names or array indices.</description></item>
/// </list>
/// <para>
/// The delta form has to be genuinely replayed rather than approximated. Assistant responses are
/// streamed in as <c>kind 2</c> appends to <c>requests[i].response</c>, so any shortcut that only
/// looks at whole request objects recovers the user's prompts and almost none of the answers.
/// </para>
/// </remarks>
internal static class VsCodeSessionFileReader
{
    /// <summary>
    /// Refuses to load a session larger than this. A single chat log can reach tens of megabytes
    /// once large tool results are inlined, and no listing should stall on one pathological file.
    /// </summary>
    public const long MaxSessionBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Reads a session file into a JSON object, or returns <see langword="null"/> when the file is
    /// missing, oversized, or too malformed to recover.
    /// </summary>
    public static async Task<JsonObject?> LoadAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxSessionBytes)
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            return path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? await LoadDeltaLogAsync(path, cancellationToken).ConfigureAwait(false)
                : await LoadDocumentAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<JsonObject?> LoadDocumentAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenShared(path);
        var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return node as JsonObject;
    }

    private static async Task<JsonObject?> LoadDeltaLogAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = OpenShared(path);
        using var reader = new StreamReader(stream);

        JsonObject? state = null;

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            JsonObject? entry;
            try
            {
                entry = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException)
            {
                // A torn final line is expected when VS Code is mid-write. Everything replayed so
                // far is still valid, so stop rather than discard the session.
                break;
            }

            if (entry is null || !entry.TryGetPropertyValue("kind", out var kindNode) || kindNode is null)
            {
                continue;
            }

            var kind = kindNode.GetValue<int>();
            entry.TryGetPropertyValue("v", out var value);

            switch (kind)
            {
                case 0:
                    state = value?.DeepClone() as JsonObject;
                    break;

                case 1 when state is not null:
                    ApplySet(state, ReadPath(entry), value);
                    break;

                case 2 when state is not null:
                    ApplyAppend(state, ReadPath(entry), value);
                    break;

                default:
                    // Unknown op from a newer VS Code build. Skipping loses fidelity but keeps the
                    // rest of the session readable, which is strictly better than failing.
                    break;
            }
        }

        return state;
    }

    private static FileStream OpenShared(string path) =>
        // FileShare.ReadWrite because VS Code holds these files open and appends to them live.
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static IReadOnlyList<JsonNode?> ReadPath(JsonObject entry) =>
        entry.TryGetPropertyValue("k", out var key) && key is JsonArray array
            ? [.. array]
            : [];

    private static void ApplySet(JsonObject root, IReadOnlyList<JsonNode?> path, JsonNode? value)
    {
        if (path.Count == 0)
        {
            return;
        }

        var parent = Traverse(root, path, path.Count - 1);
        AssignAt(parent, path[^1], value?.DeepClone());
    }

    private static void ApplyAppend(JsonObject root, IReadOnlyList<JsonNode?> path, JsonNode? value)
    {
        if (path.Count == 0 || value is not JsonArray items)
        {
            return;
        }

        var parent = Traverse(root, path, path.Count - 1);
        var target = ReadAt(parent, path[^1]);

        if (target is not JsonArray array)
        {
            array = [];
            AssignAt(parent, path[^1], array);
        }

        foreach (var item in items)
        {
            array.Add(item?.DeepClone());
        }
    }

    /// <summary>
    /// Walks the first <paramref name="length"/> path segments, materialising containers that do
    /// not exist yet so a delta targeting a not-yet-created object still applies.
    /// </summary>
    private static JsonNode? Traverse(JsonNode root, IReadOnlyList<JsonNode?> path, int length)
    {
        var current = root;
        for (var index = 0; index < length; index++)
        {
            var next = ReadAt(current, path[index]);
            if (next is null)
            {
                // Look ahead: a numeric next segment means the missing container is an array.
                next = IsIndex(path[index + 1]) ? new JsonArray() : new JsonObject();
                AssignAt(current, path[index], next);
            }

            current = next;
        }

        return current;
    }

    private static JsonNode? ReadAt(JsonNode? container, JsonNode? segment)
    {
        switch (container)
        {
            case JsonObject obj when segment is not null && segment.GetValueKind() == JsonValueKind.String:
                return obj.TryGetPropertyValue(segment.GetValue<string>(), out var value) ? value : null;

            case JsonArray array when TryGetIndex(segment, out var index):
                return index >= 0 && index < array.Count ? array[index] : null;

            default:
                return null;
        }
    }

    private static void AssignAt(JsonNode? container, JsonNode? segment, JsonNode? value)
    {
        switch (container)
        {
            case JsonObject obj when segment is not null && segment.GetValueKind() == JsonValueKind.String:
                obj[segment.GetValue<string>()] = value;
                break;

            case JsonArray array when TryGetIndex(segment, out var index):
                // Grow with nulls rather than throwing: deltas can arrive for an index beyond the
                // current length when an intermediate write was lost.
                while (array.Count <= index)
                {
                    array.Add(null);
                }

                array[index] = value;
                break;
        }
    }

    private static bool IsIndex(JsonNode? segment) => segment?.GetValueKind() == JsonValueKind.Number;

    private static bool TryGetIndex(JsonNode? segment, out int index)
    {
        if (segment is not null && segment.GetValueKind() == JsonValueKind.Number)
        {
            index = segment.GetValue<int>();
            return true;
        }

        index = -1;
        return false;
    }
}
