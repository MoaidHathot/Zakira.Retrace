using Microsoft.Extensions.DependencyInjection;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Sources.CopilotVsCode;

/// <summary>Registers the VS Code Copilot Chat source.</summary>
public static class CopilotVsCodeServiceCollectionExtensions
{
    /// <summary>Adds GitHub Copilot in VS Code as a session source.</summary>
    public static IServiceCollection AddCopilotVsCodeSource(this IServiceCollection services)
    {
        services.AddSingleton<ISessionSource, CopilotVsCodeSessionSource>();
        return services;
    }
}
