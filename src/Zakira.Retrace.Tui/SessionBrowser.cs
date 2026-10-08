using System.Collections.Concurrent;
using System.Diagnostics;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Index;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

/// <summary>
/// The interactive session browser: search as you type, read, and act on a session without
/// leaving the keyboard.
/// </summary>
/// <remarks>
/// <para>
/// Single-threaded by construction. A background thread turns terminal input into
/// <see cref="KeyEvent"/>s and queries run on the thread pool, but every piece of state is touched
/// only by the main loop: completed work is posted back as an <see cref="Action"/> and drained
/// between frames. That rule is what lets the rest of the class be written without a single lock.
/// </para>
/// <para>
/// Nothing that can take longer than a frame runs on the loop thread. Searches, transcript reads,
/// the index top-up, and the embedding model's warm-up all happen in the background and report
/// back; the loop's only jobs are to draw, to animate while it waits, and to react to keys.
/// </para>
/// </remarks>
public sealed partial class SessionBrowser : IDisposable
{
    private const int DebounceMilliseconds = 160;
    private const int PreviewDebounceMilliseconds = 90;
    private const int SpinnerIntervalMilliseconds = 80;

    private readonly IBrowserBackend backend;
    private readonly BrowserOptions options;
    private readonly TerminalScreen screen;
    private readonly bool headless;
    private readonly ConcurrentQueue<Action> posted = new();
    private readonly ConcurrentQueue<KeyEvent> keys = new();
    private readonly List<Task> inFlight = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource lifetime = new();

    private ScreenBuffer buffer;
    private bool running = true;
    private bool dirty = true;
    private long lastSpinnerFrame = -1;
    private BrowserResult result = BrowserResult.Quit;

    // ---- list state --------------------------------------------------------------------------

    private sealed record Row(SessionSummary Session, SearchHit? Hit);

    private IReadOnlyList<Row> rows = [];
    private int selected;
    private int listScroll;
    private string query = string.Empty;
    private int caret;
    private bool searchFocused = true;
    private Pane focus = Pane.List;
    private string? sourceFilter;
    private bool here;
    private bool includeArchived;
    private SearchMode searchMode;
    private bool showToolOutput;
    private bool showReasoning;

    // One query runs at a time. A keystroke that arrives while one is in flight marks the query
    // dirty, and the latest text is run once the current one finishes. Starting a fresh search on
    // every keystroke would stack half a dozen database scans on top of each other, each slowing
    // the others down, with only the last one's result wanted.
    private bool queryInFlight;
    private bool queryDirty;
    private bool queryLoading;
    private bool hasLoadedOnce;
    private string? queryError;
    private int queryGeneration;
    private long? queryDueAt;
    private CancellationTokenSource? queryCancellation;
    private bool lastQueryWasSearch;
    private string lastExecutedQuery = string.Empty;
    private TimeSpan lastQueryDuration;

    // ---- transcript state --------------------------------------------------------------------

    private SessionRef? loadedRef;
    private TranscriptOptions? loadedOptions;
    private SessionTranscript? transcript;
    private bool transcriptLoading;
    private bool transcriptLoadingMore;
    private string? transcriptError;
    private long? previewDueAt;
    private CancellationTokenSource? transcriptCancellation;
    private IReadOnlyList<TranscriptLine> formatted = [];
    private int formattedWidth = -1;
    private int previewScroll;
    private int readerScroll;

    // ---- background activity -----------------------------------------------------------------

    private IndexInfo? indexInfo;
    private string? activity;
    private bool refreshing;

    // ---- modes and overlays ------------------------------------------------------------------

    private enum Pane
    {
        List,
        Preview
    }

    private enum Mode
    {
        List,
        Reader,
        Help,
        Info,
        Confirm,
        Input
    }

    private Mode mode = Mode.List;
    private ConfirmState? confirm;
    private InputState? input;
    private string? status;
    private StatusKind statusKind;
    private long statusUntil;

    private enum StatusKind
    {
        Info,
        Ok,
        Error,
        Busy
    }

    private sealed record ConfirmState(string Title, IReadOnlyList<StyledSpan[]> Lines, Action Accept, Action? Copy, string AcceptLabel);

    private sealed class InputState(string prompt, string placeholder, Action<string> submit)
    {
        public string Prompt { get; } = prompt;

        public string Placeholder { get; } = placeholder;

