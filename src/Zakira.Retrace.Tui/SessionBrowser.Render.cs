using System.Globalization;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Text;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

public sealed partial class SessionBrowser
{
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

    private static class Theme
    {
        public static Style Border { get; } = Style.Fg(TermColor.BrightBlack);

        public static Style BorderFocused { get; } = Style.Fg(TermColor.Green);

        public static Style Title { get; } = Style.Plain.Bold();

        public static Style TitleFocused { get; } = Style.Fg(TermColor.Green).Bold();

        public static Style Dim { get; } = Style.Fg(TermColor.BrightBlack);

        public static Style Date { get; } = Style.Fg(TermColor.BrightBlack);

        public static Style RowTitle { get; } = Style.Plain;

        public static Style Selected { get; } = new(TermColor.White, TermColor.Blue, TermAttr.Bold);

        public static Style SelectedInactive { get; } = new(TermColor.White, TermColor.BrightBlack);

        public static Style Gutter { get; } = Style.Fg(TermColor.Cyan).Bold();

        public static Style Hint { get; } = Style.Fg(TermColor.BrightBlack);

        public static Style HintKey { get; } = Style.Fg(TermColor.Cyan);

        public static Style Error { get; } = Style.Fg(TermColor.Red);

        public static Style Ok { get; } = Style.Fg(TermColor.Green);

        public static Style Chip { get; } = Style.Fg(TermColor.Yellow);

        public static Style Caret { get; } = Style.Plain.With(TermAttr.Reverse);

        public static Style Overlay { get; } = Style.Plain;

        public static Style OverlayBorder { get; } = Style.Fg(TermColor.Cyan);

        public static Style Match { get; } = new(TermColor.Black, TermColor.Yellow, TermAttr.Bold);
    }

    private readonly record struct Layout(Rect Search, Rect List, Rect Preview, int StatusRow, bool ShowPreview);

    private Layout ComputeLayout()
    {
        var width = buffer.Width;
        var height = buffer.Height;
        var search = new Rect(0, 0, width, 3);
        var bodyTop = 3;
        var bodyHeight = Math.Max(3, height - bodyTop - 1);
        var showPreview = width >= 100;
        var listWidth = showPreview ? Math.Clamp((int)(width * 0.44), 44, 96) : width;

        return new Layout(
            search,
            new Rect(0, bodyTop, listWidth, bodyHeight),
            showPreview ? new Rect(listWidth, bodyTop, width - listWidth, bodyHeight) : new Rect(0, 0, 0, 0),
            height - 1,
            showPreview);
    }

    private int ListPageSize()
    {
        var layout = ComputeLayout();
        return Math.Max(1, layout.List.Inset().Height / 2);
    }

    private int PreviewPageSize()
    {
        var layout = ComputeLayout();
        return Math.Max(1, layout.Preview.Inset().Height);
    }

    private int ReaderPageSize() => Math.Max(1, buffer.Height - 3);

    private void EnsureSelectionVisible()
    {
        var page = ListPageSize();
        if (selected < listScroll)
        {
            listScroll = selected;
        }
        else if (selected >= listScroll + page)
        {
            listScroll = selected - page + 1;
        }

        listScroll = Math.Clamp(listScroll, 0, Math.Max(0, rows.Count - 1));
    }

    private string Spinner => SpinnerFrames[(int)(clock.ElapsedMilliseconds / 80 % SpinnerFrames.Length)];

    private void Draw()
    {
        buffer.Clear();

        if (queryLoading || transcriptLoading || transcriptLoadingMore || refreshing)
        {
            // Keep the spinner moving while anything is in flight.
            dirty = true;
        }

        switch (mode)
        {
            case Mode.Reader:
                DrawReader();
                break;

            default:
                DrawBrowse();
                break;
        }

        DrawStatusBar();

        switch (mode)
        {
            case Mode.Help:
                DrawHelp();
                break;

            case Mode.Info:
                DrawInfo();
                break;

            case Mode.Confirm when confirm is not null:
                DrawConfirm(confirm);
                break;

            case Mode.Input when input is not null:
                DrawInput(input);
                break;
        }
    }

    // ---- browse: search box, list, preview ---------------------------------------------------

    private void DrawBrowse()
    {
        var layout = ComputeLayout();
        DrawSearchBox(layout.Search);
        DrawList(layout.List, focused: !searchFocused && focus == Pane.List);

        if (layout.ShowPreview)
        {
            DrawPreview(layout.Preview, focused: !searchFocused && focus == Pane.Preview);
        }
    }

