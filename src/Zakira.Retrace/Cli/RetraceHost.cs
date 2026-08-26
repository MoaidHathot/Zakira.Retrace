using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.DependencyInjection;
using Zakira.Retrace.Sources.CopilotCli;
using Zakira.Retrace.Sources.CopilotVsCode;
using Zakira.Retrace.Sources.OpenCode;

namespace Zakira.Retrace.Cli;

/// <summary>
/// Builds the service provider shared by every command and by the MCP server.
/// </summary>
/// <remarks>
/// The provider is built lazily, once, on first use. Commands such as <c>version</c> and
/// <c>config path</c> never touch it, which keeps them fast and keeps them working even when
/// configuration is malformed.
/// </remarks>
internal sealed class RetraceHost(string? configPath, bool verbose) : IAsyncDisposable
{
    private ServiceProvider? provider;
    private RetraceConfig? config;
    private ConfigStore? store;

    /// <summary>Path resolution, available without building the container.</summary>
    public RetracePaths Paths { get; } = new();

    /// <summary>The config store for the resolved path.</summary>
    public ConfigStore Store => store ??= new ConfigStore(Paths, configPath);

    /// <summary>Loads configuration, creating the file with full defaults when absent.</summary>
    public async Task<RetraceConfig> GetConfigAsync(CancellationToken cancellationToken)
    {
        if (config is not null)
        {
            return config;
        }

        // EnsureExists rather than Load: the first run should leave a fully populated retrace.json
        // in the user's dotfiles so every option is discoverable by reading the file.
        config = await Store.EnsureExistsAsync(cancellationToken).ConfigureAwait(false);
        ConsoleStyle.Configure(config.Output.Color);
        return config;
    }

    /// <summary>Builds (or returns) the service provider.</summary>
    public async Task<IServiceProvider> GetServicesAsync(CancellationToken cancellationToken)
    {
        if (provider is not null)
        {
            return provider;
        }

        var loaded = await GetConfigAsync(cancellationToken).ConfigureAwait(false);

        var services = new ServiceCollection();

        services.AddLogging(logging =>
        {
            logging.ClearProviders();

            // Every log line goes to stderr, without exception. stdout carries command results and,
            // in MCP mode, the JSON-RPC stream; a stray log line on stdout corrupts both.
            logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
            logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
        });

        services.AddRetraceCore(loaded, Paths);
        services.AddOpenCodeSource();
        services.AddCopilotCliSource();
        services.AddCopilotVsCodeSource();

        provider = services.BuildServiceProvider();
        return provider;
    }

    /// <summary>Resolves a service, building the container if needed.</summary>
    public async Task<T> GetServiceAsync<T>(CancellationToken cancellationToken) where T : notnull
    {
        var services = await GetServicesAsync(cancellationToken).ConfigureAwait(false);
        return services.GetRequiredService<T>();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (provider is not null)
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }
}
