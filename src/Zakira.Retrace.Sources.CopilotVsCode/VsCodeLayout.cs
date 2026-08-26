using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Sources.CopilotVsCode;

/// <summary>One VS Code installation whose chat sessions can be read.</summary>
internal sealed record VsCodeInstall(string Id, string DisplayName, string WorkspaceStorageDirectory)
{
    /// <summary>Whether this installation has any workspace storage on disk.</summary>
    public bool Exists => Directory.Exists(WorkspaceStorageDirectory);
}

/// <summary>
/// Locates the VS Code installations to read chat sessions from.
/// </summary>
/// <remarks>
/// Copilot Chat stores its sessions per workspace, under
/// <c>&lt;userData&gt;/User/workspaceStorage/&lt;workspaceHash&gt;/chatSessions/</c>. The hash is an
/// opaque digest, so the human-readable folder has to be recovered from the sibling
/// <c>workspace.json</c>. There is also a <c>globalStorage/github.copilot-chat/session-store.db</c>,
/// but on current builds it is an empty schema, so it is deliberately not read.
/// </remarks>
internal sealed class VsCodeLayout
{
    private static readonly (string Id, string DisplayName, string Directory)[] KnownEditors =
    [
        ("stable", "VS Code", "Code"),
        ("insiders", "VS Code Insiders", "Code - Insiders"),
        ("exploration", "VS Code Exploration", "Code - Exploration")
    ];

    private readonly RetracePaths paths;
    private readonly CopilotVsCodeSourceConfig config;

    /// <summary>Creates the layout.</summary>
    public VsCodeLayout(RetracePaths paths, CopilotVsCodeSourceConfig config)
    {
        this.paths = paths;
        this.config = config;
        Installs = ResolveInstalls();
    }

    /// <summary>Installations selected by config, whether or not each exists on disk.</summary>
    public IReadOnlyList<VsCodeInstall> Installs { get; }

    /// <summary>Installations that actually have workspace storage.</summary>
    public IReadOnlyList<VsCodeInstall> AvailableInstalls => [.. Installs.Where(install => install.Exists)];

    /// <summary>Enumerates every <c>chatSessions</c> directory across the selected installations.</summary>
    public IEnumerable<(VsCodeInstall Install, string WorkspaceHash, string Directory)> EnumerateChatSessionDirectories()
    {
        foreach (var install in AvailableInstalls)
        {
            IEnumerable<string> workspaces;
            try
            {
                workspaces = Directory.EnumerateDirectories(install.WorkspaceStorageDirectory);
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var workspace in workspaces)
            {
                var chatSessions = Path.Combine(workspace, "chatSessions");
                if (Directory.Exists(chatSessions))
                {
                    yield return (install, Path.GetFileName(workspace), chatSessions);
                }
            }
        }
    }

    private List<VsCodeInstall> ResolveInstalls()
    {
        // An explicit dataDirectory points straight at a User directory and disables discovery,
        // which is what a portable or remote install needs.
        if (!string.IsNullOrWhiteSpace(config.DataDirectory))
        {
            var root = paths.ExpandPath(config.DataDirectory);
            var storage = Path.Combine(root, "workspaceStorage");
            return [new VsCodeInstall("custom", "VS Code", Directory.Exists(storage) ? storage : root)];
        }

        var selected = new HashSet<string>(
            config.Editors.Count > 0 ? config.Editors : ["stable"],
            StringComparer.OrdinalIgnoreCase);

        var installs = new List<VsCodeInstall>();
        foreach (var (id, displayName, directoryName) in KnownEditors)
        {
            if (!selected.Contains(id))
            {
                continue;
            }

            installs.Add(new VsCodeInstall(id, displayName, Path.Combine(GetUserDataRoot(), directoryName, "User", "workspaceStorage")));
        }

        return installs;
    }

    private string GetUserDataRoot()
    {
        // VS Code stores user data under %APPDATA% on Windows, ~/Library/Application Support on
        // macOS, and $XDG_CONFIG_HOME (defaulting to ~/.config) on Linux.
        if (OperatingSystem.IsWindows())
        {
            return paths.RoamingAppData;
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(paths.UserProfile, "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(paths.UserProfile, ".config")
            : paths.ExpandPath(xdg);
    }
}