    private void DrawSearchBox(Rect rect)
    {
        var focused = searchFocused;
        buffer.DrawBox(rect, focused ? Theme.BorderFocused : Theme.Border, "Search", focused ? Theme.TitleFocused : Theme.Title);

        var inner = rect.Inset();
        var column = 1;
        column += buffer.WriteIn(inner, 0, column, "/ ", Theme.Dim);

        var modeChip = $"[{SearchModeLabel(searchMode)}]";
        var chipWidth = TextWidth.Of(modeChip) + 1;
        var available = Math.Max(1, inner.Width - column - chipWidth);

        if (query.Length == 0 && !focused)
        {
            buffer.WriteIn(inner, 0, column, "type / to search; empty shows recent sessions", Theme.Dim);
        }
        else
        {
            // Keep the caret in view when the query outgrows the box.
            var start = 0;
            var caretWidth = TextWidth.Of(query.AsSpan(0, Math.Clamp(caret, 0, query.Length)));
            while (caretWidth - TextWidth.Of(query.AsSpan(0, start)) >= available && start < query.Length)
            {
                start++;
            }

            var visible = query[start..];
            buffer.Write(inner.Top, inner.Left + column, visible, Style.Plain, available);

            if (focused)
            {
                var caretColumn = inner.Left + column + TextWidth.Of(query.AsSpan(start, Math.Clamp(caret, start, query.Length) - start));
                var under = caret < query.Length ? query[caret].ToString() : " ";
                buffer.Write(inner.Top, caretColumn, under, Theme.Caret, 1);
            }
        }

        buffer.Write(inner.Top, inner.Right - chipWidth, modeChip, Theme.Chip, chipWidth);
    }

    private void DrawList(Rect rect, bool focused)
    {
        var count = rows.Count;
        var title = lastQueryWasSearch
            ? $"Matches ({count})"
            : $"Sessions ({count})";

        buffer.DrawBox(rect, focused ? Theme.BorderFocused : Theme.Border, title, focused ? Theme.TitleFocused : Theme.Title);
        var inner = rect.Inset();
        if (inner.IsEmpty)
        {
            return;
        }

        if (queryError is not null)
        {
            var row = 0;
            foreach (var line in TextWidth.Wrap(queryError, Math.Max(4, inner.Width - 2)))
            {
                buffer.WriteIn(inner, row++, 1, line, Theme.Error);
            }

            return;
        }

        if (count == 0)
        {
            var message = queryLoading
                ? $"{Spinner} {(lastQueryWasSearch || query.Length > 0 ? "searching" : "loading")}\u2026"
                : query.Length > 0
                    ? "No matches."
                    : "No sessions.";
            buffer.WriteIn(inner, 0, 1, message, Theme.Dim);
            return;
        }

        var page = Math.Max(1, inner.Height / 2);
        EnsureSelectionVisible();
        var now = DateTimeOffset.UtcNow;
        var hasScrollbar = count > page;
        var contentWidth = inner.Width - (hasScrollbar ? 1 : 0);
        var content = new Rect(inner.Left, inner.Top, contentWidth, inner.Height);

        for (var slot = 0; slot < page; slot++)
        {
            var index = listScroll + slot;
            if (index >= count)
            {
                break;
            }

            var row = rows[index];
            var isSelected = index == selected;
            var top = slot * 2;

            DrawListRow(content, top, row, now);

            if (isSelected)
            {
                var highlight = focused ? Theme.Selected : Theme.SelectedInactive;
                for (var line = 0; line < 2 && top + line < content.Height; line++)
                {
                    buffer.RestyleRow(content, top + line, style => style.Background == TermColor.Yellow
                        ? style
                        : new Style(
                            style.Foreground is TermColor.Default or TermColor.BrightBlack ? highlight.Foreground : style.Foreground,
                            highlight.Background,
                            (style.Attributes & ~TermAttr.Dim) | (line == 0 ? highlight.Attributes : TermAttr.None)));
                }

                buffer.WriteIn(content, top, 0, "\u258d", Theme.Gutter.WithBg(highlight.Background));
            }
        }

        if (hasScrollbar)
        {
            buffer.DrawScrollbar(new Rect(inner.Right - 1, inner.Top, 1, inner.Height), count, page, listScroll, Theme.Border);
        }
    }

