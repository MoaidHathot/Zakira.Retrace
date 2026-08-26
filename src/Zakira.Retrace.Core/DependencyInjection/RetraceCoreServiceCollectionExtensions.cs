using Microsoft.Extensions.DependencyInjection;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Embeddings;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Core.Services;

namespace Zakira.Retrace.Core.DependencyInjection;

/// <summary>
/// Registers the Retrace core services.
/// </summary>
/// <remarks>
/// The CLI and the MCP server share one container and one registration entry point, so a behaviour
/// change made for one is automatically true of the other. Sources register themselves through
/// their own extension methods on top of this.
/// </remarks>
public static class RetraceCoreServiceCollectionExtensions
{
    /// <summary>Adds paths, configuration, index, embeddings, and the catalog.</summary>
    /// <param name="services">Service collection.</param>
    /// <param name="config">Configuration already loaded from disk and merged with CLI overrides.</param>
    /// <param name="paths">Resolved paths, or null to use the real environment.</param>
    public static IServiceCollection AddRetraceCore(this IServiceCollection services, RetraceConfig config, RetracePaths? paths = null)
    {
        var resolvedPaths = paths ?? new RetracePaths();

        services.AddSingleton(resolvedPaths);
        services.AddSingleton(config);
        services.AddSingleton(config.Sources.OpenCode);
        services.AddSingleton(config.Sources.CopilotCli);
        services.AddSingleton(config.Sources.CopilotVsCode);

        services.AddSingleton<ISystemEnvironment>(SystemEnvironment.Instance);

        services.AddHttpClient<EmbeddingModelProvisioner>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"Zakira.Retrace/{RetraceVersion.Current}");
        });

        services.AddSingleton<IEmbeddingProviderFactory, OnnxEmbeddingProviderFactory>();
        services.AddSingleton<IndexSearcher>();
        services.AddSingleton<IndexBuilder>();
        services.AddSingleton<TagStore>();
        services.AddSingleton<SessionCatalog>();

        return services;
    }
}

/// <summary>The running assembly version, used in the user agent and `version` output.</summary>
public static class RetraceVersion
{
    /// <summary>Informational version of the running build.</summary>
    public static string Current { get; } =
        typeof(RetraceVersion).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(RetraceVersion).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
