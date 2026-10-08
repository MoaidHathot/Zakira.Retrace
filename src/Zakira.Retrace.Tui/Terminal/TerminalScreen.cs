using System.Runtime.InteropServices;
using System.Text;

namespace Zakira.Retrace.Tui.Terminal;

/// <summary>
/// Owns the terminal for the lifetime of the browser: alternate screen, hidden cursor, raw input,
/// and diffed frame output.
/// </summary>
/// <remarks>
/// <para>
/// Frames are written only where they changed. Every row from the previous frame is kept as a
/// string; a new frame emits a row only when its string differs, and the whole write is wrapped in
/// DEC 2026 synchronized-output markers so terminals that support them paint it atomically. That
/// is what keeps scrolling a long list flicker-free over a slow connection.
/// </para>
/// <para>
/// Output goes to stdout unless stdout is redirected, in which case it goes to stderr. That one
/// rule is what lets <c>retrace tui --pick dir</c> run inside <c>$(...)</c>: the picker draws on
/// the terminal through stderr while the chosen value is the only thing written to stdout.
/// </para>
/// </remarks>
public sealed class TerminalScreen : IDisposable
{
    private readonly Stream output;
    private readonly bool capture;
    private readonly bool mouse;
    private string[] lastRows = [];
    private bool disposed;

    /// <summary>Opens the terminal for full-screen drawing.</summary>
    /// <param name="mouse">Whether to capture mouse wheel and clicks.</param>
    /// <param name="depth">Colour depth to render with, or null to detect it from the environment.</param>
    /// <exception cref="InvalidOperationException">Neither stdout nor stderr is a terminal.</exception>
    public TerminalScreen(bool mouse, ColorDepth? depth = null)
    {
        if (Console.IsInputRedirected || (Console.IsOutputRedirected && Console.IsErrorRedirected))
        {
            throw new InvalidOperationException("The interactive browser needs a terminal attached to stdin and to stdout or stderr.");
        }

        this.mouse = mouse;
        Depth = depth ?? DetectColorDepth();
        WritesToStderr = Console.IsOutputRedirected;
        output = WritesToStderr ? Console.OpenStandardError() : Console.OpenStandardOutput();

        EnableVirtualTerminal(WritesToStderr);
        EnterRawModeUnix();

        // Alternate screen, hidden cursor, cleared, bracketed paste on. SGR mouse reporting is a
        // VT affair on Unix; on Windows the input reader asks the console API for mouse records.
        WriteNow("\u001b[?1049h\u001b[?25l\u001b[2J\u001b[H\u001b[?2004h");
        if (mouse && !OperatingSystem.IsWindows())
        {
            WriteNow("\u001b[?1000h\u001b[?1006h");
        }

        RefreshSize();

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    private TerminalScreen(int width, int height)
    {
        capture = true;
        output = Stream.Null;
        Width = width;
        Height = height;
    }

    /// <summary>Creates a headless screen of a fixed size for tests. Performs no terminal I/O.</summary>
    public static TerminalScreen CreateHeadless(int width, int height) => new(width, height);

    /// <summary>Colour depth frames are rendered with.</summary>
    public ColorDepth Depth { get; } = ColorDepth.TrueColor;

    /// <summary>
    /// Works out how many colours the terminal can show from the environment it advertises.
    /// </summary>
    /// <remarks>
    /// <c>NO_COLOR</c> wins outright. After that the checks go from most to least specific:
    /// <c>COLORTERM</c> is the explicit truecolor signal; Windows Terminal, VS Code, iTerm2,
    /// WezTerm, kitty, Ghostty, and Hyper all render 24-bit colour but not all set it; modern
    /// conhost does too. A <c>TERM</c> ending in <c>256color</c> gets the palette, and anything
    /// else the basic sixteen.
    /// </remarks>
    public static ColorDepth DetectColorDepth()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return ColorDepth.None;
        }