    private void DrawListRow(Rect content, int top, Row row, DateTimeOffset now)
    {
        var session = row.Session;
        var date = TextWidth.Fit(FormatDate(session.UpdatedAt, now), 8);
        var (sourceLabel, sourceStyle) = SourceBadge(session.Ref.SourceId);

        var column = 1;
        column += buffer.WriteIn(content, top, column, date, Theme.Date);
        column += buffer.WriteIn(content, top, column, " ", Style.Plain);
        column += buffer.WriteIn(content, top, column, TextWidth.Fit(sourceLabel, 8), sourceStyle);
        column += buffer.WriteIn(content, top, column, " ", Style.Plain);

        var title = TextUtilities.Flatten(session.Title);
        if (string.IsNullOrEmpty(title))
        {
            title = "(untitled)";
        }

        var titleWidth = Math.Max(0, content.Width - column - 1);
        var terms = TranscriptFormatter.TermsOf(lastExecutedQuery);
        var (titleSpans, _) = TranscriptFormatter.Highlight(TextWidth.Clip(title, titleWidth), Theme.RowTitle, terms);
        buffer.WriteSpans(content, top, column, titleSpans);

        if (top + 1 >= content.Height)
        {
            return;
        }

        var detailColumn = 1 + 8 + 1;
        var detailWidth = Math.Max(0, content.Width - detailColumn - 1);

        if (row.Hit is { Snippets.Count: > 0 } hit)
        {
            var snippet = hit.Snippets[0];
            var text = TextUtilities.Flatten(snippet.Highlighted ?? snippet.Text)
                .Replace("<<", string.Empty, StringComparison.Ordinal)
                .Replace(">>", string.Empty, StringComparison.Ordinal);
            var label = TextUtilities.RoleLabel(snippet.Role) + ": ";
            var (spans, _) = TranscriptFormatter.Highlight(TextWidth.Clip(text, Math.Max(0, detailWidth - label.Length)), Theme.Dim, terms);
            var all = new List<StyledSpan> { new(label, Theme.Dim) };
            all.AddRange(spans);
            buffer.WriteSpans(content, top + 1, detailColumn, all);
        }
        else
        {
            buffer.WriteIn(content, top + 1, detailColumn, TextWidth.Clip(DetailLine(session), detailWidth), Theme.Dim);
        }
    }

    private void DrawPreview(Rect rect, bool focused)
    {
        var row = Selected;
        var title = row is null ? "Preview" : $"Preview  {row.Session.Ref.ShortForm}";
        buffer.DrawBox(rect, focused ? Theme.BorderFocused : Theme.Border, title, focused ? Theme.TitleFocused : Theme.Title);

        var inner = rect.Inset();
        if (inner.IsEmpty || row is null)
        {
            return;
        }

        var lines = BuildPreviewLines(row, inner.Width - 2);
        var total = lines.Count;
        var hasScrollbar = total > inner.Height;
        var contentWidth = inner.Width - (hasScrollbar ? 1 : 0);
        var content = new Rect(inner.Left, inner.Top, contentWidth, inner.Height);

        previewScroll = Math.Clamp(previewScroll, 0, Math.Max(0, total - inner.Height));

        for (var slot = 0; slot < inner.Height; slot++)
        {
            var index = previewScroll + slot;
            if (index >= total)
            {
                break;
            }

            buffer.WriteSpans(content, slot, 1, lines[index].Spans);
        }

        if (hasScrollbar)
        {
            buffer.DrawScrollbar(new Rect(inner.Right - 1, inner.Top, 1, inner.Height), total, inner.Height, previewScroll, Theme.Border);
        }
    }

