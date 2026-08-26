namespace Zakira.Retrace.Core.Configuration;

/// <summary>
/// Resolves every path Retrace reads or writes.
/// </summary>
/// <remarks>
/// <para>
/// Two distinct roots are involved and they are deliberately not the same directory:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     The <b>config root</b> holds <c>retrace.json</c>. It is XDG-first so it lands in the user's
///     dotfiles next to the other Zakira tools' configuration, which means it is versioned,
///     portable, and hand-editable.
///     </description>
///   </item>
///   <item>
///     <description>
///     The <b>data root</b> holds the search index and downloaded ONNX models. Both are large,
///     machine-specific, and fully rebuildable, so they belong in a cache location
///     (<c>LOCALAPPDATA</c> / <c>XDG_DATA_HOME</c>) and must never end up in dotfiles.
///     </description>
///   </item>
/// </list>
/// </remarks>
public sealed class RetracePaths(ISystemEnvironment environment)
{
    /// <summary>Directory name used under every root, matching the other Zakira tools.</summary>
    public const string ApplicationDirectoryName = "Zakira.Retrace";

    /// <summary>Config file name.</summary>
    public const string ConfigFileName = "retrace.json";

    /// <summary>Overrides the full path to <c>retrace.json</c>.</summary>
    public const string ConfigPathVariable = "RETRACE_CONFIG_PATH";

    /// <summary>Overrides the data root that holds the index and models.</summary>
    public const string DataDirectoryVariable = "RETRACE_DATA_DIRECTORY";

    private readonly ISystemEnvironment environment = environment;

    /// <summary>Uses the real process environment.</summary>
    public RetracePaths() : this(SystemEnvironment.Instance)
    {
    }

    /// <summary>
    /// Full path to <c>retrace.json</c>.
    /// Precedence: <c>RETRACE_CONFIG_PATH</c>, then <c>XDG_CONFIG_HOME</c>, then the platform default.
    /// </summary>
    public string ConfigFilePath
    {
        get
        {
            var configured = environment.GetEnvironmentVariable(ConfigPathVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Normalize(configured);
            }

            return Path.Combine(ConfigDirectory, ConfigFileName);
        }
    }

    /// <summary>Directory that contains <c>retrace.json</c>.</summary>
    public string ConfigDirectory
    {
        get
        {
            var configured = environment.GetEnvironmentVariable(ConfigPathVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Path.GetDirectoryName(Normalize(configured)) ?? Normalize(configured);
            }

            return Path.Combine(ConfigRoot, ApplicationDirectoryName);
        }
    }

    /// <summary>
    /// The XDG config root, or its platform equivalent. Exposed so <c>$XDG_CONFIG_HOME</c> can be
    /// expanded inside config values the way the sibling Zakira tools do.
    /// </summary>
    public string ConfigRoot
    {
        get
        {
            var xdg = environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
            {
                return Normalize(xdg);
            }

            if (environment.IsWindows)
            {
                return environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            }

            if (environment.IsMacOs)
            {
                return Path.Combine(environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
            }

            return Path.Combine(environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
    }

    /// <summary>
    /// Root for machine-local, rebuildable state: the search index and downloaded models.
    /// Precedence: <c>RETRACE_DATA_DIRECTORY</c>, then <c>XDG_DATA_HOME</c>, then the platform default.
    /// </summary>
    public string DataDirectory
    {
        get
        {
            var configured = environment.GetEnvironmentVariable(DataDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Normalize(configured);
            }

            var xdg = environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg))
            {
                return Path.Combine(Normalize(xdg), ApplicationDirectoryName);
            }

            if (environment.IsWindows)
            {
                return Path.Combine(environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ApplicationDirectoryName);
            }

            if (environment.IsMacOs)
            {
                return Path.Combine(environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", ApplicationDirectoryName);
            }

            return Path.Combine(environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", ApplicationDirectoryName);
        }
    }

    /// <summary>Default path to the search index database.</summary>
    public string DefaultIndexPath => Path.Combine(DataDirectory, "index.db");

    /// <summary>Directory that holds downloaded ONNX embedding models, one subdirectory per model id.</summary>
    public string ModelsDirectory => Path.Combine(DataDirectory, "models");

    /// <summary>
    /// Scratch directory for snapshot copies of session databases that are locked by a running
    /// harness. Contents are disposable and cleaned up after each use.
    /// </summary>
    public string SnapshotDirectory => Path.Combine(environment.GetTempPath(), ApplicationDirectoryName, "snapshots");

    /// <summary>Home directory of the current user.</summary>
    public string UserProfile => environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Roaming application data, which is where VS Code keeps per-user storage.</summary>
    public string RoamingAppData => environment.IsWindows
        ? environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        : Path.Combine(UserProfile, ".config");

    /// <summary>
    /// Expands <c>$XDG_CONFIG_HOME</c>, <c>$XDG_DATA_HOME</c>, <c>~</c>, and platform environment
    /// variable syntax inside a configured path, then normalises it to a full path.
    /// </summary>
    /// <remarks>
    /// Config values are stored verbatim rather than pre-expanded so that a dotfiles repository
    /// synchronised across machines keeps working: the same <c>$XDG_CONFIG_HOME/...</c> literal
    /// resolves correctly on each one.
    /// </remarks>
    public string ExpandPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var expanded = value
            .Replace("$XDG_CONFIG_HOME", ConfigRoot, StringComparison.Ordinal)
            .Replace("${XDG_CONFIG_HOME}", ConfigRoot, StringComparison.Ordinal)
            .Replace("$XDG_DATA_HOME", DataDirectory, StringComparison.Ordinal)
            .Replace("${XDG_DATA_HOME}", DataDirectory, StringComparison.Ordinal);

        if (expanded.StartsWith('~'))
        {
            expanded = UserProfile + expanded[1..];
        }

        return Normalize(expanded);
    }

    /// <summary>
    /// Expands environment variables and resolves relative segments to a canonical full path.
    /// </summary>
    /// <remarks>
    /// The trailing separator is deliberately removed. <see cref="Path.GetFullPath(string)"/>
    /// preserves whatever the input had, so <c>.../config</c> and <c>.../config/</c> would produce
    /// different strings for the same directory. That inconsistency then leaks into
    /// <c>$XDG_CONFIG_HOME</c> expansion (yielding <c>config\/skills</c>) and into every path
    /// comparison. A drive or filesystem root is left intact, since its separator is significant.
    /// </remarks>
    private static string Normalize(string value)
    {
        var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));

        if (full.Length <= 1 || Path.GetPathRoot(full) == full)
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
