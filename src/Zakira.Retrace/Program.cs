using System.Text;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Cli;

// UTF-8 on stdout so session content round-trips on Windows, where the console defaults to a
// legacy code page that would mangle anything outside Latin-1.
Console.OutputEncoding = Encoding.UTF8;

// Ctrl-C becomes a real cancellation token rather than an abrupt kill, so an in-progress index
// build commits the sessions it already finished instead of leaving a half-written transaction.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

try
{
    return await CliApp.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}
catch (SessionNotFoundException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 4;
}
catch (IndexNotBuiltException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 3;
}
catch (EmbeddingModelMismatchException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 5;
}
catch (SourceUnavailableException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 2;
}
catch (RetraceException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    // Anything reaching here is a defect rather than a user error, so the stack trace is printed:
    // it is the only useful thing to put in a bug report.
    Console.Error.WriteLine("Unexpected error:");
    Console.Error.WriteLine(ex);
    return 70;
}