    /// <summary>Snippets first, so the matched passage is the first thing visible, then the transcript.</summary>
    private List<TranscriptLine> BuildPreviewLines(Row row, int width)
    {
        var lines = new List<TranscriptLine>();
        var terms = TranscriptFormatter.TermsOf(lastExecutedQuery);

        if (row.Hit is { Snippets.Count: > 0 } hit)
        {
            lines.Add(new TranscriptLine([new StyledSpan("Matches", Theme.Chip.Bold())], -1, false, false));
            foreach (var snippet in hit.Snippets)
            {
                var text = TextUtilities.Flatten(snippet.Highlighted ?? snippet.Text)
                    .Replace("<<", string.Empty, StringComparison.Ordinal)
                    .Replace(">>", string.Empty, StringComparison.Ordinal);
                var label = $"[{snippet.TurnIndex}] {TextUtilities.RoleLabel(snippet.Role)}: ";
                var first = true;
                foreach (var wrapped in TextWidth.Wrap(text, Math.Max(8, width - label.Length)))
                {
                    var (spans, matched) = TranscriptFormatter.Highlight(wrapped, Style.Plain, terms);
                    var all = new List<StyledSpan> { new(first ? label : new string(' ', label.Length), Theme.Dim) };
                    all.AddRange(spans);
                    lines.Add(new TranscriptLine(all, snippet.TurnIndex, false, matched));
                    first = false;
                }
            }

            lines.Add(new TranscriptLine([new StyledSpan(new string('\u2500', Math.Max(1, width)), Theme.Border)], -1, false, false));
        }

        if (transcriptLoading)
        {
            lines.Add(new TranscriptLine([new StyledSpan($"{Spinner} loading transcript\u2026", Theme.Dim)], -1, false, false));
        }
        else if (transcriptError is not null)
        {
            foreach (var wrapped in TextWidth.Wrap(transcriptError, width))
            {
                lines.Add(new TranscriptLine([new StyledSpan(wrapped, Theme.Error)], -1, false, false));
            }
        }
        else if (transcript is not null && loadedRef == row.Session.Ref)
        {
            lines.AddRange(FormattedLines(width));
        }

        return lines;
    }

    // ---- reader ------------------------------------------------------------------------------

    private void DrawReader()
    {
        var row = Selected;
        var rect = new Rect(0, 0, buffer.Width, buffer.Height - 1);
        var title = row is null ? "Reader" : TextUtilities.Flatten(row.Session.Title);
        buffer.DrawBox(rect, Theme.BorderFocused, title, Theme.TitleFocused);

        var inner = rect.Inset();
        if (inner.IsEmpty || row is null)
        {
            return;
        }

        if (transcriptLoading)
        {
            buffer.WriteIn(inner, 0, 1, $"{Spinner} loading transcript\u2026", Theme.Dim);
            return;
        }

        if (transcriptError is not null)
        {
            buffer.WriteIn(inner, 0, 1, transcriptError, Theme.Error);
            return;
        }

        var lines = FormattedLines(inner.Width - 3);
        var total = lines.Count;
        readerScroll = Math.Clamp(readerScroll, 0, Math.Max(0, total - inner.Height));
        var content = new Rect(inner.Left, inner.Top, inner.Width - 1, inner.Height);

        for (var slot = 0; slot < inner.Height; slot++)
        {
            var index = readerScroll + slot;
            if (index >= total)
            {
                break;
            }

            buffer.WriteSpans(content, slot, 1, lines[index].Spans);
        }

        if (total > inner.Height)
        {
            buffer.DrawScrollbar(new Rect(inner.Right - 1, inner.Top, 1, inner.Height), total, inner.Height, readerScroll, Theme.Border);
        }

        if (transcriptLoadingMore)
        {
            buffer.Write(rect.Bottom - 1, rect.Left + 2, $" {Spinner} loading more\u2026 ", Theme.Dim);
        }

        var position = total == 0 ? "0%" : $"{Math.Min(100, (readerScroll + inner.Height) * 100 / Math.Max(1, total))}%";
        var footer = $" {position}  turn {CurrentReaderTurn(lines)}/{transcript?.TotalTurns ?? 0} ";
        buffer.Write(rect.Bottom - 1, Math.Max(rect.Left + 1, rect.Right - TextWidth.Of(footer) - 2), footer, Theme.Dim);
    }

    private int CurrentReaderTurn(IReadOnlyList<TranscriptLine> lines)
    {
        for (var index = Math.Min(readerScroll, lines.Count - 1); index >= 0; index--)
        {
            if (lines[index].TurnIndex >= 0)
            {
                return lines[index].TurnIndex + 1;
            }
        }

        return 0;
    }

    // ---- status bar --------------------------------------------------------------------------

