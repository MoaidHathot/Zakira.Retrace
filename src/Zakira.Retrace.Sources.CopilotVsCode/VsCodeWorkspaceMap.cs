using System.Text.Json;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Sources.CopilotVsCode;

/// <summary>
/// Resolves a VS Code workspace storage hash to the folder it represents.
/// </summary>
/// <remarks>
/// The directory name under <c>workspaceStorage</c> is an opaque digest. The sibling
/// <c>workspace.json</c> holds the mapping, in one of three shapes:
/// <c>{"folder":"file:///…"}</c> for a single folder, <c>{"workspace":"file:///…code-workspace"}</c>
/// for a multi-root workspace, and <c>{"emptyWindow":true}</c> for a window with nothing open.
/// Without this mapping every session would be attributed to a meaningless hash, which would make
/// workspace filtering useless for the source people most often want it for.
/// </remarks>
internal sealed class VsCodeWorkspaceMap
{
    private readonly Dictionary<string, WorkspaceInfo> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();

    /// <summary>Resolves the workspace for a storage directory, caching the result.</summary>
    public WorkspaceInfo Resolve(string workspaceStorageDirectory, string workspaceHash)
    {
        lock (gate)
        {
            if (cache.TryGetValue(workspaceHash, out var cached))
            {
                return cached;
            }
        }

        var resolved = Read(workspaceStorageDirectory, workspaceHash);

        lock (gate)
        {
            cache[workspaceHash] = resolved;
        }

        return resolved;
    }

    private static WorkspaceInfo Read(string workspaceStorageDirectory, string workspaceHash)
    {
        var path = Path.Combine(workspaceStorageDirectory, "workspace.json");
        if (!File.Exists(path))
        {
            return new WorkspaceInfo { DisplayName = workspaceHash };
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.TryGetProperty("folder", out var folder) && folder.ValueKind == JsonValueKind.String)
            {
                var local = ToLocalPath(folder.GetString());
                return new WorkspaceInfo { Path = local, DisplayName = GetLeaf(local) };
            }

            if (root.TryGetProperty("workspace", out var workspace) && workspace.ValueKind == JsonValueKind.String)
            {
                var local = ToLocalPath(workspace.GetString());
                return new WorkspaceInfo
                {
                    // A multi-root workspace is identified by its .code-workspace file; the
                    // containing directory is the closest thing to a working directory.
                    Path = local is null ? null : Path.GetDirectoryName(local),
                    DisplayName = local is null ? workspaceHash : Path.GetFileNameWithoutExtension(local)
                };
            }
        }
        catch (JsonException)
        {
            // Fall through to the hash-only form.
        }
        catch (IOException)
        {
            // Same.
        }

        return new WorkspaceInfo { DisplayName = workspaceHash };
    }

    /// <summary>
    /// Converts a VS Code <c>file://</c> URI to a native path. Drive letters arrive percent-encoded
    /// (<c>file:///p%3A/Github/x</c>), so plain string trimming is not enough.
    /// </summary>
    public static string? ToLocalPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return uri;
        }

        if (!parsed.IsFile)
        {
            return uri;
        }

        try
        {
            var local = parsed.LocalPath;
            if (OperatingSystem.IsWindows())
            {
                // LocalPath yields "/P:/Github/x" for these URIs; strip the leading separator and
                // normalise to backslashes so the value matches what other sources record.
                if (local.Length > 2 && local[0] == '/' && char.IsLetter(local[1]) && local[2] == ':')
                {
                    local = local[1..];
                }

                local = local.Replace('/', '\\');
            }

            return local.TrimEnd('/', '\\');
        }
        catch (InvalidOperationException)
        {
            return uri;
        }
    }

    private static string? GetLeaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.TrimEnd('/', '\\');
        var index = trimmed.LastIndexOfAny(['/', '\\']);
        return index >= 0 && index < trimmed.Length - 1 ? trimmed[(index + 1)..] : trimmed;
    }
}
