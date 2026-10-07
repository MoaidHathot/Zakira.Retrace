using System.Runtime.InteropServices;
using System.Text;

namespace Zakira.Retrace.Tui.Terminal;

/// <summary>
/// Decodes keyboard and mouse input into <see cref="KeyEvent"/>s.
/// </summary>
/// <remarks>
/// Two backends. On Windows, <c>ReadConsoleInputW</c> delivers key and mouse records directly,
/// which is the dependable way to see individual key presses and wheel ticks under Windows
/// Terminal and ConPTY. Everywhere else the terminal is in raw mode and this parses VT sequences
/// from stdin: CSI cursor keys with xterm modifiers, tilde keys, SGR mouse reports, bracketed
/// paste, and the odd terminal reply, which is swallowed rather than typed into the search box.
/// </remarks>
public static class TerminalInput
{
    /// <summary>Reads events until <paramref name="isRunning"/> returns false. Blocks; run on its own thread.</summary>
    public static IEnumerable<KeyEvent> Read(Func<bool> isRunning, bool mouse) =>
        OperatingSystem.IsWindows() ? ReadWindows(isRunning, mouse) : ReadPortable(isRunning);

    /// <summary>Parses a complete byte sequence the way the portable reader would. A test seam.</summary>
    public static List<KeyEvent> ParseBytes(byte[] bytes)
    {
        var pending = new List<byte>(bytes);
        var state = new PortableState();
        var events = new List<KeyEvent>();

        while (pending.Count > 0)
        {
            var consumed = TryParse(pending, state, out var keyEvent, final: true);
            if (consumed <= 0)
            {
                break;
            }

            pending.RemoveRange(0, consumed);
            if (keyEvent is not null)
            {
                events.Add(keyEvent);
            }
        }

        return events;
    }

    // ---- portable ---------------------------------------------------------------------------

    private sealed class PortableState
    {
        public bool InPaste { get; set; }

        public StringBuilder Paste { get; } = new();
    }

    private static readonly byte[] PasteEnd = "\u001b[201~"u8.ToArray();

    private static IEnumerable<KeyEvent> ReadPortable(Func<bool> isRunning)
    {
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[4096];
        var pending = new List<byte>(64);
        var state = new PortableState();

        while (isRunning())
        {
            int read;
            try
            {
                read = stdin.Read(buffer, 0, buffer.Length);
            }
            catch (IOException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }

            if (read <= 0)
            {
                if (!isRunning())
                {
                    yield break;
                }

                continue;
            }

            for (var index = 0; index < read; index++)
            {
                pending.Add(buffer[index]);
            }

            int consumed;
            while ((consumed = TryParse(pending, state, out var keyEvent, final: false)) > 0)
            {
                pending.RemoveRange(0, consumed);
                if (keyEvent is not null)
                {
                    yield return keyEvent;
                }
            }

            // A lone ESC that ends a read is the Escape key; a real sequence arrives in one read.
            if (pending.Count == 1 && pending[0] == 0x1b)
            {
                pending.Clear();
                yield return new KeyEvent(KeyKind.Escape);
            }
        }
    }