    private void DrawStatusBar()
    {
        var row = buffer.Height - 1;
        var width = buffer.Width;
        buffer.Fill(new Rect(0, row, width, 1), Style.Plain);

        var hints = HintsFor(mode, width);
        var hintsWidth = hints.Sum(span => TextWidth.Of(span.Text));
        var leftBudget = Math.Max(0, width - hintsWidth - 2);

        var left = new List<StyledSpan>();
        if (status is not null)
        {
            left.Add(new StyledSpan(TextWidth.Clip(status, leftBudget), statusIsError ? Theme.Error : Theme.Ok));
        }
        else if (refreshing)
        {
            left.Add(new StyledSpan(TextWidth.Clip($"{Spinner} {refreshMessage ?? "refreshing index\u2026"}", leftBudget), Theme.Chip));
        }
        else
        {
            var summary = lastQueryWasSearch
                ? $"{rows.Count} match(es)"
                : $"{rows.Count} session(s)";
            left.Add(new StyledSpan(summary, Theme.Dim));

            if (queryLoading)
            {
                left.Add(new StyledSpan($" {Spinner}", Theme.Chip));
            }
        }

        // Filters stay visible whatever else the bar is saying; a hidden filter is how "where did
        // half my sessions go" happens.
        if (sourceFilter is not null)
        {
            left.Add(new StyledSpan($"  source:{sourceFilter}", Theme.Chip));
        }

        if (here)
        {
            left.Add(new StyledSpan("  here", Theme.Chip));
        }

        if (includeArchived)
        {
            left.Add(new StyledSpan("  archived", Theme.Chip));
        }

        if (options.Pick != PickKind.None)
        {
            left.Add(new StyledSpan($"  pick:{options.Pick.ToString().ToLowerInvariant()}", Theme.Chip));
        }

        if (status is null && !refreshing && backend.PendingSources.Count > 0)
        {
            left.Add(new StyledSpan($"  index behind: {string.Join(", ", backend.PendingSources)} (Ctrl+R)", Theme.Chip));
        }

        var statusRect = new Rect(0, row, leftBudget, 1);
        buffer.WriteSpans(statusRect, 0, 1, left);

        var hintRect = new Rect(Math.Max(0, width - hintsWidth - 1), row, hintsWidth + 1, 1);
        buffer.WriteSpans(hintRect, 0, 0, hints);
    }

    private List<StyledSpan> HintsFor(Mode current, int width)
    {
        IEnumerable<(string Key, string Label)> pairs = current switch
        {
            Mode.Reader =>
            [
                ("j/k", "scroll"), ("n/N", "match"), ("]/[", "turn"), ("x", "tools"), ("z", "reasoning"),
                ("r", "resume"), ("c", "copy cmd"), ("q", "back"), ("?", "help")
            ],
            Mode.Confirm => [("enter", confirm?.AcceptLabel ?? "ok"), ("c", "copy"), ("esc", "cancel")],
            Mode.Input => [("enter", "apply"), ("esc", "cancel")],
            Mode.Help or Mode.Info => [("esc", "close")],
            _ when searchFocused =>
            [
                ("type", "to search"), ("\u2191/\u2193", "select"), ("enter", options.Pick == PickKind.None ? "open" : "pick"),
                ("esc", "to list"), ("ctrl+u", "clear"), ("?", "help")
            ],
            _ when focus == Pane.Preview => [("j/k", "scroll"), ("tab", "to list"), ("enter", "reader"), ("?", "help")],
            _ =>
            [
                ("j/k", "move"), ("enter", options.Pick == PickKind.None ? "open" : "pick"), ("/", "search"), ("r", "resume"),
                ("c", "copy cmd"), ("y", "uri"), ("d", "dir"), ("o", "folder"), ("t", "tag"), ("s", "source"), ("?", "help"), ("q", "quit")
            ]
        };

        var spans = new List<StyledSpan>();
        var used = 0;

        // Hints get at most three fifths of the bar; the rest belongs to counts, filters, and messages.
        var budget = Math.Max(10, width * 3 / 5);

        foreach (var (key, label) in pairs)
        {
            var piece = TextWidth.Of(key) + 1 + TextWidth.Of(label) + 2;
            if (used + piece > budget && spans.Count > 0)
            {
                break;
            }

            spans.Add(new StyledSpan(key, Theme.HintKey));
            spans.Add(new StyledSpan(" " + label + "  ", Theme.Hint));
            used += piece;
        }

        return spans;
    }

    // ---- overlays ----------------------------------------------------------------------------