        public Action<string> Submit { get; } = submit;

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Creates a browser attached to the real terminal.</summary>
    public SessionBrowser(IBrowserBackend backend, BrowserOptions options)
        : this(backend, options, new TerminalScreen(options.Mouse, options.ColorDepth), headless: false)
    {
    }

    private SessionBrowser(IBrowserBackend backend, BrowserOptions options, TerminalScreen screen, bool headless)
    {
        this.backend = backend;
        this.options = options;
        this.screen = screen;
        this.headless = headless;
        buffer = new ScreenBuffer(screen.Width, screen.Height);

        searchMode = options.SearchMode;
        showToolOutput = options.ShowToolOutput;
        showReasoning = options.ShowReasoning;
        here = options.Filter.WorkspacePath is not null;
        includeArchived = options.Filter.IncludeArchived;
        sourceFilter = options.Filter.SourceIds.Count == 1 ? options.Filter.SourceIds[0] : null;

        if (!string.IsNullOrWhiteSpace(options.InitialQuery))
        {
            query = options.InitialQuery.Trim();
            caret = query.Length;
            searchFocused = false;
        }
    }

    /// <summary>Creates a browser drawing into memory, for tests.</summary>
    public static SessionBrowser CreateHeadless(IBrowserBackend backend, BrowserOptions options, int width, int height) =>
        new(backend, options, TerminalScreen.CreateHeadless(width, height), headless: true);

    /// <summary>The frame as plain text, for tests.</summary>
    public string Screen
    {
        get
        {
            Draw();
            return buffer.ToText();
        }
    }

    /// <summary>The result so far, for tests.</summary>
    public BrowserResult Result => result;

    /// <summary>Whether the loop would still be running, for tests.</summary>
    public bool IsRunning => running;

    /// <summary>Runs until the user quits or chooses an action, then restores the terminal.</summary>
    public BrowserResult Run(CancellationToken cancellationToken)
    {
        var reader = new Thread(() =>
        {
            foreach (var keyEvent in TerminalInput.Read(() => running, screen.MouseEnabled))
            {
                keys.Enqueue(keyEvent);
            }
        })
        {
            IsBackground = true,
            Name = "retrace-tui-input"
        };

        reader.Start();
        screen.SetTitle("retrace");
        Start();

        try
        {
            while (running)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    result = BrowserResult.Quit;
                    break;
                }

                if (screen.RefreshSize())
                {
                    buffer.Resize(screen.Width, screen.Height);
                    formattedWidth = -1;
                    dirty = true;
                }

                Tick();

                if (dirty)
                {
                    Draw();
                    screen.Present(buffer);
                    dirty = false;
                }

                var handled = false;
                while (keys.TryDequeue(out var keyEvent))
                {
                    HandleKey(keyEvent);
                    handled = true;
                    if (!running)
                    {
                        break;
                    }
                }

                if (!handled && !dirty)
                {
                    // Idle: sleep a frame. While something is animating, Tick marks the frame dirty
                    // when the spinner advances, so the sleep here is what sets the frame rate.
                    Thread.Sleep(IsAnimating ? 16 : 24);
                }
            }
        }
        finally
        {
            running = false;
            lifetime.Cancel();
            queryCancellation?.Cancel();
            transcriptCancellation?.Cancel();
            screen.Dispose();
        }

