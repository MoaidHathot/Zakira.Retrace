using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Cli;
using Zakira.Retrace.Core.DependencyInjection;
using Zakira.Retrace.Sources.CopilotCli;
using Zakira.Retrace.Sources.CopilotVsCode;
using Zakira.Retrace.Sources.OpenCode;

namespace Zakira.Retrace.Mcp;

/// <summary>
/// Hosts the Retrace MCP server.
/// </summary>
/// <remarks>
/// stdio is the default because that is what every MCP client configuration expects. The critical
/// constraint of that transport is that stdout carries JSON-RPC and nothing else, so logging is
/// re-pointed at stderr before the server starts. A single stray <c>Console.WriteLine</c> reaching
/// stdout would desynchronise the protocol stream and hang the client.
/// </remarks>
internal static class McpHost
{
    /// <summary>The stdio transport.</summary>
    internal const string TransportStdio = "stdio";

    /// <summary>The streamable HTTP transport.</summary>
    internal const string TransportHttp = "http";

    /// <summary>Runs the server.</summary>
    internal static async Task<int> RunAsync(
        RetraceHost host,
        string transport,
        int port,
        bool verbose,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (!transport.Equals(TransportStdio, StringComparison.OrdinalIgnoreCase))
        {
            if (transport.Equals(TransportHttp, StringComparison.OrdinalIgnoreCase))
            {
                throw new RetraceException(
                    "The http transport is not available in this build. Run `retrace mcp serve` for stdio, "
                    + "which is what MCP clients use for a local tool.");
            }

            throw new RetraceException($"Unknown MCP transport '{transport}'. Use stdio.");
        }

        // Configuration and paths resolve exactly as they do for the CLI, so an MCP client and a
        // terminal see the same sources, the same index, and the same tags.
        var config = await host.GetConfigAsync(cancellationToken).ConfigureAwait(false);

        var builder = Host.CreateApplicationBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        // --verbose has to reach the MCP server too. Without it there is no way to see why a
        // tool failed: stdout is the protocol stream, so stderr logging is the only channel.
        builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);

        builder.Services.AddRetraceCore(config, host.Paths);
        builder.Services.AddOpenCodeSource();
        builder.Services.AddCopilotCliSource();
        builder.Services.AddCopilotVsCodeSource();

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new ModelContextProtocol.Protocol.Implementation
                {
                    Name = "Zakira.Retrace",
                    Version = RetraceVersion.Current
                };
            })
            .WithStdioServerTransport()
            .WithTools<RetraceTools>()
            .WithResources<RetraceResources>();

        stderr.WriteLine($"[Zakira.Retrace] MCP server {RetraceVersion.Current} starting on stdio.");
        stderr.WriteLine($"[Zakira.Retrace] Config: {host.Store.ConfigPath}");

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