    private Rect CenteredCard(int width, int height)
    {
        width = Math.Min(width, buffer.Width - 2);
        height = Math.Min(height, buffer.Height - 2);
        var left = Math.Max(0, (buffer.Width - width) / 2);
        var top = Math.Max(0, (buffer.Height - height) / 2);
        return new Rect(left, top, width, height);
    }

    private void DrawCard(Rect rect, string title)
    {
        buffer.Fill(rect, Theme.Overlay);
        buffer.DrawBox(rect, Theme.OverlayBorder, title, Theme.OverlayBorder.Bold());
    }

    private void DrawHelp()
    {
        (string Key, string Text)[] navigation =
        [
            ("/", "focus the search box; typing searches as you go"),
            ("esc", "leave the search box / close an overlay"),
            ("j k \u2191 \u2193", "move selection"),
            ("g G", "first / last"),
            ("pgup pgdn ctrl+u ctrl+d", "page"),
            ("enter l \u2192", options.Pick == PickKind.None ? "open the reader" : "pick this session"),
            ("tab", "switch between list and preview"),
            ("J K", "scroll the preview without leaving the list"),
            ("q", "quit (or back from the reader)")
        ];

        (string Key, string Text)[] actions =
        [
            ("r", "resume in its harness (opens a confirmation)"),
            ("R", "resume as a fork, where supported"),
            ("c", "copy the resume command"),
            ("y / Y", "copy the retrace:// URI / the native id"),
            ("d", "copy the working directory"),
            ("o", "open the working directory in the file manager"),
            ("e", "export the transcript as Markdown into the current directory"),
            ("t", "add tags (prefix with - to remove)"),
            ("i", "session details"),
            ("s w a", "cycle source filter / toggle here / toggle archived"),
            ("m", "cycle search mode: hybrid, lexical, semantic"),
            ("x z", "toggle tool output / reasoning in the reader"),
            ("ctrl+r", "refresh the index (keyword-only) and re-run the query"),
            ("n N ] [", "reader: next/previous match, next/previous turn")
        ];

        var keyWidth = Math.Max(navigation.Max(item => item.Key.Length), actions.Max(item => item.Key.Length)) + 2;
        var card = CenteredCard(92, navigation.Length + actions.Length + 7);
        DrawCard(card, "Keys");

        var inner = card.Inset();
        var row = 0;
        buffer.WriteIn(inner, row++, 1, "Navigation", Theme.Chip.Bold());
        foreach (var (key, text) in navigation)
        {
            buffer.WriteIn(inner, row, 1, key.PadRight(keyWidth), Theme.HintKey);
            buffer.WriteIn(inner, row++, 1 + keyWidth, text, Style.Plain);
        }

        row++;
        buffer.WriteIn(inner, row++, 1, "Actions", Theme.Chip.Bold());
        foreach (var (key, text) in actions)
        {
            buffer.WriteIn(inner, row, 1, key.PadRight(keyWidth), Theme.HintKey);
            buffer.WriteIn(inner, row++, 1 + keyWidth, text, Style.Plain);
        }

        buffer.WriteIn(inner, inner.Height - 1, 1, "press any key to close", Theme.Dim);
    }

