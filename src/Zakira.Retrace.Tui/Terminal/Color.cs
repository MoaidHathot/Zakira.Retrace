using System.Globalization;

namespace Zakira.Retrace.Tui.Terminal;

/// <summary>The sixteen ANSI colours, used directly on basic terminals and as fallbacks for RGB colours.</summary>
public enum AnsiColor : byte
{
    /// <summary>ANSI 0.</summary>
    Black,

    /// <summary>ANSI 1.</summary>
    Red,

    /// <summary>ANSI 2.</summary>
    Green,

    /// <summary>ANSI 3.</summary>
    Yellow,

    /// <summary>ANSI 4.</summary>
    Blue,

    /// <summary>ANSI 5.</summary>
    Magenta,

    /// <summary>ANSI 6.</summary>
    Cyan,

    /// <summary>ANSI 7.</summary>
    White,

    /// <summary>ANSI 8, the bright black most themes render as grey.</summary>
    BrightBlack,

    /// <summary>ANSI 9.</summary>
    BrightRed,

    /// <summary>ANSI 10.</summary>
    BrightGreen,

    /// <summary>ANSI 11.</summary>
    BrightYellow,

    /// <summary>ANSI 12.</summary>
    BrightBlue,

    /// <summary>ANSI 13.</summary>
    BrightMagenta,

    /// <summary>ANSI 14.</summary>
    BrightCyan,

    /// <summary>ANSI 15.</summary>
    BrightWhite
}

/// <summary>How many colours the terminal can show.</summary>
public enum ColorDepth
{
    /// <summary>No colour; attributes only. What <c>NO_COLOR</c> asks for.</summary>
    None,

    /// <summary>The sixteen ANSI colours.</summary>
    Ansi16,

    /// <summary>The xterm 256-colour palette.</summary>
    Ansi256,

    /// <summary>24-bit colour.</summary>
    TrueColor
}

/// <summary>
/// A terminal colour: the terminal's default, one of the sixteen ANSI colours, or an RGB value
/// carrying its own ANSI fallback.
/// </summary>
/// <remarks>
/// RGB colours are what make the browser look designed rather than assembled, but not every
/// terminal has them. Rather than guess a fallback at render time, each palette colour names the
/// basic colour it should become, so the sixteen-colour rendering is a deliberate choice too.
/// A <see langword="default"/> value is the terminal default, so an unstyled cell costs nothing.
/// </remarks>
public readonly record struct Color
{
    private readonly byte kind;

    private Color(byte kind, byte r, byte g, byte b, AnsiColor ansi)
    {
        this.kind = kind;
        R = r;
        G = g;
        B = b;
        Ansi = ansi;
    }

    /// <summary>Red component, for RGB colours.</summary>
    public byte R { get; }

    /// <summary>Green component, for RGB colours.</summary>
    public byte G { get; }

    /// <summary>Blue component, for RGB colours.</summary>
    public byte B { get; }

    /// <summary>The ANSI colour, or the ANSI fallback of an RGB colour.</summary>
    public AnsiColor Ansi { get; }

    /// <summary>The terminal's own default foreground or background.</summary>
    public static Color Default => default;

    /// <summary>Whether this is the terminal default.</summary>
    public bool IsDefault => kind == 0;

    /// <summary>Whether this carries an RGB value.</summary>
    public bool IsRgb => kind == 2;

    /// <summary>One of the sixteen ANSI colours.</summary>
    public static Color FromAnsi(AnsiColor color) => new(1, 0, 0, 0, color);

    /// <summary>An RGB colour with the basic colour it degrades to.</summary>
    public static Color Rgb(byte r, byte g, byte b, AnsiColor fallback) => new(2, r, g, b, fallback);

    /// <summary>An RGB colour from <c>#rrggbb</c>.</summary>
    public static Color Hex(string hex, AnsiColor fallback)
    {
        var span = hex.AsSpan().TrimStart('#');
        if (span.Length != 6)
        {
            throw new ArgumentException($"'{hex}' is not a #rrggbb colour.", nameof(hex));
        }

        return Rgb(
            byte.Parse(span[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            fallback);
    }

    /// <summary>Blends toward another RGB colour; <paramref name="amount"/> 0 is this colour, 1 is the other. Non-RGB operands are returned unchanged.</summary>
    public Color Mix(Color other, double amount)
    {
        if (!IsRgb || !other.IsRgb)
        {
            return amount >= 0.5 ? other : this;
        }

        amount = Math.Clamp(amount, 0, 1);
        return Rgb(
            (byte)Math.Round(R + (other.R - R) * amount),
            (byte)Math.Round(G + (other.G - G) * amount),
            (byte)Math.Round(B + (other.B - B) * amount),
            amount >= 0.5 ? other.Ansi : Ansi);
    }

    /// <summary>Nearest entry in the xterm 256-colour palette.</summary>
    public int ToAnsi256()
    {
        // Grey ramp: indices 232..255 cover 8..238 in steps of 10.
        var grey = (int)Math.Round(R * 0.299 + G * 0.587 + B * 0.114);
        var greyIndex = Math.Clamp((grey - 8) / 10, 0, 23);
        var greyValue = 8 + greyIndex * 10;

        // Colour cube: 0, 95, 135, 175, 215, 255.
        static int Level(int value) => value < 48 ? 0 : value < 115 ? 1 : (value - 35) / 40;
        static int Value(int level) => level == 0 ? 0 : 55 + level * 40;

        var (rl, gl, bl) = (Level(R), Level(G), Level(B));
        var cubeDistance = Distance(R, G, B, Value(rl), Value(gl), Value(bl));
        var greyDistance = Distance(R, G, B, greyValue, greyValue, greyValue);

        return greyDistance < cubeDistance ? 232 + greyIndex : 16 + 36 * rl + 6 * gl + bl;
    }

    private static int Distance(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        var dr = r1 - r2;
        var dg = g1 - g2;
        var db = b1 - b2;
        return 3 * dr * dr + 4 * dg * dg + 2 * db * db;
    }

    /// <inheritdoc />
    public override string ToString() => kind switch
    {
        0 => "default",
        1 => Ansi.ToString(),
        _ => $"#{R:x2}{G:x2}{B:x2}"
    };
}
