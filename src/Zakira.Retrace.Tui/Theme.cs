using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

/// <summary>
/// The browser's palette and the styles built from it.
/// </summary>
/// <remarks>
/// <para>
/// A restrained dark palette in the Tokyo Night family: one accent, one brand tint, warm amber for
/// anything that needs to be found at a glance, and several steps of blue-grey for everything
/// that should recede. Body text is left at the terminal's own foreground so the browser sits
/// inside the user's theme rather than on top of it.
/// </para>
/// <para>
/// Every colour carries an ANSI fallback, so on a sixteen-colour terminal the result is a chosen
/// scheme rather than whatever the nearest-match arithmetic happened to produce.
/// </para>
/// </remarks>
internal static class Theme
{
    // ---- palette -----------------------------------------------------------------------------

    public static Color Accent { get; } = Color.Hex("#7aa2f7", AnsiColor.BrightBlue);

    public static Color Brand { get; } = Color.Hex("#bb9af7", AnsiColor.BrightMagenta);

    public static Color Text { get; } = Color.Default;

    public static Color TextBright { get; } = Color.Hex("#c0caf5", AnsiColor.BrightWhite);

    public static Color TextSecondary { get; } = Color.Hex("#a9b1d6", AnsiColor.White);

    public static Color Muted { get; } = Color.Hex("#565f89", AnsiColor.BrightBlack);

    public static Color Faint { get; } = Color.Hex("#3b4261", AnsiColor.BrightBlack);

    public static Color Selection { get; } = Color.Hex("#283457", AnsiColor.Blue);

    public static Color SelectionInactive { get; } = Color.Hex("#1f2335", AnsiColor.BrightBlack);

    public static Color Surface { get; } = Color.Hex("#1f2335", AnsiColor.Black);

    public static Color SurfaceRaised { get; } = Color.Hex("#292e42", AnsiColor.BrightBlack);

    public static Color Shadow { get; } = Color.Hex("#0d0f17", AnsiColor.Black);

    public static Color Green { get; } = Color.Hex("#9ece6a", AnsiColor.BrightGreen);

    public static Color Amber { get; } = Color.Hex("#e0af68", AnsiColor.Yellow);

    public static Color Red { get; } = Color.Hex("#f7768e", AnsiColor.BrightRed);

    public static Color Cyan { get; } = Color.Hex("#7dcfff", AnsiColor.BrightCyan);

    public static Color Purple { get; } = Color.Hex("#bb9af7", AnsiColor.BrightMagenta);

    public static Color Orange { get; } = Color.Hex("#ff9e64", AnsiColor.Yellow);

    public static Color Ink { get; } = Color.Hex("#1a1b26", AnsiColor.Black);

    // ---- chrome ------------------------------------------------------------------------------

    public static Style Border { get; } = Style.Fg(Faint);

    public static Style BorderFocused { get; } = Style.Fg(Accent);

    public static Style PaneTitle { get; } = Style.Fg(TextSecondary).Bold();

    public static Style PaneTitleFocused { get; } = Style.Fg(Accent).Bold();

    public static Style PaneCount { get; } = Style.Fg(Muted);

    public static Style BrandMark { get; } = Style.Fg(Brand).Bold();

    public static Style BrandName { get; } = Style.Fg(Accent).Bold();

    public static Style Prompt { get; } = Style.Fg(Accent).Bold();

    public static Style PromptIdle { get; } = Style.Fg(Muted);

    public static Style Placeholder { get; } = Style.Fg(Muted).Italic();

    public static Style Caret { get; } = new(Ink, Accent);

    public static Style ScrollTrack { get; } = Style.Fg(Faint);

    public static Style ScrollThumb { get; } = Style.Fg(Muted);

    // ---- text ------------------------------------------------------------------------------

    public static Style Body { get; } = Style.Plain;

    public static Style Secondary { get; } = Style.Fg(TextSecondary);

    public static Style Dim { get; } = Style.Fg(Muted);

    public static Style Date { get; } = Style.Fg(Muted);

    public static Style Uri { get; } = Style.Fg(Accent);

    public static Style Heading { get; } = Style.Fg(TextBright).Bold();

    public static Style Label { get; } = Style.Fg(Muted);

    public static Style Match { get; } = new(Ink, Amber, TermAttr.Bold);

    public static Style Ok { get; } = Style.Fg(Green);

    public static Style Warn { get; } = Style.Fg(Amber);

    public static Style Error { get; } = Style.Fg(Red);

    public static Style Spinner { get; } = Style.Fg(Accent);

    // ---- list ------------------------------------------------------------------------------

    public static Style SelectionBar { get; } = Style.Fg(Accent).Bold();

    public static Style RowTitle { get; } = Style.Plain;

    public static Style Snippet { get; } = Style.Fg(TextSecondary);

    public static Style SnippetRole { get; } = Style.Fg(Muted);

    public static Style Chip { get; } = new(TextSecondary, SurfaceRaised);

    public static Style ChipAccent { get; } = new(Accent, SurfaceRaised);

    public static Style ChipWarn { get; } = new(Amber, SurfaceRaised);

    // ---- hints and overlays --------------------------------------------------------------

    public static Style HintKey { get; } = Style.Fg(Accent);

    public static Style HintLabel { get; } = Style.Fg(Muted);

    public static Style KeyCap { get; } = new(Accent, SurfaceRaised, TermAttr.Bold);

    public static Style Overlay { get; } = new(TextBright, Surface);

    public static Style OverlayMuted { get; } = new(Muted, Surface);

    public static Style OverlaySecondary { get; } = new(TextSecondary, Surface);

    public static Style OverlayBorder { get; } = new(Accent, Surface);

    public static Style OverlayTitle { get; } = new(TextBright, Surface, TermAttr.Bold);

    public static Style OverlaySection { get; } = new(Amber, Surface, TermAttr.Bold);

    public static Style OverlayKeyCap { get; } = new(Accent, SurfaceRaised, TermAttr.Bold);

    public static Style OverlayAccent { get; } = new(Accent, Surface);

    public static Style OverlayWarn { get; } = new(Amber, Surface);

    public static Style OverlayError { get; } = new(Red, Surface);

    // ---- transcript ------------------------------------------------------------------------

    public static Style TranscriptMeta { get; } = Style.Fg(Muted);

    public static Style TranscriptMetaValue { get; } = Style.Fg(TextSecondary);

    public static Style Reasoning { get; } = Style.Fg(Muted).Italic();

    public static Style ToolOutput { get; } = Style.Fg(Muted);

    public static Style ToolMarker { get; } = Style.Fg(Amber);

    public static Style PatchMarker { get; } = Style.Fg(Purple);

    public static Color RoleColor(Abstractions.TurnRole role) => role switch
    {
        Abstractions.TurnRole.User => Green,
        Abstractions.TurnRole.Assistant => Accent,
        Abstractions.TurnRole.Tool => Amber,
        _ => Muted
    };

    public static (string Label, Color Color) SourceBadge(string sourceId) => sourceId switch
    {
        "opencode" => ("opencode", Purple),
        "copilot-cli" => ("copilot", Cyan),
        "copilot-vscode" => ("vscode", Green),
        _ => (sourceId, TextSecondary)
    };
}
