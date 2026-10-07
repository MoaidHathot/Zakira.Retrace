using System.Diagnostics;

namespace Zakira.Retrace.Tui;

/// <summary>
/// Puts text on the system clipboard using whatever the platform offers.
/// </summary>
/// <remarks>
/// Each platform ships a command-line clipboard tool, and piping to it is both simpler and more
/// robust than P/Invoking a clipboard API from a process that owns no window. When nothing is
/// found the caller falls back to an OSC 52 escape, which most modern terminals honour.
/// </remarks>
public static class Clipboard
{
    /// <summary>Copies text. Returns the name of the tool used, or null when none worked.</summary>
    public static string? TryCopy(string text)
    {
        var candidates = OperatingSystem.IsWindows()
            ? [("clip.exe", Array.Empty<string>())]
            : OperatingSystem.IsMacOS()
                ? [("pbcopy", Array.Empty<string>())]
                : new (string, string[])[]
                {
                    ("wl-copy", []),
                    ("xclip", ["-selection", "clipboard"]),
                    ("xsel", ["--clipboard", "--input"]),
                    ("clip.exe", [])
                };

        foreach (var (tool, arguments) in candidates)
        {
            if (TryPipe(tool, arguments, text))
            {
                return tool;
            }
        }

        return null;
    }

    private static bool TryPipe(string tool, string[] arguments, string text)
    {
        try
        {
            var startInfo = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            // clip.exe copies exactly what it is given, so a trailing newline would end up on the
            // clipboard and break a pasted command. Write the bytes without one.
            process.StandardInput.Write(text);
            process.StandardInput.Close();

            return process.WaitForExit(1500) && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
