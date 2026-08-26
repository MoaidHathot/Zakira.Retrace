using System.Runtime.InteropServices;

namespace Zakira.Retrace.Core.Configuration;

/// <summary>
/// Abstraction over environment variables and well-known folders, so path resolution can be
/// exercised in tests without mutating the real process environment.
/// </summary>
public interface ISystemEnvironment
{
    /// <summary>Reads an environment variable.</summary>
    string? GetEnvironmentVariable(string name);

    /// <summary>Resolves a well-known folder.</summary>
    string GetFolderPath(Environment.SpecialFolder folder);

    /// <summary>Resolves the temporary directory. Abstracted so tests can redirect scratch output.</summary>
    string GetTempPath();

    /// <summary>Whether the process is running on Windows.</summary>
    bool IsWindows { get; }

    /// <summary>Whether the process is running on macOS.</summary>
    bool IsMacOs { get; }
}

/// <summary>The real environment.</summary>
public sealed class SystemEnvironment : ISystemEnvironment
{
    /// <summary>A shared instance; the type is stateless.</summary>
    public static SystemEnvironment Instance { get; } = new();

    /// <inheritdoc />
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    /// <inheritdoc />
    public string GetFolderPath(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);

    /// <inheritdoc />
    public string GetTempPath() => Path.GetTempPath();

    /// <inheritdoc />
    public bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public bool IsMacOs => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
}
