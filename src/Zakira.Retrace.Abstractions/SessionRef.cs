namespace Zakira.Retrace.Abstractions;

/// <summary>
/// A stable, global handle to a single session in a single source.
/// </summary>
/// <remarks>
/// The canonical string form is <c>retrace://&lt;sourceId&gt;/&lt;nativeId&gt;</c>. It is the only
/// identifier that is safe to persist, print, or hand to an agent: native ids collide across
/// harnesses (Copilot and VS Code both use bare GUIDs) and carry no provenance on their own.
/// </remarks>
public sealed record SessionRef(string SourceId, string NativeId)
{
    /// <summary>The URI scheme used by every Retrace handle.</summary>
    public const string Scheme = "retrace";

    /// <summary>The canonical <c>retrace://source/id</c> string form.</summary>
    public string Uri => $"{Scheme}://{SourceId}/{NativeId}";

    /// <summary>
    /// A short, human-facing form: the first 12 characters of the native id, prefixed with the
    /// source. Used in list output where full ids would dominate the line.
    /// </summary>
    public string ShortForm =>
        $"{SourceId}/{(NativeId.Length <= 12 ? NativeId : NativeId[..12])}";

    /// <summary>
    /// Parses a canonical <c>retrace://source/id</c> URI. Returns <see langword="false"/> for any
    /// other input, including bare native ids, which callers resolve through the catalog instead.
    /// </summary>
    public static bool TryParse(string? value, out SessionRef? result)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        const string prefix = $"{Scheme}://";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = value[prefix.Length..];
        var separator = remainder.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == remainder.Length - 1)
        {
            return false;
        }

        result = new SessionRef(remainder[..separator], remainder[(separator + 1)..]);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Uri;
}
