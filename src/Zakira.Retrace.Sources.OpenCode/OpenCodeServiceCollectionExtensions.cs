using Microsoft.Extensions.DependencyInjection;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Sources.OpenCode;

/// <summary>Registers the OpenCode source.</summary>
public static class OpenCodeServiceCollectionExtensions
{
    /// <summary>Adds OpenCode as a session source.</summary>
    public static IServiceCollection AddOpenCodeSource(this IServiceCollection services)
    {
        services.AddSingleton<ISessionSource, OpenCodeSessionSource>();
        return services;
    }
}