    private static int TryParse(List<byte> bytes, PortableState state, out KeyEvent? keyEvent, bool final)
    {
        keyEvent = null;
        if (bytes.Count == 0)
        {
            return 0;
        }

        if (state.InPaste)
        {
            var end = IndexOf(bytes, PasteEnd);
            if (end < 0)
            {
                var keep = Math.Min(bytes.Count, PasteEnd.Length - 1);
                var take = bytes.Count - keep;
                if (take <= 0)
                {
                    return 0;
                }

                state.Paste.Append(Encoding.UTF8.GetString(bytes.ToArray(), 0, take));
                return take;
            }

            state.Paste.Append(Encoding.UTF8.GetString(bytes.ToArray(), 0, end));
            state.InPaste = false;
            keyEvent = new KeyEvent(KeyKind.Paste, Text: state.Paste.ToString());
            state.Paste.Clear();
            return end + PasteEnd.Length;
        }

        var first = bytes[0];
        if (first != 0x1b)
        {
            return ParsePlainByte(bytes, out keyEvent);
        }

        if (bytes.Count == 1)
        {
            if (final)
            {
                keyEvent = new KeyEvent(KeyKind.Escape);
                return 1;
            }

            return 0;
        }

        var second = bytes[1];

        if (second is (byte)'[' or (byte)'O')
        {
            var end = -1;
            for (var index = 2; index < bytes.Count; index++)
            {
                if (bytes[index] >= 0x40 && bytes[index] <= 0x7e)
                {
                    end = index;
                    break;
                }
            }

            if (end < 0)
            {
                return bytes.Count > 64 ? bytes.Count : 0;
            }

            var sequence = Encoding.ASCII.GetString(bytes.ToArray(), 1, end);
            if (sequence == "[200~")
            {
                state.InPaste = true;
                state.Paste.Clear();
                return end + 1;
            }

            keyEvent = ParseCsi(sequence);
            return end + 1;
        }

        if (second is (byte)']' or (byte)'P' or (byte)'_' or (byte)'^')
        {
            // OSC / DCS / APC / PM replies end with ST or, for OSC, BEL. Nothing here is a key.
            for (var index = 2; index < bytes.Count; index++)
            {
                var isSt = bytes[index] == 0x5c && bytes[index - 1] == 0x1b;
                var isBel = second == (byte)']' && bytes[index] == 0x07;
                if (isSt || isBel)
                {
                    return index + 1;
                }
            }

            return bytes.Count > 4096 ? bytes.Count : 0;
        }

        if (second >= 0x20 && second < 0x7f)
        {
            keyEvent = new KeyEvent(KeyKind.Character, (char)second, Alt: true);
            return 2;
        }

        if (second is >= 1 and <= 26)
        {
            keyEvent = new KeyEvent(KeyKind.Character, (char)('a' + (second - 1)), Ctrl: true, Alt: true);
            return 2;
        }

        keyEvent = new KeyEvent(KeyKind.Escape);
        return 1;
    }

    private static int ParsePlainByte(List<byte> bytes, out KeyEvent? keyEvent)
    {
        keyEvent = null;
        var value = bytes[0];

        switch (value)
        {
            case 0x0d or 0x0a:
                keyEvent = new KeyEvent(KeyKind.Enter);
                return 1;
            case 0x7f or 0x08:
                keyEvent = new KeyEvent(KeyKind.Backspace);
                return 1;
            case 0x09:
                keyEvent = new KeyEvent(KeyKind.Tab);
                return 1;
            case 0x00:
                keyEvent = new KeyEvent(KeyKind.Character, ' ', Ctrl: true);
                return 1;
        }

        if (value is >= 1 and <= 26)
        {
            keyEvent = new KeyEvent(KeyKind.Character, (char)('a' + (value - 1)), Ctrl: true);
            return 1;
        }

        if (value < 0x80)
        {
            keyEvent = new KeyEvent(KeyKind.Character, (char)value);
            return 1;
        }

        var length = value switch
        {
            >= 0xf0 => 4,
            >= 0xe0 => 3,
            _ => 2
        };

        if (bytes.Count < length)
        {
            return 0;
        }

        var text = Encoding.UTF8.GetString(bytes.ToArray(), 0, length);
        keyEvent = text.Length > 0 ? new KeyEvent(KeyKind.Character, text[0], Text: text.Length > 1 ? text : null) : null;
        return length;
    }

