using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Sources.CopilotCli;

/// <summary>
/// Locates GitHub Copilot CLI's data directory and the files inside it.
/// </summary>
internal sealed class CopilotCliLayout(RetracePaths paths, CopilotCliSourceConfig config)
{
    /// <summary>Environment variable Copilot CLI honours for relocating its home directory.</summary>
    private const string HomeVariable = "COPILOT_HOME";

    /// <summary>Resolved data directory, whether or not it exists.</summary>
    public string DataDirectory { get; } = ResolveDataDirectory(paths, config);

    /// <summary>The SQLite store holding sessions, turns, usage, and the full-text index.</summary>
    public string DatabasePath => Path.Combine(DataDirectory, "session-store.db");

    /// <summary>
    /// Directory of per-session event logs. Current builds keep a subdirectory per session id;
    /// older builds wrote a flat <c>&lt;sessionId&gt;.jsonl</c>. Both forms are handled.
    /// </summary>
    public string SessionStateDirectory => Path.Combine(DataDirectory, "session-state");

    /// <summary>Path to a session's flat event log, which may or may not exist.</summary>
    public string GetEventLogPath(string sessionId) => Path.Combine(SessionStateDirectory, $"{sessionId}.jsonl");

    /// <summary>Whether the SQLite store is present.</summary>
    public bool HasDatabase => File.Exists(DatabasePath);

    private static string ResolveDataDirectory(RetracePaths paths, CopilotCliSourceConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.DataDirectory))
        {
            return paths.ExpandPath(config.DataDirectory);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(HomeVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return paths.ExpandPath(fromEnvironment);
        }

        // Copilot CLI uses ~/.copilot on every platform.
        return Path.Combine(paths.UserProfile, ".copilot");
    }
}
