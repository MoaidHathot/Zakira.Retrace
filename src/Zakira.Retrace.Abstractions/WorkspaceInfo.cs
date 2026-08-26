namespace Zakira.Retrace.Abstractions;

/// <summary>
/// Where a session was recorded. Every supported harness records at least a working directory;
/// only some record repository and branch, so the extra fields are optional.
/// </summary>
public sealed record WorkspaceInfo
{
    /// <summary>Absolute working directory the session ran in.</summary>
    public string? Path { get; init; }

    /// <summary>Repository identifier as the harness recorded it (may be a URL, an owner/name pair, or a path).</summary>
    public string? Repository { get; init; }

    /// <summary>Git branch at the time the session was recorded.</summary>
    public string? Branch { get; init; }

    /// <summary>
    /// A short label for display. Falls back to the last path segment when the harness did not
    /// record a friendly name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>The best available short label, computed from whatever fields are populated.</summary>
    public string Label
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DisplayName))
            {
                return DisplayName;
            }

            if (!string.IsNullOrWhiteSpace(Path))
            {
                var trimmed = Path.TrimEnd('/', '\\');
                var index = trimmed.LastIndexOfAny(['/', '\\']);
                return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
            }

            return Repository ?? "(unknown)";
        }
    }
}