    /// <summary>Parses a CSI or SS3 body (everything after the ESC), such as <c>[A</c>, <c>[1;5C</c>, or <c>[&lt;64;12;7M</c>.</summary>
    internal static KeyEvent? ParseCsi(string sequence)
    {
        if (sequence.Length > 2 && sequence[0] == '[' && sequence[1] == '<')
        {
            var isRelease = sequence[^1] == 'm';
            var parts = sequence[2..^1].Split(';');
            if (parts.Length == 3
                && int.TryParse(parts[0], out var code)
                && int.TryParse(parts[1], out var column)
                && int.TryParse(parts[2], out var row))
            {
                return MouseFromSgr(code, column - 1, row - 1, isRelease);
            }

            return null;
        }

        if (sequence is "[I" or "[O")
        {
            return new KeyEvent(KeyKind.Focus);
        }

        var last = sequence[^1];
        if (last is 't' or 'c' or 'R' or 'y' || (sequence.Length > 1 && sequence[1] == '?'))
        {
            // A terminal reply, not a key.
            return null;
        }

        var body = sequence.Length > 0 && sequence[0] is '[' or 'O' ? sequence[1..] : sequence;
        var key = body.Length > 0 ? body[^1] : '\0';
        var (shift, alt, ctrl) = ParseModifier(body);

        return key switch
        {
            'A' => new KeyEvent(KeyKind.Up, Ctrl: ctrl, Alt: alt, Shift: shift),
            'B' => new KeyEvent(KeyKind.Down, Ctrl: ctrl, Alt: alt, Shift: shift),
            'C' => new KeyEvent(KeyKind.Right, Ctrl: ctrl, Alt: alt, Shift: shift),
            'D' => new KeyEvent(KeyKind.Left, Ctrl: ctrl, Alt: alt, Shift: shift),
            'H' => new KeyEvent(KeyKind.Home, Ctrl: ctrl, Alt: alt, Shift: shift),
            'F' => new KeyEvent(KeyKind.End, Ctrl: ctrl, Alt: alt, Shift: shift),
            'Z' => new KeyEvent(KeyKind.Tab, Shift: true),
            'P' => new KeyEvent(KeyKind.Function, Number: 1, Ctrl: ctrl, Alt: alt, Shift: shift),
            'Q' => new KeyEvent(KeyKind.Function, Number: 2, Ctrl: ctrl, Alt: alt, Shift: shift),
            'S' => new KeyEvent(KeyKind.Function, Number: 4, Ctrl: ctrl, Alt: alt, Shift: shift),
            '~' => ParseTildeKey(body, shift, alt, ctrl),
            _ => null
        };
    }

    private static (bool Shift, bool Alt, bool Ctrl) ParseModifier(string body)
    {
        var semicolon = body.IndexOf(';', StringComparison.Ordinal);
        if (semicolon < 0)
        {
            return (false, false, false);
        }

        var rest = body[(semicolon + 1)..].TrimEnd('~', 'A', 'B', 'C', 'D', 'H', 'F', 'P', 'Q', 'S', 'R');
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            rest = rest[..colon];
        }

        if (!int.TryParse(rest, out var modifier) || modifier < 2)
        {
            return (false, false, false);
        }

