using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Tui;

/// <summary>What a run of the browser should hand back once the screen is restored.</summary>
public enum BrowserExit
{
    /// <summary>The user quit; nothing to do.</summary>
    Quit,

    /// <summary>Launch the harness with <see cref="BrowserResult.Resume"/>.</summary>
    Resume,

    /// <summary>Print <see cref="BrowserResult.Output"/> to stdout, for shell integration.</summary>
    Print
}

/// <summary>The outcome of a browser session.</summary>
/// <param name="Exit">What to do next.</param>
/// <param name="Session">The session involved, when any.</param>
/// <param name="Resume">The harness command to run, for <see cref="BrowserExit.Resume"/>.</param>
/// <param name="Output">The text to print, for <see cref="BrowserExit.Print"/>.</param>
public sealed record BrowserResult(BrowserExit Exit, SessionSummary? Session = null, ResumeCommand? Resume = null, string? Output = null)
{
    /// <summary>A plain quit.</summary>
    public static BrowserResult Quit { get; } = new(BrowserExit.Quit);
}

/// <summary>What Enter should print when the browser runs as a picker.</summary>
public enum PickKind
{
    /// <summary>Normal browsing; Enter opens the reader.</summary>
    None,

    /// <summary>The <c>retrace://</c> URI.</summary>
    Uri,

    /// <summary>The harness's native session id.</summary>
    Id,

    /// <summary>The session's working directory.</summary>
    Directory,

    /// <summary>The resume command line.</summary>
    Command
}

/// <summary>Startup options for the browser.</summary>
public sealed record BrowserOptions
{
    /// <summary>Query to start with, as if typed.</summary>
    public string? InitialQuery { get; init; }

    /// <summary>Filter applied to every list and search, from the CLI's shared filter flags.</summary>
    public SessionFilter Filter { get; init; } = SessionFilter.All;

    /// <summary>Picker mode. <see cref="PickKind.None"/> means a normal browse.</summary>
    public PickKind Pick { get; init; }

    /// <summary>Default search mode from configuration.</summary>
    public SearchMode SearchMode { get; init; } = SearchMode.Hybrid;

    /// <summary>Capture the mouse.</summary>
    public bool Mouse { get; init; } = true;

    /// <summary>Character budget for the transcript loaded into the preview and reader.</summary>
    public int PreviewMaxCharacters { get; init; } = 60000;

    /// <summary>Show tool output in the reader initially.</summary>
    public bool ShowToolOutput { get; init; }

    /// <summary>Show reasoning in the reader initially.</summary>
    public bool ShowReasoning { get; init; }

    /// <summary>Sessions fetched when browsing without a query.</summary>
    public int ListLimit { get; init; } = 200;

    /// <summary>Results fetched per search.</summary>
    public int SearchLimit { get; init; } = 50;

    /// <summary>Use relative dates ("3d ago") in the list.</summary>
    public bool RelativeDates { get; init; } = true;
}