        return result;
    }

    /// <summary>Kicks off the initial query and the background start-up work. Called by <see cref="Run"/>; exposed for headless tests.</summary>
    public void Start()
    {
        queryDueAt = clock.ElapsedMilliseconds;
        Tick();
        BeginStartupWork();
    }

    /// <summary>Feeds one key, for tests.</summary>
    public void Press(KeyEvent keyEvent) => HandleKey(keyEvent);

    /// <summary>Runs one loop tick without drawing, for tests. Returns whether the frame became dirty.</summary>
    public bool TickForTest()
    {
        dirty = false;
        Tick();
        var wasDirty = dirty;
        dirty = false;
        return wasDirty;
    }

    /// <summary>Fires any pending debounce immediately, for tests, without waiting for the result.</summary>
    public void ForceDueQueriesForTest()
    {
        if (queryDueAt is not null)
        {
            queryDueAt = 0;
        }

        Tick();
    }

    /// <summary>Types a string, for tests.</summary>
    public void Type(string text)
    {
        foreach (var character in text)
        {
            HandleKey(new KeyEvent(KeyKind.Character, character));
        }
    }

    /// <summary>
    /// Waits for every outstanding query and applies its result, for tests. Debounces are
    /// collapsed so a test never has to sleep.
    /// </summary>
    public async Task SettleAsync()
    {
        for (var iteration = 0; iteration < 40; iteration++)
        {
            queryDueAt = queryDueAt is null ? null : 0;
            previewDueAt = previewDueAt is null ? null : 0;
            Tick();

            Task[] pending;
            lock (inFlight)
            {
                inFlight.RemoveAll(task => task.IsCompleted);
                pending = [.. inFlight];
            }

            if (pending.Length == 0 && posted.IsEmpty && queryDueAt is null && previewDueAt is null && !queryDirty)
            {
                return;
            }

            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Failures are reported through posted actions; the loop below applies them.
            }

            Tick();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lifetime.Cancel();
        lifetime.Dispose();
        queryCancellation?.Dispose();
        transcriptCancellation?.Dispose();
        screen.Dispose();
    }

    // ---- loop internals ----------------------------------------------------------------------

    private bool IsAnimating =>
        queryLoading || transcriptLoading || transcriptLoadingMore || refreshing || activity is not null
        || (status is not null && statusKind == StatusKind.Busy);

    private void Tick()
    {
        while (posted.TryDequeue(out var action))
        {
            action();
            dirty = true;
        }

        var now = clock.ElapsedMilliseconds;

        if (queryDueAt is { } due && now >= due)
        {
            queryDueAt = null;
            RequestQuery();
        }

        if (previewDueAt is { } previewDue && now >= previewDue)
        {
            previewDueAt = null;
            LoadTranscriptForSelection();
        }

        if (status is not null && now > statusUntil)
        {
            status = null;
            dirty = true;
        }

        // Animation is driven here, not from Draw: a frame is marked dirty exactly when the
        // spinner would show a different glyph, so the loop redraws at the spinner's cadence and
        // not once per iteration.
        if (IsAnimating)
        {
            var frame = now / SpinnerIntervalMilliseconds;
            if (frame != lastSpinnerFrame)
            {
                lastSpinnerFrame = frame;
                dirty = true;
            }
        }
    }

    private void Post(Action action)
    {
        posted.Enqueue(action);
    }

    /// <summary>
    /// An <see cref="IProgress{T}"/> that posts straight onto the loop's queue, in order.
    /// </summary>
    /// <remarks>
    /// <see cref="Progress{T}"/> hands each report to the thread pool when there is no
    /// synchronisation context, so a report raised just before an operation completed could be
    /// delivered after the completion itself and leave a stale "indexing…" on the status bar
    /// forever. Enqueuing inline keeps reports and completion in the order they happened.
    /// </remarks>
    private sealed class PostedProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private PostedProgress<IndexProgress> ActivityProgress(Func<IndexProgress, string> describe) =>
        new PostedProgress<IndexProgress>(report => Post(() => activity = describe(report)));

    private void Track(Task task)
    {
        lock (inFlight)
        {
            inFlight.RemoveAll(item => item.IsCompleted);
            inFlight.Add(task);
        }
    }

    private void SetStatus(string message, StatusKind kind = StatusKind.Info, int milliseconds = 3500)
    {
        status = message;
        statusKind = kind;
        statusUntil = clock.ElapsedMilliseconds + milliseconds;
        dirty = true;
    }

    private Row? Selected => rows.Count == 0 ? null : rows[Math.Clamp(selected, 0, rows.Count - 1)];

    // ---- start-up work -----------------------------------------------------------------------

    /// <summary>
    /// Everything that should happen once the first frame is up and the user can already type:
    /// read index facts for the header, load the embedding model so the first search is not the
    /// slow one, and top the index up with whatever changed since the last run.
    /// </summary>
    private void BeginStartupWork()
    {
        var token = lifetime.Token;

        var task = Task.Run(async () =>
        {
            try
            {
                var info = await backend.GetIndexInfoAsync(token).ConfigureAwait(false);
                Post(() => indexInfo = info);

                if (info.SemanticAvailable && searchMode != SearchMode.Lexical)
                {
                    Post(() => activity = "warming up semantic search");
                    await backend.WarmUpAsync(token).ConfigureAwait(false);
                    Post(() => activity = null);
                }

                Post(() => activity = "checking the index for new sessions");
                var progress = ActivityProgress(report => $"indexing {report.SourceId} \u00b7 {report.Processed:N0} session(s)");

                var changed = await backend.TopUpIndexAsync(progress, token).ConfigureAwait(false);

                Post(() =>
                {
                    activity = null;
                    if (changed)
                    {
                        SetStatus("index topped up with recent sessions", StatusKind.Ok, milliseconds: 4000);
                        ScheduleQuery(immediate: true);
                    }
                });

                var refreshed = await backend.GetIndexInfoAsync(token).ConfigureAwait(false);
                Post(() => indexInfo = refreshed);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    activity = null;
                    SetStatus($"start-up: {ex.Message}", StatusKind.Error, milliseconds: 6000);
                });
            }
        }, token);

        Track(task);
    }

    // ---- queries -----------------------------------------------------------------------------

    private void ScheduleQuery(bool immediate = false)
    {
        queryDueAt = clock.ElapsedMilliseconds + (immediate ? 0 : DebounceMilliseconds);
        dirty = true;
    }

    private SessionFilter BuildFilter(int limit)
    {
        var filter = options.Filter with
        {
            Limit = limit,
            IncludeArchived = includeArchived,
            SourceIds = sourceFilter is null ? options.Filter.SourceIds : [sourceFilter],
            WorkspacePath = here ? options.Filter.WorkspacePath ?? Directory.GetCurrentDirectory() : null
        };

        return filter;
    }

    /// <summary>Runs the current query now if nothing is running, or notes that it should run next.</summary>
    private void RequestQuery()
    {
        if (queryInFlight)
        {
            queryDirty = true;
            queryLoading = true;
            dirty = true;
            return;
        }

        ExecuteQuery();
    }

    private void ExecuteQuery()
    {
        queryCancellation?.Dispose();
        queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = queryCancellation.Token;
        var generation = ++queryGeneration;
        var text = query.Trim();
        var isSearch = text.Length > 0;
        var filter = BuildFilter(isSearch ? 0 : options.ListLimit);
        var mode = searchMode;
        var started = Stopwatch.StartNew();

        queryInFlight = true;
        queryDirty = false;
        queryLoading = true;
        queryError = null;
        dirty = true;

        var task = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<Row> fetched;

                if (isSearch)
                {
                    var hits = await backend.SearchAsync(
                        new SearchQuery
                        {
                            Text = text,
                            Filter = filter,
                            Top = options.SearchLimit,
                            SnippetsPerSession = 3,
                            Mode = mode
                        },
                        token).ConfigureAwait(false);

                    fetched = [.. hits.Select(hit => new Row(hit.Session, hit))];
                }
                else
                {
                    var sessions = await backend.ListAsync(filter, token).ConfigureAwait(false);
                    fetched = [.. sessions.Select(session => new Row(session, null))];
                }

                Post(() => ApplyResults(generation, fetched, isSearch, text, started.Elapsed));
            }
            catch (OperationCanceledException)
            {
                Post(() => FinishQuery(generation));
            }
            catch (IndexNotBuiltException)
            {
                Post(() => ApplyQueryError(generation, "No index yet. Press Ctrl+R to build one (keyword-only), or run `retrace index build`."));
            }
            catch (Exception ex)
            {
                Post(() => ApplyQueryError(generation, ex.Message));
            }
        }, token);

        Track(task);
    }

    /// <summary>Marks the in-flight query finished and starts the next one if the text moved on.</summary>
    private void FinishQuery(int generation)
    {
        if (generation != queryGeneration)
        {
            return;
        }

        queryInFlight = false;

        if (queryDirty)
        {
            ExecuteQuery();
        }
        else
        {
            queryLoading = false;
        }

        dirty = true;
    }

    private void ApplyResults(int generation, IReadOnlyList<Row> fetched, bool isSearch, string text, TimeSpan duration)
    {
        if (generation != queryGeneration)
        {
            return;
        }

        // The text moved on while this ran: do not flash an intermediate result set, just run the
        // current text. The previous results stay on screen until the real answer arrives.
        if (!string.Equals(text, query.Trim(), StringComparison.Ordinal))
        {
            FinishQuery(generation);
            return;
        }

        var previous = Selected?.Session.Ref;

        rows = fetched;
        hasLoadedOnce = true;
        lastQueryWasSearch = isSearch;
        lastExecutedQuery = text;
        lastQueryDuration = duration;

        var keep = previous is null ? -1 : IndexOfRef(previous);
        selected = keep >= 0 ? keep : 0;
        listScroll = Math.Min(listScroll, Math.Max(0, selected));
        EnsureSelectionVisible();

        formattedWidth = -1;
        previewScroll = 0;
        SchedulePreview(immediate: true);
        FinishQuery(generation);
    }

    private void ApplyQueryError(int generation, string message)
    {
        if (generation != queryGeneration)
        {
            return;
        }

        queryError = message;
        hasLoadedOnce = true;
        rows = [];
        selected = 0;
        listScroll = 0;
        FinishQuery(generation);
    }

    private int IndexOfRef(SessionRef reference)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Session.Ref == reference)
            {
                return index;
            }
        }

        return -1;
    }

    // ---- transcript loading ------------------------------------------------------------------

    private void SchedulePreview(bool immediate = false)
    {
        previewDueAt = clock.ElapsedMilliseconds + (immediate ? 0 : PreviewDebounceMilliseconds);
    }

    private TranscriptOptions CurrentTranscriptOptions => new()
    {
        IncludeToolOutput = showToolOutput,
        IncludeReasoning = showReasoning,
        MaxCharacters = options.PreviewMaxCharacters,
        MaxToolOutputCharacters = 4000
    };

    private void LoadTranscriptForSelection()
    {
        var row = Selected;
        if (row is null)
        {
            transcript = null;
            loadedRef = null;
            formatted = [];
            transcriptError = null;
            dirty = true;
            return;
        }

        var wanted = CurrentTranscriptOptions;
        if (row.Session.Ref == loadedRef && wanted == loadedOptions && (transcript is not null || transcriptLoading))
        {
            return;
        }

        transcriptCancellation?.Cancel();
        transcriptCancellation?.Dispose();
        transcriptCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = transcriptCancellation.Token;
        var reference = row.Session.Ref;

        loadedRef = reference;
        loadedOptions = wanted;
        transcript = null;
        formatted = [];
        formattedWidth = -1;
        transcriptLoading = true;
        transcriptError = null;
        previewScroll = 0;
        readerScroll = 0;
        dirty = true;

        var task = Task.Run(async () =>
        {
            try
            {
                var loaded = await backend.GetTranscriptAsync(reference, wanted, token).ConfigureAwait(false);
                Post(() =>
                {
                    if (loadedRef != reference || loadedOptions != wanted)
                    {
                        return;
                    }

                    transcript = loaded;
                    transcriptLoading = false;
                    formattedWidth = -1;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    if (loadedRef != reference)
                    {
                        return;
                    }

                    transcriptLoading = false;
                    transcriptError = ex.Message;
                });
            }
        }, token);

        Track(task);
    }

    /// <summary>Appends the next window of turns when the loaded transcript was cut by the character budget.</summary>
    private void LoadMoreTurns()
    {
        if (transcript is not { IsTruncated: true, NextTurnIndex: { } next } current || transcriptLoadingMore || loadedRef is null || loadedOptions is null)
        {
            return;
        }

        var reference = loadedRef;
        var wanted = loadedOptions with { FromTurn = next };
        var token = transcriptCancellation?.Token ?? lifetime.Token;

        transcriptLoadingMore = true;
        dirty = true;

        var task = Task.Run(async () =>
        {
            try
            {
                var more = await backend.GetTranscriptAsync(reference, wanted, token).ConfigureAwait(false);
                Post(() =>
                {
                    transcriptLoadingMore = false;
                    if (loadedRef != reference || transcript != current)
                    {
                        return;
                    }

                    transcript = current with
                    {
                        Turns = [.. current.Turns, .. more.Turns],
                        Files = more.Files.Count > current.Files.Count ? more.Files : current.Files,
                        IsTruncated = more.IsTruncated,
                        NextTurnIndex = more.NextTurnIndex,
                        TotalTurns = Math.Max(current.TotalTurns, more.TotalTurns)
                    };
                    formattedWidth = -1;
                });
            }
            catch (OperationCanceledException)
            {
                Post(() => transcriptLoadingMore = false);
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    transcriptLoadingMore = false;
                    SetStatus(ex.Message, StatusKind.Error);
                });
            }
        }, token);

        Track(task);
    }

    private IReadOnlyList<TranscriptLine> FormattedLines(int width)
    {
        if (transcript is null)
        {
            return [];
        }

        if (formattedWidth != width)
        {
            formatted = TranscriptFormatter.Format(
                transcript,
                new TranscriptFormatOptions(width, showToolOutput, showReasoning, TranscriptFormatter.TermsOf(lastExecutedQuery)));
            formattedWidth = width;
        }

        return formatted;
    }

    private void InvalidateTranscript()
    {
        loadedOptions = null;
        SchedulePreview(immediate: true);
    }
}