    private void DrawInfo()
    {
        var row = Selected;
        if (row is null)
        {
            return;
        }

        var session = row.Session;
        var fields = new List<(string Label, string Value)>
        {
            ("title", session.Title),
            ("uri", session.Ref.Uri),
            ("source", session.Ref.SourceId),
            ("id", session.Ref.NativeId),
            ("directory", session.Workspace?.Path ?? "(not recorded)"),
            ("repository", session.Workspace?.Repository ?? "-"),
            ("branch", session.Workspace?.Branch ?? "-"),
            ("agent", session.Agent ?? "-"),
            ("models", session.Models.Count > 0 ? string.Join(", ", session.Models) : "-"),
            ("created", session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("updated", session.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("messages", session.Stats.MessageCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"),
            ("tool calls", session.Stats.ToolCallCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"),
            ("tokens", session.Stats.TotalTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"),
            ("cost", session.Stats.Cost is { } cost ? cost.ToString("C", CultureInfo.InvariantCulture) : "-"),
            ("files changed", session.Stats.FilesChanged?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"),
            ("tags", session.Tags.Count > 0 ? string.Join(" ", session.Tags.Select(tag => "#" + tag)) : "-"),
            ("archived", session.IsArchived ? "yes" : "no")
        };

        if (transcript is not null && loadedRef == session.Ref && transcript.Files.Count > 0)
        {
            fields.Add(("files", string.Join(", ", transcript.Files.Take(12).Select(file => file.Path)) + (transcript.Files.Count > 12 ? $" (+{transcript.Files.Count - 12})" : string.Empty)));
        }

        var labelWidth = fields.Max(field => field.Label.Length) + 2;
        var card = CenteredCard(100, fields.Count + 5);
        DrawCard(card, "Session");
        var inner = card.Inset();
        var line = 0;

        foreach (var (label, value) in fields)
        {
            if (line >= inner.Height - 1)
            {
                break;
            }

            buffer.WriteIn(inner, line, 1, label.PadRight(labelWidth), Theme.Dim);
            buffer.WriteIn(inner, line++, 1 + labelWidth, TextWidth.Clip(value, inner.Width - labelWidth - 2), Style.Plain);
        }

        buffer.WriteIn(inner, inner.Height - 1, 1, "esc to close", Theme.Dim);
    }

    private void DrawConfirm(ConfirmState state)
    {
        var width = Math.Min(buffer.Width - 4, Math.Max(50, state.Lines.Max(line => line.Sum(span => TextWidth.Of(span.Text))) + 4));
        var card = CenteredCard(width, state.Lines.Count + 5);
        DrawCard(card, state.Title);
        var inner = card.Inset();

        for (var index = 0; index < state.Lines.Count && index < inner.Height - 2; index++)
        {
            buffer.WriteSpans(inner, index, 1, state.Lines[index]);
        }

        var footer = new List<StyledSpan>
        {
            new("enter", Theme.HintKey), new($" {state.AcceptLabel}   ", Theme.Hint)
        };

        if (state.Copy is not null)
        {
            footer.Add(new StyledSpan("c", Theme.HintKey));
            footer.Add(new StyledSpan(" copy   ", Theme.Hint));
        }

        footer.Add(new StyledSpan("esc", Theme.HintKey));
        footer.Add(new StyledSpan(" cancel", Theme.Hint));
        buffer.WriteSpans(inner, inner.Height - 1, 1, footer);
    }

    private void DrawInput(InputState state)
    {
        var card = CenteredCard(Math.Min(buffer.Width - 4, 80), 3);
        DrawCard(card, state.Prompt);
        var inner = card.Inset();

        if (state.Text.Length == 0)
        {
            buffer.WriteIn(inner, 0, 1, state.Placeholder, Theme.Dim);
        }
        else
        {
            buffer.WriteIn(inner, 0, 1, state.Text, Style.Plain);
        }

        var caretColumn = 1 + TextWidth.Of(state.Text);
        if (caretColumn < inner.Width)
        {
            buffer.WriteIn(inner, 0, caretColumn, " ", Theme.Caret);
        }
    }

    // ---- small helpers -----------------------------------------------------------------------

    private string FormatDate(DateTimeOffset value, DateTimeOffset now) =>
        options.RelativeDates
            ? TextUtilities.ToRelativeTime(value, now)
            : value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static (string Label, Style Style) SourceBadge(string sourceId) => sourceId switch
    {
        "opencode" => ("opencode", Style.Fg(TermColor.Magenta)),
        "copilot-cli" => ("copilot", Style.Fg(TermColor.Blue)),
        "copilot-vscode" => ("vscode", Style.Fg(TermColor.Cyan)),
        _ => (sourceId, Style.Fg(TermColor.White))
    };

    private static string SearchModeLabel(SearchMode value) => value switch
    {
        SearchMode.Lexical => "lexical",
        SearchMode.Semantic => "semantic",
        _ => "hybrid"
    };

    private static string DetailLine(SessionSummary session)
    {
        var parts = new List<string>();

        if (session.Workspace?.Label is { Length: > 0 } label && label != "(unknown)")
        {
            parts.Add(label);
        }

        if (session.Agent is { Length: > 0 } agent)
        {
            parts.Add(agent);
        }

        if (session.Models.Count > 0)
        {
            parts.Add(session.Models[0]);
        }

        if (session.Stats.MessageCount is { } messages and > 0)
        {
            parts.Add($"{messages} msg");
        }

        if (session.Tags.Count > 0)
        {
            parts.Add("#" + string.Join(" #", session.Tags));
        }

        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(session.Preview))
        {
            parts.Add(TextUtilities.Flatten(session.Preview));
        }

        return string.Join("  \u00b7  ", parts);
    }
}
