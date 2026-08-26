using System.Diagnostics;
using System.Text;

namespace Zakira.Retrace.E2ETests;

/// <summary>
/// Runs the built <c>retrace</c> binary as a subprocess.
/// </summary>
/// <remarks>
/// Every test gets its own config and data directory through environment overrides, so the suite
/// never reads or writes the developer's real configuration or index.
/// </remarks>
internal sealed class RetraceRunner : IDisposable
{
    private readonly TempDirectory workspace = new();

    /// <summary>Config file this runner uses.</summary>
    public string ConfigPath => Path.Combine(workspace.Path, "config", "retrace.json");

    /// <summary>Data directory this runner uses.</summary>
    public string DataDirectory => Path.Combine(workspace.Path, "data");

    /// <summary>Scratch root, for fixtures.</summary>
    public string Root => workspace.Path;

    /// <summary>Runs a command and captures its output.</summary>
    public async Task<CommandResult> RunAsync(params string[] args)
    {
        var startInfo = BuildStartInfo(args);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the retrace process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(timeout.Token);

        return new CommandResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Sends newline-delimited JSON-RPC to <c>mcp serve</c> and returns whatever it wrote to stdout.
    /// </summary>
    /// <remarks>
    /// Reading runs concurrently with writing. Draining stdout only after the process ends
    /// deadlocks as soon as the server fills the pipe buffer, which is exactly what a real MCP
    /// client would hit.
    /// </remarks>
    /// <param name="requests">Messages to send, in order.</param>
    /// <param name="expectedResponses">
    /// How many id-bearing responses to wait for before shutting down. Waiting on the responses
    /// rather than on a fixed delay keeps the suite fast without making it flaky on a slow machine.
    /// </param>
    public async Task<McpResult> RunMcpAsync(IReadOnlyList<string> requests, int expectedResponses = 1)
    {
        var startInfo = BuildStartInfo(["mcp", "serve"]);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the retrace MCP server.");

        var stdout = new List<string>();
        var stderr = new StringBuilder();
        using var responsesReceived = new SemaphoreSlim(0);

        var stdoutReader = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                lock (stdout)
                {
                    stdout.Add(line);
                }

                if (line.Contains("\"id\"", StringComparison.Ordinal))
                {
                    responsesReceived.Release();
                }
            }
        });

        var stderrReader = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                lock (stderr)
                {
                    stderr.AppendLine(line);
                }
            }
        });

        foreach (var request in requests)
        {
            await process.StandardInput.WriteAsync(request + "\n");
        }

        await process.StandardInput.FlushAsync();

        // Generous per-response ceiling: the first call pays for JIT, DI construction, and opening
        // whatever session stores exist on the machine.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        for (var index = 0; index < expectedResponses; index++)
        {
            try
            {
                await responsesReceived.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        process.StandardInput.Close();

        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        await Task.WhenAll(stdoutReader, stderrReader).WaitAsync(TimeSpan.FromSeconds(10));

        lock (stdout)
        {
            lock (stderr)
            {
                return new McpResult([.. stdout], stderr.ToString());
            }
        }
    }

    private ProcessStartInfo BuildStartInfo(IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workspace.Path
        };

        startInfo.ArgumentList.Add(RetraceTool.DllPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.Environment["RETRACE_CONFIG_PATH"] = ConfigPath;
        startInfo.Environment["RETRACE_DATA_DIRECTORY"] = DataDirectory;

        // Deterministic output regardless of the developer's terminal.
        startInfo.Environment["NO_COLOR"] = "1";

        return startInfo;
    }

    /// <inheritdoc />
    public void Dispose() => workspace.Dispose();
}

/// <summary>The outcome of one CLI invocation.</summary>
internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Combined output, for assertions that do not care which stream a line came from.</summary>
    public string All => StandardOutput + StandardError;
}

/// <summary>The captured streams of one MCP session.</summary>
internal sealed record McpResult(IReadOnlyList<string> Lines, string StandardError)
{
    /// <summary>Raw stdout, reassembled.</summary>
    public string StandardOutput => string.Join('\n', Lines);
}

/// <summary>A scratch directory removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Zakira.Retrace.E2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