        var term = Environment.GetEnvironmentVariable("TERM") ?? string.Empty;
        if (term.Equals("dumb", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.None;
        }

        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM") ?? string.Empty;
        if (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase) || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.TrueColor;
        }

        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEZTERM_PANE"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR")))
        {
            return ColorDepth.TrueColor;
        }

        var program = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? string.Empty;
        if (program is "vscode" or "iTerm.app" or "WezTerm" or "Hyper" or "ghostty" or "Tabby" or "rio")
        {
            return ColorDepth.TrueColor;
        }

        if (program == "Apple_Terminal")
        {
            return ColorDepth.Ansi256;
        }

        if (OperatingSystem.IsWindows() && Environment.OSVersion.Version >= new Version(10, 0, 15063))
        {
            return ColorDepth.TrueColor;
        }

        if (term.Contains("256color", StringComparison.OrdinalIgnoreCase) || term.Contains("direct", StringComparison.OrdinalIgnoreCase))
        {
            return ColorDepth.Ansi256;
        }

        return ColorDepth.Ansi16;
    }

    /// <summary>Parses a configured depth name: <c>auto</c>, <c>truecolor</c>, <c>256</c>, <c>16</c>, or <c>none</c>.</summary>
    public static ColorDepth? ParseColorDepth(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "truecolor" or "24bit" or "24-bit" or "rgb" => ColorDepth.TrueColor,
        "256" or "ansi256" or "256color" => ColorDepth.Ansi256,
        "16" or "ansi" or "ansi16" or "basic" => ColorDepth.Ansi16,
        "none" or "mono" or "off" => ColorDepth.None,
        _ => null
    };

    /// <summary>Whether frames are drawn on stderr because stdout is being captured.</summary>
    public bool WritesToStderr { get; }

    /// <summary>Whether mouse capture was requested.</summary>
    public bool MouseEnabled => mouse;

    /// <summary>Columns, as of the last <see cref="RefreshSize"/>.</summary>
    public int Width { get; private set; }

    /// <summary>Rows, as of the last <see cref="RefreshSize"/>.</summary>
    public int Height { get; private set; }

    /// <summary>Re-reads the terminal size. Returns true when it changed.</summary>
    public bool RefreshSize()
    {
        if (capture)
        {
            return false;
        }

        int width;
        int height;
        try
        {
            width = Math.Max(20, Console.WindowWidth);
            height = Math.Max(5, Console.WindowHeight);
        }
        catch (IOException)
        {
            width = 80;
            height = 24;
        }

        var changed = width != Width || height != Height;
        Width = width;
        Height = height;

        if (changed)
        {
            lastRows = [];
        }

        return changed;
    }

    /// <summary>Headless only: pretend the terminal was resized.</summary>
    public void SetSizeForTest(int width, int height)
    {
        Width = width;
        Height = height;
        lastRows = [];
    }

    /// <summary>Writes a rendered frame, emitting only the rows that changed.</summary>
    public void Present(ScreenBuffer buffer)
    {
        var rows = buffer.RenderRows(Depth);

        if (capture)
        {
            lastRows = rows;
            return;
        }

        var all = lastRows.Length != rows.Length;
        var builder = new StringBuilder(64 * 1024);
        builder.Append("\u001b[?2026h");

        for (var index = 0; index < rows.Length; index++)
        {
            if (all || !string.Equals(rows[index], lastRows[index], StringComparison.Ordinal))
            {
                builder.Append(rows[index]);
            }
        }

        builder.Append("\u001b[0m\u001b[?2026l");
        WriteNow(builder.ToString());
        lastRows = rows;
    }

    /// <summary>Forces the next <see cref="Present"/> to redraw every row.</summary>
    public void Invalidate() => lastRows = [];

    /// <summary>Sets the terminal window title.</summary>
    public void SetTitle(string title)
    {
        if (capture)
        {
            return;
        }

        var clean = new string(title.Where(character => character >= 0x20 && character != 0x7f).ToArray());
        WriteNow("\u001b]2;" + clean + "\a");
    }

    /// <summary>Sends an OSC 52 clipboard write; terminals that allow it put the text on the system clipboard.</summary>
    public void WriteClipboardEscape(string text)
    {
        if (capture)
        {
            return;
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        WriteNow("\u001b]52;c;" + encoded + "\a");
    }

    /// <summary>Restores the terminal. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (capture || disposed)
        {
            return;
        }

        disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;

        if (mouse && !OperatingSystem.IsWindows())
        {
            WriteNow("\u001b[?1006l\u001b[?1000l");
        }

        WriteNow("\u001b[?2004l\u001b[0m\u001b[?25h\u001b[?1049l");
        RestoreRawModeUnix();
        RestoreInputMode();
    }

    private void OnProcessExit(object? sender, EventArgs e) => Dispose();

    private void WriteNow(string text)
    {
        if (capture)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }

    // ---- Windows console modes -------------------------------------------------------------

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;

    private static uint savedInputMode;
    private static nint inputHandle;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    private static void EnableVirtualTerminal(bool stderr)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = GetStdHandle(stderr ? StdErrorHandle : StdOutputHandle);
        if (GetConsoleMode(handle, out var outMode))
        {
            SetConsoleMode(handle, outMode | EnableVirtualTerminalProcessing);
        }

        inputHandle = GetStdHandle(StdInputHandle);
        if (GetConsoleMode(inputHandle, out savedInputMode))
        {
            // Line buffering and echo off so keys arrive one at a time and silently. Processed
            // input stays on so Ctrl+C remains a guaranteed way out, and VT input stays off: the
            // reader decodes key records, which Windows Terminal delivers far more reliably than
            // escape bytes.
            var inMode = savedInputMode;
            inMode &= ~(EnableLineInput | EnableEchoInput);
            inMode |= EnableProcessedInput;
            SetConsoleMode(inputHandle, inMode);
        }
    }

    private static void RestoreInputMode()
    {
        if (!OperatingSystem.IsWindows() || inputHandle == 0 || savedInputMode == 0)
        {
            return;
        }

        SetConsoleMode(inputHandle, savedInputMode);
    }

    // ---- Unix raw mode (termios) -----------------------------------------------------------

    private const int StdinFileno = 0;
    private const int TcsaNow = 0;
    private const uint Icanon = 0x0002;
    private const uint Echo = 0x0008;
    private const uint Iexten = 0x8000;
    private const uint Ixon = 0x0400;
    private const uint Icrnl = 0x0100;

    private static byte[]? savedTermios;
    private static bool rawModeSet;

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, [Out] byte[] termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, [In] byte[] termios);

    private static void EnterRawModeUnix()
    {
        if (OperatingSystem.IsWindows() || Console.IsInputRedirected)
        {
            return;
        }

        try
        {
            var termios = new byte[256];
            if (tcgetattr(StdinFileno, termios) != 0)
            {
                return;
            }

            savedTermios = (byte[])termios.Clone();

            // c_iflag and c_lflag are the first and fourth 4-byte fields on Linux and macOS alike.
            // ISIG is deliberately left on so Ctrl+C still raises SIGINT.
            ref var iflag = ref MemoryMarshal.AsRef<uint>(termios.AsSpan(0, 4));
            ref var lflag = ref MemoryMarshal.AsRef<uint>(termios.AsSpan(12, 4));
            iflag &= ~(Ixon | Icrnl);
            lflag &= ~(Icanon | Echo | Iexten);

            rawModeSet = tcsetattr(StdinFileno, TcsaNow, termios) == 0;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private static void RestoreRawModeUnix()
    {
        if (!rawModeSet || savedTermios is null)
        {
            return;
        }

        try
        {
            _ = tcsetattr(StdinFileno, TcsaNow, savedTermios);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        rawModeSet = false;
    }
}
