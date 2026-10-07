namespace Zakira.Retrace.Tui.Terminal;

/// <summary>What kind of input arrived.</summary>
public enum KeyKind
{
    /// <summary>A printable character, possibly with Ctrl or Alt held.</summary>
    Character,

    /// <summary>Enter / Return.</summary>
    Enter,

    /// <summary>Escape.</summary>
    Escape,

    /// <summary>Backspace.</summary>
    Backspace,

    /// <summary>Tab, or Shift+Tab when <see cref="KeyEvent.Shift"/> is set.</summary>
    Tab,

    /// <summary>Cursor up.</summary>
    Up,

    /// <summary>Cursor down.</summary>
    Down,

    /// <summary>Cursor left.</summary>
    Left,

    /// <summary>Cursor right.</summary>
    Right,

    /// <summary>Home.</summary>
    Home,

    /// <summary>End.</summary>
    End,

    /// <summary>Page up.</summary>
    PageUp,

    /// <summary>Page down.</summary>
    PageDown,

    /// <summary>Delete.</summary>
    Delete,

    /// <summary>Insert.</summary>
    Insert,

    /// <summary>A function key; the number is in <see cref="KeyEvent.Number"/>.</summary>
    Function,

    /// <summary>A bracketed paste; the text is in <see cref="KeyEvent.Text"/>.</summary>
    Paste,

    /// <summary>Mouse wheel up at the given cell.</summary>
    MouseScrollUp,

    /// <summary>Mouse wheel down at the given cell.</summary>
    MouseScrollDown,

    /// <summary>Left button pressed at the given cell.</summary>
    MouseClick,

    /// <summary>Terminal window gained or lost focus; the browser ignores these.</summary>
    Focus
}

/// <summary>One decoded input event, identical in shape on every platform.</summary>
/// <param name="Kind">What arrived.</param>
/// <param name="Character">The character for <see cref="KeyKind.Character"/>; Ctrl+letter is normalised to the lower-case letter.</param>
/// <param name="Ctrl">Control held.</param>
/// <param name="Alt">Alt / Meta held.</param>
/// <param name="Shift">Shift held, where the terminal reports it.</param>
/// <param name="Number">Function key number.</param>
/// <param name="Row">Zero-based cell row for mouse events.</param>
/// <param name="Column">Zero-based cell column for mouse events.</param>
/// <param name="Text">Pasted text, or the full grapheme when a non-BMP character arrived.</param>
public sealed record KeyEvent(
    KeyKind Kind,
    char Character = '\0',
    bool Ctrl = false,
    bool Alt = false,
    bool Shift = false,
    int Number = 0,
    int Row = 0,
    int Column = 0,
    string? Text = null)
{
    /// <summary>Whether this is the given plain (no modifier) character.</summary>
    public bool Is(char character) => Kind == KeyKind.Character && !Ctrl && !Alt && Character == character;

    /// <summary>Whether this is Ctrl plus the given letter.</summary>
    public bool IsCtrl(char character) => Kind == KeyKind.Character && Ctrl && !Alt && Character == character;

    /// <summary>Whether this is a mouse event.</summary>
    public bool IsMouse => Kind is KeyKind.MouseScrollUp or KeyKind.MouseScrollDown or KeyKind.MouseClick;
}
