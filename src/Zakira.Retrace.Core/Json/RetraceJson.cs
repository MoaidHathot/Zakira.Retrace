using System.Text.Json;

namespace Zakira.Retrace.Core.Json;

/// <summary>
/// Shared <see cref="System.Text.Json"/> settings and small helpers for reading the semi-structured
/// blobs that harnesses store.
/// </summary>
/// <remarks>
/// Every session store keeps at least part of its payload as free-form JSON whose shape drifts
/// between harness releases. Reading it defensively, element by element, keeps a single unexpected
/// field from failing an entire listing.
/// </remarks>
public static class RetraceJson
{
    /// <summary>Camel-cased, case-insensitive options used for both harness payloads and CLI output.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Indented variant, for anything a human will read.</summary>
    public static JsonSerializerOptions IndentedOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Parses a JSON document, returning <see langword="null"/> instead of throwing on malformed
    /// input.
    /// </summary>
    public static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a nested property by path, for example <c>time</c> then <c>created</c>.</summary>
    public static JsonElement? Path(this JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>Reads a string property, or <see langword="null"/> when absent or not a string.</summary>
    public static string? GetStringOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    /// <summary>Reads an integer property, tolerating values stored as strings.</summary>
    public static long? GetInt64OrNull(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    /// <summary>Reads a floating point property.</summary>
    public static double? GetDoubleOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
                ? number
                : null;

    /// <summary>Reads a boolean property.</summary>
    public static bool? GetBoolOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            }
            : null;

    /// <summary>Converts epoch milliseconds to an instant, treating non-positive values as absent.</summary>
    public static DateTimeOffset? FromEpochMilliseconds(long? value) =>
        value is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null;

    /// <summary>
    /// Renders an element as compact JSON text. Used for tool arguments, which are passed through
    /// verbatim rather than re-modelled.
    /// </summary>
    public static string? ToRawJson(this JsonElement? element) =>
        element is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null }
            ? element.Value.GetRawText()
            : null;

    /// <summary>
    /// Flattens an element to display text: strings pass through, everything else is serialised.
    /// Tool output is sometimes a plain string and sometimes a structured object.
    /// </summary>
    public static string? ToDisplayText(this JsonElement? element)
    {
        if (element is not { } value)
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => value.GetRawText()
        };
    }
}
