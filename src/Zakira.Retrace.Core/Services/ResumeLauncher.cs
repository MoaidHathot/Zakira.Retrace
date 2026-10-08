using System.Diagnostics;
using System.Text;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Core.Services;

/// <summary>
/// Turns a <see cref="ResumeCommand"/> into a running process that inherits the current terminal.
/// </summary>
/// <remarks>
/// <para>
/// The complication is Windows. Harnesses are usually installed through npm, which puts a
/// <c>opencode.cmd</c> or <c>copilot.cmd</c> shim on <c>PATH</c> rather than an executable, and
/// <c>CreateProcess</c> refuses to start a batch file directly. Asking the shell to run it
/// (<c>UseShellExecute = true</c>) is not an option either: a console program launched that way
/// gets a new console window instead of the one the user is sitting in.
/// </para>
/// <para>
/// So the launcher resolves the executable through <c>PATH</c> and <c>PATHEXT</c> itself, and when
/// the hit is a <c>.cmd</c> or <c>.bat</c> file it runs <c>cmd.exe /d /s /c "..."</c> around it. On
/// every other platform the executable is handed to the OS as-is.
/// </para>
/// </remarks>
public static class ResumeLauncher
{
    /// <summary>Builds the start info without starting anything, for callers that want to inspect or log it.</summary>
    public static ProcessStartInfo Build(ResumeCommand command)
    {
        var resolved = ResolveExecutable(command.Executable);
        var isBatch = OperatingSystem.IsWindows()
            && resolved is not null
            && (resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || resolved.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        ProcessStartInfo startInfo;

        if (isBatch)
        {
            // cmd.exe's quoting rules are the reason this exists. With /s, cmd strips exactly one
            // leading and one trailing quote from the whole command string and treats the rest as
            // the command line, which is the only reliable way to pass a quoted path plus quoted
            // arguments through it.
            var builder = new StringBuilder();
            builder.Append('"').Append(resolved).Append('"');
            foreach (var argument in command.Arguments)
            {
                builder.Append(' ').Append(QuoteForCmd(argument));
            }

            startInfo = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /s /c \"{builder}\"",
                UseShellExecute = false
            };
        }
        else
        {
            startInfo = new ProcessStartInfo(resolved ?? command.Executable) { UseShellExecute = false };
            foreach (var argument in command.Arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (command.WorkingDirectory is { Length: > 0 } directory && Directory.Exists(directory))
        {
            startInfo.WorkingDirectory = directory;
        }

        return startInfo;
    }

    /// <summary>Starts the harness, waits for it to exit, and returns its exit code.</summary>
    /// <exception cref="RetraceException">The executable could not be started.</exception>
    public static async Task<int> RunAsync(ResumeCommand command, CancellationToken cancellationToken)
    {
        var startInfo = Build(command);

        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new RetraceException($"Could not start '{command.Executable}'.");

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl-C reaches the child through the shared console; the harness handles its own
                // shutdown. Waiting a little longer avoids tearing down the terminal under it.
                if (!process.WaitForExit(2000))
                {
                    throw;
                }
            }

            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new RetraceException(
                $"Could not run '{command.Executable}': {ex.Message}. Is it installed and on PATH?", ex);
        }
    }

    /// <summary>
    /// Finds the file <c>PATH</c> would resolve <paramref name="executable"/> to, or
    /// <see langword="null"/> when it is not found and should be handed to the OS unchanged.
    /// </summary>
    public static string? ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        // A path, relative or absolute, is used as given.
        if (executable.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || executable.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            return File.Exists(executable) ? Path.GetFullPath(executable) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var directories = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // On Windows the shell tries each PATHEXT extension in order. A bare name is never run as
        // is: npm installs an extensionless POSIX shim next to every `.cmd` shim, and picking that
        // one up gives "not a valid application for this OS platform". A name that already carries
        // an extension is looked up verbatim. Elsewhere a file is a file.
        string[] extensions;
        if (!OperatingSystem.IsWindows())
        {
            extensions = [string.Empty];
        }
        else if (Path.HasExtension(executable))
        {
            extensions = [string.Empty];
        }
        else
        {
            extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory, executable + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Quotes one argument for a <c>cmd.exe /s /c</c> command string. Harness arguments are session
    /// ids, agent and model names, and paths; none of them legitimately contain the characters cmd
    /// treats as operators, so this only has to handle spaces and embedded quotes.
    /// </summary>
    private static string QuoteForCmd(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        if (argument.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%']) < 0)
        {
            return argument;
        }

        // Inside quotes cmd leaves everything alone except the quote itself; doubling it is what the
        // scripts npm generates expect to receive.
        return "\"" + argument.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