        var bits = modifier - 1;
        return ((bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
    }

    private static KeyEvent? ParseTildeKey(string body, bool shift, bool alt, bool ctrl)
    {
        var semicolon = body.IndexOf(';', StringComparison.Ordinal);
        var number = semicolon >= 0 ? body[..semicolon] : body.TrimEnd('~');
        if (!int.TryParse(number, out var code))
        {
            return null;
        }

        return code switch
        {
            1 or 7 => new KeyEvent(KeyKind.Home, Ctrl: ctrl, Alt: alt, Shift: shift),
            2 => new KeyEvent(KeyKind.Insert, Ctrl: ctrl, Alt: alt, Shift: shift),
            3 => new KeyEvent(KeyKind.Delete, Ctrl: ctrl, Alt: alt, Shift: shift),
            4 or 8 => new KeyEvent(KeyKind.End, Ctrl: ctrl, Alt: alt, Shift: shift),
            5 => new KeyEvent(KeyKind.PageUp, Ctrl: ctrl, Alt: alt, Shift: shift),
            6 => new KeyEvent(KeyKind.PageDown, Ctrl: ctrl, Alt: alt, Shift: shift),
            >= 11 and <= 15 => new KeyEvent(KeyKind.Function, Number: code - 10, Ctrl: ctrl, Alt: alt, Shift: shift),
            >= 17 and <= 21 => new KeyEvent(KeyKind.Function, Number: code - 11, Ctrl: ctrl, Alt: alt, Shift: shift),
            23 or 24 => new KeyEvent(KeyKind.Function, Number: code - 12, Ctrl: ctrl, Alt: alt, Shift: shift),
            _ => null
        };
    }

    private static KeyEvent? MouseFromSgr(int code, int column, int row, bool isRelease)
    {
        var ctrl = (code & 0x10) != 0;
        var alt = (code & 0x08) != 0;
        var shift = (code & 0x04) != 0;

        if ((code & 0x40) != 0)
        {
            var up = (code & 0x03) == 0;
            return new KeyEvent(up ? KeyKind.MouseScrollUp : KeyKind.MouseScrollDown, Ctrl: ctrl, Alt: alt, Shift: shift, Row: row, Column: column);
        }

        var isMotion = (code & 0x20) != 0;
        if (isMotion || isRelease)
        {
            return null;
        }

        return (code & 0x03) == 0
            ? new KeyEvent(KeyKind.MouseClick, Ctrl: ctrl, Alt: alt, Shift: shift, Row: row, Column: column)
            : null;
    }

    private static int IndexOf(List<byte> haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Count; start++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return start;
            }
        }

