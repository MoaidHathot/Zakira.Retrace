using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Sources.OpenCode;

/// <summary>
/// Locates OpenCode's data directory and decides which of its two stores to read.
/// </summary>
/// <remarks>
/// OpenCode historically kept sessions as a tree of JSON files under <c>storage/</c> and has since
/// migrated to a single SQLite database, writing both during the transition. The database is
/// authoritative and dramatically faster to query, so it wins whenever it is present and
/// non-trivial; the JSON tree remains as a fallback for older installs.
/// </remarks>
internal sealed class OpenCodeLayout(RetracePaths paths, OpenCodeSourceConfig config)
{
    /// <summary>Environment variable OpenCode itself honours for relocating its data directory.</summary>
    private const string DataVariable = "OPENCODE_DATA";

    /// <summary>Resolved OpenCode data directory, whether or not it exists.</summary>
    public string DataDirectory { get; } = ResolveDataDirectory(paths, config);

    /// <summary>Path to the primary SQLite database.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, "opencode.db");

    /// <summary>
    /// Path to the auxiliary local database. Some builds split unshared state out of the main
    /// database; it carries the same session schema, so it is read as a secondary store.
    /// </summary>
    public string LocalDatabasePath => Path.Combine(DataDirectory, "opencode-local.db");

    /// <summary>Root of the legacy JSON tree.</summary>
    public string StorageDirectory => Path.Combine(DataDirectory, "storage");

    /// <summary>Whether the SQLite database exists and holds more than an empty schema.</summary>
    public bool HasDatabase
    {
        get
        {
            var file = new FileInfo(DatabasePath);
            // An empty SQLite file is 0 or exactly one page. Anything at or below that has no rows
            // worth preferring over the JSON tree.
            return file.Exists && file.Length > 8192;
        }
    }

    /// <summary>Whether the legacy JSON tree exists.</summary>
    public bool HasJsonStore => Directory.Exists(Path.Combine(StorageDirectory, "session"));

    /// <summary>Which store this layout will read from.</summary>
    public OpenCodeStoreKind StoreKind
    {
        get
        {
            if (config.PreferLegacyJsonStore && HasJsonStore)
            {
                return OpenCodeStoreKind.Json;
            }

            if (HasDatabase)
            {
                return OpenCodeStoreKind.Sqlite;
            }

            return HasJsonStore ? OpenCodeStoreKind.Json : OpenCodeStoreKind.None;
        }
    }

    private static string ResolveDataDirectory(RetracePaths paths, OpenCodeSourceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.DataDirectory))
        {
            return paths.ExpandPath(config.DataDirectory);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return paths.ExpandPath(fromEnvironment);
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdgDataHome))
        {
            return Path.Combine(paths.ExpandPath(xdgDataHome), "opencode");
        }

        // OpenCode uses the XDG layout on every platform, including Windows, rather than
        // %APPDATA%. This is unusual but consistent across its releases.
        return Path.Combine(paths.UserProfile, ".local", "share", "opencode");
    }
}

/// <summary>Which OpenCode store is in use.</summary>
internal enum OpenCodeStoreKind
{
    /// <summary>Neither store was found.</summary>
    None,

    /// <summary>The current SQLite database.</summary>
    Sqlite,

    /// <summary>The legacy JSON file tree.</summary>
    Json
}
