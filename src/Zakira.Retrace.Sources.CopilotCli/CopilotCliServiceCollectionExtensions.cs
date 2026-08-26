using Microsoft.Extensions.DependencyInjection;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Sources.CopilotCli;

/// <summary>Registers the Copilot CLI source.</summary>
public static class CopilotCliServiceCollectionExtensions
{
    /// <summary>Adds GitHub Copilot CLI as a session source.</summary>
    public static IServiceCollection AddCopilotCliSource(this IServiceCollection services)
    {
        services.AddSingleton<ISessionSource, CopilotCliSessionSource>();
        return services;
    }
}