        return -1;
    }

    // ---- Windows ----------------------------------------------------------------------------

    private static IEnumerable<KeyEvent> ReadWindows(Func<bool> isRunning, bool mouse)
    {
        var handle = GetStdHandle(StdInputHandle);

        uint savedMode = 0;
        var modeChanged = false;
        if (GetConsoleMode(handle, out savedMode))
        {
            // Mouse and window events on, quick-edit off so clicks reach us, processed input on so
            // Ctrl+C still works, VT input off so keys arrive as records.
            var mode = EnableProcessedInput | EnableWindowInput | EnableExtendedFlags;
            mode |= mouse ? EnableMouseInput : EnableQuickEditMode;
            modeChanged = SetConsoleMode(handle, mode);
        }

        var records = new InputRecord[64];
        var inEscape = false;
        var inPaste = false;
        var escape = new StringBuilder();
        var paste = new StringBuilder();
        uint previousButtons = 0;

        try
        {
            while (isRunning())
            {
                if (WaitForSingleObject(handle, 100) != 0)
                {
                    continue;
                }

                if (!ReadConsoleInputW(handle, records, (uint)records.Length, out var read) || read == 0)
                {
                    continue;
                }

                for (uint index = 0; index < read; index++)
                {
                    var record = records[index];

                    if (record.EventType == KeyEventType)
                    {
                        if (record.KeyEvent.KeyDown == 0)
                        {
                            continue;
                        }

                        var character = (char)record.KeyEvent.UnicodeChar;

                        if (inEscape)
                        {
                            // ConPTY delivers terminal replies and bracketed-paste markers as key
                            // records. Collect the sequence and never let it type anything.
                            if (character != '\0')
                            {
                                escape.Append(character);
                                var kind = escape.Length > 1 ? escape[1] : '\0';
                                var isOsc = kind is ']' or 'P' or '_' or '^';
                                var done = isOsc
                                    ? character == '\a' || (character == '\\' && escape.Length > 2 && escape[^2] == '\u001b')
                                    : character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '~';

                                if (done)
                                {
                                    var sequence = escape.ToString(1, escape.Length - 1);
                                    inEscape = false;
                                    escape.Clear();

                                    if (sequence == "[200~")
                                    {
                                        inPaste = true;
                                        paste.Clear();
                                        continue;
                                    }

                                    if (sequence == "[201~")
                                    {
                                        inPaste = false;
                                        yield return new KeyEvent(KeyKind.Paste, Text: paste.ToString());
                                        paste.Clear();
                                        continue;
                                    }

                                    if (!isOsc)
                                    {
                                        var parsed = ParseCsi(sequence);
                                        if (parsed is not null && !inPaste)
                                        {
                                            yield return parsed;
                                        }
                                    }
                                }
                                else if (escape.Length > 4096)
                                {
                                    inEscape = false;
                                    escape.Clear();
                                }
                            }

                            continue;
                        }

                        if (character == '\u001b')
                        {
                            var nextIsSequence = index + 1 < read
                                && records[index + 1].EventType == KeyEventType
                                && records[index + 1].KeyEvent.KeyDown != 0
                                && (char)records[index + 1].KeyEvent.UnicodeChar is '[' or 'O' or ']' or 'P' or '_';

                            if (nextIsSequence)
                            {
                                inEscape = true;
                                escape.Clear();
                                escape.Append(character);
                                continue;
                            }
                        }

                        if (inPaste)
                        {
                            if (character != '\0')
                            {
                                paste.Append(character == '\r' ? '\n' : character);
                            }

                            continue;
                        }

                        var keyEvent = FromKeyRecord(record.KeyEvent);
                        if (keyEvent is not null)
                        {
                            yield return keyEvent;
                        }
                    }
                    else if (record.EventType == MouseEventType && mouse)
                    {
                        var keyEvent = FromMouseRecord(record.MouseEvent, ref previousButtons);
                        if (keyEvent is not null)
                        {
                            yield return keyEvent;
                        }
                    }
                    else if (record.EventType == FocusEventType)
                    {
                        yield return new KeyEvent(KeyKind.Focus);
                    }
                }
            }
        }
        finally
        {
            if (modeChanged)
            {
                SetConsoleMode(handle, savedMode);
            }
        }
    }

    private static KeyEvent? FromMouseRecord(in MouseEventRecord record, ref uint previousButtons)
    {
        var ctrl = (record.ControlKeyState & (LeftCtrlPressed | RightCtrlPressed)) != 0;
        var alt = (record.ControlKeyState & (LeftAltPressed | RightAltPressed)) != 0;
        var shift = (record.ControlKeyState & ShiftPressed) != 0;
        int row = record.MousePosition.Y;
        int column = record.MousePosition.X;

        if (record.EventFlags == MouseWheeled)
        {
            var delta = (short)((record.ButtonState >> 16) & 0xffff);
            return new KeyEvent(delta > 0 ? KeyKind.MouseScrollUp : KeyKind.MouseScrollDown, Ctrl: ctrl, Alt: alt, Shift: shift, Row: row, Column: column);
        }

        var leftNow = (record.ButtonState & FromLeftFirstButton) != 0;
        var leftBefore = (previousButtons & FromLeftFirstButton) != 0;
        previousButtons = record.ButtonState;

        if (record.EventFlags is 0 or DoubleClick && leftNow && !leftBefore)
        {
            return new KeyEvent(KeyKind.MouseClick, Ctrl: ctrl, Alt: alt, Shift: shift, Row: row, Column: column);
        }

        return null;
    }

    private static KeyEvent? FromKeyRecord(in KeyEventRecord record)
    {
        var ctrl = (record.ControlKeyState & (LeftCtrlPressed | RightCtrlPressed)) != 0;
        var alt = (record.ControlKeyState & (LeftAltPressed | RightAltPressed)) != 0;
        var shift = (record.ControlKeyState & ShiftPressed) != 0;
        var character = (char)record.UnicodeChar;

        // AltGr shows up as Ctrl+RightAlt and produces ordinary characters such as '@' or '{'.
        var altGr = (record.ControlKeyState & RightAltPressed) != 0 && (record.ControlKeyState & LeftCtrlPressed) != 0;
        if (altGr && character != '\0' && !char.IsControl(character))
        {
            ctrl = false;
            alt = false;
        }

        switch (record.VirtualKeyCode)
        {
            case VkUp:
                return new KeyEvent(KeyKind.Up, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkDown:
                return new KeyEvent(KeyKind.Down, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkLeft:
                return new KeyEvent(KeyKind.Left, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkRight:
                return new KeyEvent(KeyKind.Right, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkHome:
                return new KeyEvent(KeyKind.Home, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkEnd:
                return new KeyEvent(KeyKind.End, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkPrior:
                return new KeyEvent(KeyKind.PageUp, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkNext:
                return new KeyEvent(KeyKind.PageDown, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkInsert:
                return new KeyEvent(KeyKind.Insert, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkDelete:
                return new KeyEvent(KeyKind.Delete, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkReturn:
                return new KeyEvent(KeyKind.Enter, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkEscape:
                return new KeyEvent(KeyKind.Escape);
            case VkBack:
                return new KeyEvent(KeyKind.Backspace, Ctrl: ctrl, Alt: alt);
            case VkTab:
                return new KeyEvent(KeyKind.Tab, Ctrl: ctrl, Alt: alt, Shift: shift);
            case >= VkF1 and <= VkF12:
                return new KeyEvent(KeyKind.Function, Number: record.VirtualKeyCode - VkF1 + 1, Ctrl: ctrl, Alt: alt, Shift: shift);
            case VkShift or VkControl or VkMenu or VkCapital or VkLwin or VkRwin or VkNumlock or VkScroll:
                return null;
        }

        if (ctrl && character is >= (char)1 and <= (char)26)
        {
            return new KeyEvent(KeyKind.Character, (char)('a' + (character - 1)), Ctrl: true, Alt: alt);
        }

        if (ctrl && character == '\0' && record.VirtualKeyCode is >= 0x30 and <= 0x39)
        {
            return new KeyEvent(KeyKind.Character, (char)('0' + (record.VirtualKeyCode - 0x30)), Ctrl: true, Alt: alt);
        }

        if (alt && character == '\0' && record.VirtualKeyCode is >= 0x41 and <= 0x5a)
        {
            return new KeyEvent(KeyKind.Character, (char)('a' + (record.VirtualKeyCode - 0x41)), Alt: true, Ctrl: ctrl);
        }

        if (character != '\0' && !char.IsControl(character))
        {
            return new KeyEvent(KeyKind.Character, character, ctrl, alt, shift);
        }

        return null;
    }

    // ---- Win32 interop ----------------------------------------------------------------------

    private const int StdInputHandle = -10;
    private const ushort KeyEventType = 0x0001;
    private const ushort MouseEventType = 0x0002;
    private const ushort FocusEventType = 0x0010;
    private const uint MouseWheeled = 0x0004;
    private const uint DoubleClick = 0x0002;
    private const uint FromLeftFirstButton = 0x0001;
    private const int LeftCtrlPressed = 0x0008;
    private const int RightCtrlPressed = 0x0004;
    private const int LeftAltPressed = 0x0002;
    private const int RightAltPressed = 0x0001;
    private const int ShiftPressed = 0x0010;

    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableWindowInput = 0x0008;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;

    private const ushort VkBack = 0x08;
    private const ushort VkTab = 0x09;
    private const ushort VkReturn = 0x0d;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkCapital = 0x14;
    private const ushort VkEscape = 0x1b;
    private const ushort VkPrior = 0x21;
    private const ushort VkNext = 0x22;
    private const ushort VkEnd = 0x23;
    private const ushort VkHome = 0x24;
    private const ushort VkLeft = 0x25;
    private const ushort VkUp = 0x26;
    private const ushort VkRight = 0x27;
    private const ushort VkDown = 0x28;
    private const ushort VkInsert = 0x2d;
    private const ushort VkDelete = 0x2e;
    private const ushort VkLwin = 0x5b;
    private const ushort VkRwin = 0x5c;
    private const ushort VkF1 = 0x70;
    private const ushort VkF12 = 0x7b;
    private const ushort VkNumlock = 0x90;
    private const ushort VkScroll = 0x91;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConsoleInputW(nint hConsoleInput, [Out] InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [StructLayout(LayoutKind.Explicit)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;

        [FieldOffset(4)]
        public MouseEventRecord MouseEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public ushort UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEventRecord
    {
        public Coord MousePosition;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }
}
