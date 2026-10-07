using System.Text;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui.UnitTests;

/// <summary>The VT grammar the portable input reader understands.</summary>
public sealed class TerminalInputTests
{
    private static List<KeyEvent> Parse(string sequence) => TerminalInput.ParseBytes(Encoding.UTF8.GetBytes(sequence));

    [Fact]
    public void Plain_characters_and_controls_decode()
    {
        Parse("a").Should().Equal(new KeyEvent(KeyKind.Character, 'a'));
        Parse("\r").Should().Equal(new KeyEvent(KeyKind.Enter));
        Parse("\t").Should().Equal(new KeyEvent(KeyKind.Tab));
        Parse("\x7f").Should().Equal(new KeyEvent(KeyKind.Backspace));
        Parse("\x12").Should().Equal(new KeyEvent(KeyKind.Character, 'r', Ctrl: true));
    }

    [Fact]
    public void Cursor_keys_with_xterm_modifiers_decode()
    {
        Parse("\u001b[A").Should().Equal(new KeyEvent(KeyKind.Up));
        Parse("\u001b[1;5C").Should().Equal(new KeyEvent(KeyKind.Right, Ctrl: true));
        Parse("\u001b[1;2B").Should().Equal(new KeyEvent(KeyKind.Down, Shift: true));
        Parse("\u001bOH").Should().Equal(new KeyEvent(KeyKind.Home));
        Parse("\u001b[Z").Should().Equal(new KeyEvent(KeyKind.Tab, Shift: true));
    }

    [Fact]
    public void Tilde_keys_decode()
    {
        Parse("\u001b[5~").Should().Equal(new KeyEvent(KeyKind.PageUp));
        Parse("\u001b[6~").Should().Equal(new KeyEvent(KeyKind.PageDown));
        Parse("\u001b[3~").Should().Equal(new KeyEvent(KeyKind.Delete));
        Parse("\u001b[15~").Should().Equal(new KeyEvent(KeyKind.Function, Number: 5));
    }

    [Fact]
    public void Sgr_mouse_reports_decode_to_zero_based_cells()
    {
        Parse("\u001b[<64;12;7M").Should().Equal(new KeyEvent(KeyKind.MouseScrollUp, Row: 6, Column: 11));
        Parse("\u001b[<65;1;1M").Should().Equal(new KeyEvent(KeyKind.MouseScrollDown, Row: 0, Column: 0));
        Parse("\u001b[<0;5;9M").Should().Equal(new KeyEvent(KeyKind.MouseClick, Row: 8, Column: 4));

        // A release is not a click; counting it would double every click.
        Parse("\u001b[<0;5;9m").Should().BeEmpty();
    }

    [Fact]
    public void Bracketed_paste_arrives_as_one_event()
    {
        var events = Parse("\u001b[200~hello q world\u001b[201~");

        events.Should().Equal(new KeyEvent(KeyKind.Paste, Text: "hello q world"));
    }

    [Fact]
    public void Terminal_replies_are_swallowed_rather_than_typed()
    {
        Parse("\u001b[?62;c").Should().BeEmpty();
        Parse("\u001b[4;500;900t").Should().BeEmpty();
        Parse("\u001b]11;rgb:0000/0000/0000\u0007").Should().BeEmpty();
    }

    [Fact]
    public void A_lone_escape_is_the_escape_key()
    {
        Parse("\u001b").Should().Equal(new KeyEvent(KeyKind.Escape));
    }

    [Fact]
    public void Alt_prefixed_letters_decode()
    {
        Parse("\u001bx").Should().Equal(new KeyEvent(KeyKind.Character, 'x', Alt: true));
    }

    [Fact]
    public void Multibyte_characters_decode_to_a_single_event()
    {
        var events = Parse("\u00e9\u4e2d");

        events.Should().HaveCount(2);
        events[0].Character.Should().Be('\u00e9');
        events[1].Character.Should().Be('\u4e2d');
    }
}
