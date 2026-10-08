using System.Globalization;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Text;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

public sealed partial class SessionBrowser
{
    private static readonly string[] SpinnerFrames = ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

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

    private string Spinner => SpinnerFrames[(int)(clock.ElapsedMilliseconds / SpinnerIntervalMilliseconds % SpinnerFrames.Length)];

    private void Draw()
    {
        buffer.Clear();

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
        var border = focused ? Theme.BorderFocused : Theme.Border;

        var title = new List<StyledSpan>
        {
            new("\u25c6", Theme.BrandMark),
            new(" retrace", Theme.BrandName)
        };

        buffer.DrawBox(rect, border, title, HeaderInfo(), rounded: true);

        var inner = rect.Inset();
        var column = 1;
        column += buffer.WriteIn(inner, 0, column, "\u276f ", focused ? Theme.Prompt : Theme.PromptIdle);

        var trailing = TrailingSearchSpans();
        var trailingWidth = trailing.Sum(span => TextWidth.Of(span.Text)) + (trailing.Count > 0 ? 2 : 0);
        var available = Math.Max(1, inner.Width - column - trailingWidth - 1);

        if (query.Length == 0 && !focused)
        {
            buffer.WriteIn(inner, 0, column, "type to search \u00b7 empty shows recent sessions", Theme.Placeholder);
        }
        else if (query.Length == 0)
        {
            buffer.Write(inner.Top, inner.Left + column, " ", Theme.Caret, 1);
            buffer.WriteIn(inner, 0, column + 2, "search across every harness", Theme.Placeholder);
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
            buffer.Write(inner.Top, inner.Left + column, visible, Theme.Body, available);

            if (focused)
            {
                var caretColumn = inner.Left + column + TextWidth.Of(query.AsSpan(start, Math.Clamp(caret, start, query.Length) - start));
                var under = caret < query.Length ? query[caret].ToString() : " ";
                buffer.Write(inner.Top, caretColumn, under, Theme.Caret, 1);
            }
        }

        if (trailing.Count > 0)
        {
            buffer.WriteSpansRight(inner, 0, trailing, rightPadding: 1);
        }
    }

    /// <summary>What sits at the right end of the input: a spinner while searching, otherwise nothing.</summary>
    private List<StyledSpan> TrailingSearchSpans()
    {
        if (queryLoading)
        {
            return [new StyledSpan(Spinner, Theme.Spinner), new StyledSpan(query.Trim().Length > 0 ? " searching" : " loading", Theme.Dim)];
        }

        return [];
    }

    /// <summary>Right side of the header border: search mode, index size, version.</summary>
    private List<StyledSpan> HeaderInfo()
    {
        var spans = new List<StyledSpan>();
        void Separator()
        {
            if (spans.Count > 0)
            {
                spans.Add(new StyledSpan(" \u00b7 ", Theme.Border));
            }
        }

        var semanticAvailable = indexInfo?.SemanticAvailable ?? true;
        var modeLabel = searchMode switch
        {
            SearchMode.Lexical => "keyword",
            SearchMode.Semantic => semanticAvailable ? "semantic" : "semantic (no model)",
            _ => semanticAvailable ? "hybrid" : "keyword"
        };

        spans.Add(new StyledSpan(modeLabel, Theme.PaneCount));

        if (indexInfo is { Exists: true } info)
        {
            Separator();
            spans.Add(new StyledSpan($"{info.SessionCount:N0} indexed", Theme.PaneCount));
        }

        if (!string.IsNullOrEmpty(options.Version))
        {
            Separator();
            spans.Add(new StyledSpan("v" + options.Version, Style.Fg(Theme.Faint)));
        }

        return spans;
    }

    private void DrawList(Rect rect, bool focused)
    {
        var count = rows.Count;
        var title = new List<StyledSpan>
        {
            new(lastQueryWasSearch ? "Matches" : "Sessions", focused ? Theme.PaneTitleFocused : Theme.PaneTitle),
            new($" \u00b7 {count:N0}", Theme.PaneCount)
        };

        if (queryLoading)
        {
            title.Add(new StyledSpan(" " + Spinner, Theme.Spinner));
        }

        var rightTitle = new List<StyledSpan>();
        if (lastQueryWasSearch && !queryLoading && hasLoadedOnce && lastQueryDuration > TimeSpan.Zero)
        {
            rightTitle.Add(new StyledSpan($"{lastQueryDuration.TotalMilliseconds:N0} ms", Style.Fg(Theme.Faint)));
        }

        buffer.DrawBox(rect, focused ? Theme.BorderFocused : Theme.Border, title, rightTitle);
        var inner = rect.Inset();
        if (inner.IsEmpty)
        {
            return;
        }

        if (queryError is not null)
        {
            var row = 1;
            foreach (var line in TextWidth.Wrap(queryError, Math.Max(4, inner.Width - 4)))
            {
                buffer.WriteIn(inner, row++, 2, line, Theme.Error);
            }

            return;
        }

        if (count == 0)
        {
            DrawEmptyList(inner);
            return;
        }

        var page = Math.Max(1, inner.Height / 2);
        EnsureSelectionVisible();
        var now = DateTimeOffset.UtcNow;
        var hasScrollbar = count > page;
        var contentWidth = inner.Width - (hasScrollbar ? 1 : 0);
        var content = new Rect(inner.Left, inner.Top, contentWidth, inner.Height);
        var stale = queryLoading && !string.Equals(query.Trim(), lastExecutedQuery, StringComparison.Ordinal);

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

            DrawListRow(content, top, row, now, isSelected);

            if (isSelected)
            {
                var background = focused ? Theme.Selection : Theme.SelectionInactive;
                for (var line = 0; line < 2 && top + line < content.Height; line++)
                {
                    buffer.RestyleRow(content, top + line, style => OnSelection(style, background));
                }

                buffer.WriteIn(content, top, 0, "\u258e", Theme.SelectionBar.WithBg(background));
                if (top + 1 < content.Height)
                {
                    buffer.WriteIn(content, top + 1, 0, "\u258e", Theme.SelectionBar.WithBg(background));
                }
            }
            else if (stale)
            {
                // Results for a query the user has already typed past: still useful to look at,
                // visibly not the answer yet.
                for (var line = 0; line < 2 && top + line < content.Height; line++)
                {
                    buffer.RestyleRow(content, top + line, style => style.Background == Theme.Amber ? style : style.Dim());
                }
            }
        }

        if (hasScrollbar)
        {
            buffer.DrawScrollbar(new Rect(inner.Right - 1, inner.Top, 1, inner.Height), count, page, listScroll, Theme.ScrollTrack, Theme.ScrollThumb);
        }
    }

    private void DrawEmptyList(Rect inner)
    {
        var centre = Math.Max(0, inner.Height / 2 - 1);

        if (!hasLoadedOnce || queryLoading)
        {
            var message = query.Trim().Length > 0 ? " searching\u2026" : " loading sessions\u2026";
            var width = 1 + TextWidth.Of(message);
            var left = Math.Max(1, (inner.Width - width) / 2);
            buffer.WriteSpans(inner, centre, left, [new StyledSpan(Spinner, Theme.Spinner), new StyledSpan(message, Theme.Dim)]);
            return;
        }

        if (query.Trim().Length > 0)
        {
            var headline = TextWidth.Clip($"No matches for \u201c{query.Trim()}\u201d", inner.Width - 4);
            buffer.WriteIn(inner, centre, Math.Max(1, (inner.Width - TextWidth.Of(headline)) / 2), headline, Theme.Secondary);

            var nextMode = searchMode switch
            {
                SearchMode.Hybrid => "keyword",
                SearchMode.Lexical => "semantic",
                _ => "hybrid"
            };
            var hint = TextWidth.Clip($"try fewer or different words \u00b7 m switches to {nextMode}", inner.Width - 4);
            buffer.WriteIn(inner, centre + 1, Math.Max(1, (inner.Width - TextWidth.Of(hint)) / 2), hint, Theme.Dim);
            return;
        }

        var empty = sourceFilter is not null || here ? "No sessions match the current filters." : "No sessions yet.";
        buffer.WriteIn(inner, centre, Math.Max(1, (inner.Width - TextWidth.Of(empty)) / 2), empty, Theme.Secondary);
    }

    /// <summary>Restyles a cell for the selection bar: lift muted text so it stays legible on the darker band.</summary>
    private static Style OnSelection(Style style, Color background)
    {
        if (style.Background == Theme.Amber)
        {
            return style;
        }

        var foreground = style.Foreground == Theme.Muted || style.Foreground == Theme.Faint
            ? Theme.TextSecondary
            : style.Foreground;

        return new Style(foreground, background, style.Attributes & ~TermAttr.Dim);
    }

    private void DrawListRow(Rect content, int top, Row row, DateTimeOffset now, bool isSelected)
    {
        var session = row.Session;
        var date = TextWidth.Fit(FormatDate(session.UpdatedAt, now), 8);
        var (sourceLabel, sourceColor) = Theme.SourceBadge(session.Ref.SourceId);

        var column = 2;
        column += buffer.WriteIn(content, top, column, date, Theme.Date);
        column += buffer.WriteIn(content, top, column, "  ", Style.Plain);
        column += buffer.WriteIn(content, top, column, "\u25cf ", Style.Fg(sourceColor));
        column += buffer.WriteIn(content, top, column, TextWidth.Fit(sourceLabel, 8), Style.Fg(sourceColor));
        column += buffer.WriteIn(content, top, column, " ", Style.Plain);

        var title = TextUtilities.Flatten(session.Title);
        if (string.IsNullOrEmpty(title))
        {
            title = "(untitled)";
        }

        var titleWidth = Math.Max(0, content.Width - column - 1);
        var terms = TranscriptFormatter.TermsOf(lastExecutedQuery);
        var titleStyle = isSelected ? Theme.RowTitle.Bold() : Theme.RowTitle;
        var (titleSpans, _) = TranscriptFormatter.Highlight(TextWidth.Clip(title, titleWidth), titleStyle, terms);
        buffer.WriteSpans(content, top, column, titleSpans);

        if (top + 1 >= content.Height)
        {
            return;
        }

        var detailColumn = 2 + 8 + 2;
        var detailWidth = Math.Max(0, content.Width - detailColumn - 1);

        if (row.Hit is { Snippets.Count: > 0 } hit)
        {
            var snippet = hit.Snippets[0];
            var text = TextUtilities.Flatten(snippet.Highlighted ?? snippet.Text)
                .Replace("<<", string.Empty, StringComparison.Ordinal)
                .Replace(">>", string.Empty, StringComparison.Ordinal);
            var label = TextUtilities.RoleLabel(snippet.Role) + "  ";
            var (spans, _) = TranscriptFormatter.Highlight(TextWidth.Clip(text, Math.Max(0, detailWidth - label.Length)), Theme.Snippet, terms);
            var all = new List<StyledSpan> { new(label, Theme.SnippetRole) };
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
        var title = new List<StyledSpan> { new("Preview", focused ? Theme.PaneTitleFocused : Theme.PaneTitle) };
        var rightTitle = row is null ? [] : new List<StyledSpan> { new(row.Session.Ref.ShortForm, Theme.PaneCount) };

        buffer.DrawBox(rect, focused ? Theme.BorderFocused : Theme.Border, title, rightTitle);

        var inner = rect.Inset();
        if (inner.IsEmpty || row is null)
        {
            return;
        }

        var lines = BuildPreviewLines(row, inner.Width - 3);
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
            buffer.DrawScrollbar(new Rect(inner.Right - 1, inner.Top, 1, inner.Height), total, inner.Height, previewScroll, Theme.ScrollTrack, Theme.ScrollThumb);
        }
    }

    /// <summary>Snippets first, so the matched passage is the first thing visible, then the transcript.</summary>
    private List<TranscriptLine> BuildPreviewLines(Row row, int width)
    {
        var lines = new List<TranscriptLine>();
        var terms = TranscriptFormatter.TermsOf(lastExecutedQuery);

        if (row.Hit is { Snippets.Count: > 0 } hit)
        {
            lines.Add(new TranscriptLine(
                [new StyledSpan("Matches", Theme.Warn.Bold()), new StyledSpan($"  {hit.Snippets.Count}", Theme.Dim)],
                -1,
                false,
                false));

            foreach (var snippet in hit.Snippets)
            {
                var text = TextUtilities.Flatten(snippet.Highlighted ?? snippet.Text)
                    .Replace("<<", string.Empty, StringComparison.Ordinal)
                    .Replace(">>", string.Empty, StringComparison.Ordinal);
                var label = $"{TextUtilities.RoleLabel(snippet.Role)} #{snippet.TurnIndex}  ";
                var first = true;
                foreach (var wrapped in TextWidth.Wrap(text, Math.Max(8, width - label.Length)))
                {
                    var (spans, matched) = TranscriptFormatter.Highlight(wrapped, Theme.Snippet, terms);
                    var all = new List<StyledSpan> { new(first ? label : new string(' ', label.Length), Theme.SnippetRole) };
                    all.AddRange(spans);
                    lines.Add(new TranscriptLine(all, snippet.TurnIndex, false, matched));
                    first = false;
                }
            }

            lines.Add(new TranscriptLine([new StyledSpan(new string('\u2500', Math.Max(1, width)), Theme.Border)], -1, false, false));
        }

        if (transcriptLoading)
        {
            lines.Add(new TranscriptLine([new StyledSpan(Spinner, Theme.Spinner), new StyledSpan(" loading transcript\u2026", Theme.Dim)], -1, false, false));
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
        var inner = rect.Inset();
        var lines = row is null || transcriptLoading || transcriptError is not null ? [] : FormattedLines(Math.Max(10, inner.Width - 3));
        var total = lines.Count;
        readerScroll = Math.Clamp(readerScroll, 0, Math.Max(0, total - inner.Height));

        var title = new List<StyledSpan>
        {
            new("\u25c6 ", Theme.BrandMark),
            new(row is null ? "Reader" : TextUtilities.Flatten(row.Session.Title), Theme.PaneTitleFocused)
        };

        var rightTitle = new List<StyledSpan>();
        if (row is not null)
        {
            rightTitle.Add(new StyledSpan(row.Session.Ref.ShortForm, Theme.PaneCount));
            if (total > 0)
            {
                var percent = Math.Min(100, (readerScroll + inner.Height) * 100 / Math.Max(1, total));
                rightTitle.Add(new StyledSpan($" \u00b7 {percent}%", Theme.PaneCount));
                rightTitle.Add(new StyledSpan($" \u00b7 turn {CurrentReaderTurn(lines)}/{transcript?.TotalTurns ?? 0}", Theme.PaneCount));
            }
        }

        buffer.DrawBox(rect, Theme.BorderFocused, title, rightTitle);

        if (inner.IsEmpty || row is null)
        {
            return;
        }

        if (transcriptLoading)
        {
            buffer.WriteSpans(inner, 1, 2, [new StyledSpan(Spinner, Theme.Spinner), new StyledSpan(" loading transcript\u2026", Theme.Dim)]);
            return;
        }

        if (transcriptError is not null)
        {
            buffer.WriteIn(inner, 1, 2, transcriptError, Theme.Error);
            return;
        }

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
            buffer.DrawScrollbar(new Rect(inner.Right - 1, inner.Top, 1, inner.Height), total, inner.Height, readerScroll, Theme.ScrollTrack, Theme.ScrollThumb);
        }

        if (transcriptLoadingMore)
        {
            buffer.WriteSpans(new Rect(rect.Left + 2, rect.Bottom - 1, rect.Width - 4, 1), 0, 0, [new StyledSpan(" " + Spinner, Theme.Spinner), new StyledSpan(" loading more\u2026 ", Theme.Dim)]);
        }
    }

    private int CurrentReaderTurn(IReadOnlyList<TranscriptLine> lines)
    {
        if (lines.Count == 0)
        {
            return 0;
        }

        // The turn whose text is at the top of the viewport; while the header is showing, the
        // first turn below it.
        for (var index = Math.Min(readerScroll, lines.Count - 1); index >= 0; index--)
        {
            if (lines[index].TurnIndex >= 0)
            {
                return lines[index].TurnIndex + 1;
            }
        }

        for (var index = readerScroll; index < lines.Count; index++)
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
            switch (statusKind)
            {
                case StatusKind.Ok:
                    left.Add(new StyledSpan("\u2713 ", Theme.Ok));
                    left.Add(new StyledSpan(TextWidth.Clip(status, leftBudget - 2), Theme.Ok));
                    break;

                case StatusKind.Error:
                    left.Add(new StyledSpan("\u2717 ", Theme.Error));
                    left.Add(new StyledSpan(TextWidth.Clip(status, leftBudget - 2), Theme.Error));
                    break;

                case StatusKind.Busy:
                    left.Add(new StyledSpan(Spinner + " ", Theme.Spinner));
                    left.Add(new StyledSpan(TextWidth.Clip(status, leftBudget - 2), Theme.Dim));
                    break;

                default:
                    left.Add(new StyledSpan(TextWidth.Clip(status, leftBudget), Theme.Secondary));
                    break;
            }
        }
        else if (activity is not null)
        {
            left.Add(new StyledSpan(Spinner, Theme.Spinner));
            left.Add(new StyledSpan(" " + TextWidth.Clip(activity, leftBudget - 2), Theme.Dim));
        }
        else
        {
            var summary = lastQueryWasSearch
                ? $"{rows.Count:N0} match{(rows.Count == 1 ? string.Empty : "es")}"
                : $"{rows.Count:N0} session{(rows.Count == 1 ? string.Empty : "s")}";
            left.Add(new StyledSpan(summary, Theme.Dim));
        }

        // Filters stay visible whatever else the bar is saying; a hidden filter is how "where did
        // half my sessions go" happens.
        void Chip(string text, Style style)
        {
            left.Add(new StyledSpan("  ", Style.Plain));
            left.Add(new StyledSpan($" {text} ", style));
        }

        if (sourceFilter is not null)
        {
            Chip($"source {sourceFilter}", Theme.ChipAccent);
        }

        if (here)
        {
            Chip("here", Theme.ChipAccent);
        }

        if (includeArchived)
        {
            Chip("archived", Theme.Chip);
        }

        if (options.Pick != PickKind.None)
        {
            Chip($"pick {options.Pick.ToString().ToLowerInvariant()}", Theme.Chip);
        }

        if (status is null && activity is null && backend.PendingSources.Count > 0)
        {
            Chip($"index behind: {string.Join(", ", backend.PendingSources)} \u00b7 ctrl+r", Theme.ChipWarn);
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
            Mode.Confirm => [("\u23ce", confirm?.AcceptLabel ?? "ok"), ("c", "copy"), ("esc", "cancel")],
            Mode.Input => [("\u23ce", "apply"), ("esc", "cancel")],
            Mode.Help or Mode.Info => [("esc", "close")],
            _ when searchFocused =>
            [
                ("\u2191\u2193", "select"), ("\u23ce", options.Pick == PickKind.None ? "open" : "pick"),
                ("esc", "to list"), ("ctrl+u", "clear"), ("?", "help")
            ],
            _ when focus == Pane.Preview => [("j/k", "scroll"), ("tab", "to list"), ("\u23ce", "reader"), ("?", "help")],
            _ =>
            [
                ("j/k", "move"), ("\u23ce", options.Pick == PickKind.None ? "open" : "pick"), ("/", "search"), ("r", "resume"),
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
            spans.Add(new StyledSpan(" " + label + "  ", Theme.HintLabel));
            used += piece;
        }

        return spans;
    }

    // ---- overlays ----------------------------------------------------------------------------

    private Rect CenteredCard(int width, int height)
    {
        width = Math.Min(width, buffer.Width - 4);
        height = Math.Min(height, buffer.Height - 3);
        var left = Math.Max(0, (buffer.Width - width) / 2);
        var top = Math.Max(0, (buffer.Height - 1 - height) / 2);
        return new Rect(left, top, width, height);
    }

    private void DrawCard(Rect rect, string title, IReadOnlyList<StyledSpan>? rightTitle = null)
    {
        buffer.DrawShadow(rect, Theme.Shadow);
        buffer.Fill(rect, Theme.Overlay);
        buffer.DrawBox(rect, Theme.OverlayBorder, [new StyledSpan(title, Theme.OverlayTitle)], rightTitle);
    }

    /// <summary>Gives a span the card's background so content never punches holes in the surface.</summary>
    private static Style OnSurface(Style style) => new(
        style.Foreground.IsDefault ? Theme.TextBright : style.Foreground,
        style.Background.IsDefault ? Theme.Surface : style.Background,
        style.Attributes);

    private void WriteKeyCap(Rect inner, int row, int column, string key, int width)
    {
        var cap = " " + key + " ";
        buffer.WriteIn(inner, row, column, cap, Theme.OverlayKeyCap);
        var padding = width - TextWidth.Of(cap);
        if (padding > 0)
        {
            buffer.WriteIn(inner, row, column + TextWidth.Of(cap), new string(' ', padding), Theme.Overlay);
        }
    }

    private void DrawHelp()
    {
        (string Key, string Text)[] navigation =
        [
            ("/", "focus the search box; results update as you type"),
            ("esc", "leave the search box, or close an overlay"),
            ("j k \u2191 \u2193", "move the selection"),
            ("g G", "first / last"),
            ("pgup pgdn", "page (also ctrl+u / ctrl+d)"),
            ("\u23ce l \u2192", options.Pick == PickKind.None ? "open the reader" : "pick this session"),
            ("tab", "switch between the list and the preview"),
            ("J K", "scroll the preview without leaving the list"),
            ("q", "quit, or back from the reader")
        ];

        (string Key, string Text)[] actions =
        [
            ("r", "resume in its harness, after a confirmation"),
            ("R", "resume as a fork, where supported"),
            ("c", "copy the resume command"),
            ("y Y", "copy the retrace:// URI / the native id"),
            ("d", "copy the working directory"),
            ("o", "open the working directory in the file manager"),
            ("e", "export the transcript as Markdown into the current directory"),
            ("t", "add tags; prefix with - to remove"),
            ("i", "session details"),
            ("s w a", "cycle source filter / toggle here / toggle archived"),
            ("m", "cycle search mode: hybrid, keyword, semantic"),
            ("x z", "show or hide tool output / reasoning"),
            ("ctrl+r", "refresh the index (keyword-only) and re-run the query"),
            ("n N ] [", "reader: next / previous match, next / previous turn")
        ];

        var keyWidth = Math.Max(navigation.Max(item => item.Key.Length), actions.Max(item => item.Key.Length)) + 3;
        var card = CenteredCard(96, navigation.Length + actions.Length + 8);
        DrawCard(card, "Keys", [new StyledSpan("esc closes", Theme.OverlayMuted)]);

        var inner = card.Inset();
        var row = 1;
        buffer.WriteIn(inner, row++, 2, "Navigation", Theme.OverlaySection);
        foreach (var (key, text) in navigation)
        {
            WriteKeyCap(inner, row, 2, key, keyWidth);
            buffer.WriteIn(inner, row++, 2 + keyWidth + 1, text, Theme.Overlay);
        }

        row++;
        buffer.WriteIn(inner, row++, 2, "Actions", Theme.OverlaySection);
        foreach (var (key, text) in actions)
        {
            WriteKeyCap(inner, row, 2, key, keyWidth);
            buffer.WriteIn(inner, row++, 2 + keyWidth + 1, text, Theme.Overlay);
        }
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
            ("repository", session.Workspace?.Repository ?? "\u2014"),
            ("branch", session.Workspace?.Branch ?? "\u2014"),
            ("agent", session.Agent ?? "\u2014"),
            ("models", session.Models.Count > 0 ? string.Join(", ", session.Models) : "\u2014"),
            ("created", session.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("updated", session.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
            ("messages", session.Stats.MessageCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "\u2014"),
            ("tool calls", session.Stats.ToolCallCount?.ToString("N0", CultureInfo.InvariantCulture) ?? "\u2014"),
            ("tokens", session.Stats.TotalTokens?.ToString("N0", CultureInfo.InvariantCulture) ?? "\u2014"),
            ("cost", session.Stats.Cost is { } cost ? cost.ToString("0.00##", CultureInfo.InvariantCulture) : "\u2014"),
            ("files changed", session.Stats.FilesChanged?.ToString("N0", CultureInfo.InvariantCulture) ?? "\u2014"),
            ("tags", session.Tags.Count > 0 ? string.Join(" ", session.Tags.Select(tag => "#" + tag)) : "\u2014"),
            ("archived", session.IsArchived ? "yes" : "no")
        };

        if (transcript is not null && loadedRef == session.Ref && transcript.Files.Count > 0)
        {
            fields.Add(("files", string.Join(", ", transcript.Files.Take(12).Select(file => file.Path)) + (transcript.Files.Count > 12 ? $" (+{transcript.Files.Count - 12})" : string.Empty)));
        }

        var labelWidth = fields.Max(field => field.Label.Length) + 3;
        var card = CenteredCard(104, fields.Count + 4);
        DrawCard(card, "Session", [new StyledSpan("esc closes", Theme.OverlayMuted)]);
        var inner = card.Inset();
        var line = 1;

        foreach (var (label, value) in fields)
        {
            if (line >= inner.Height - 1)
            {
                break;
            }

            buffer.WriteIn(inner, line, 2, label.PadRight(labelWidth), Theme.OverlayMuted);
            var valueStyle = label is "uri" ? Theme.OverlayAccent : Theme.Overlay;
            buffer.WriteIn(inner, line++, 2 + labelWidth, TextWidth.Clip(value, inner.Width - labelWidth - 4), valueStyle);
        }
    }

    private void DrawConfirm(ConfirmState state)
    {
        var width = Math.Min(buffer.Width - 4, Math.Max(56, state.Lines.Max(line => line.Sum(span => TextWidth.Of(span.Text))) + 6));
        var card = CenteredCard(width, state.Lines.Count + 6);
        DrawCard(card, state.Title);
        var inner = card.Inset();

        for (var index = 0; index < state.Lines.Count && index < inner.Height - 3; index++)
        {
            buffer.WriteSpans(inner, index + 1, 2, state.Lines[index].Select(span => span with { Style = OnSurface(span.Style) }));
        }

        var footer = new List<StyledSpan>
        {
            new(" \u23ce ", Theme.OverlayKeyCap), new($" {state.AcceptLabel}", Theme.OverlaySecondary), new("    ", Theme.Overlay)
        };

        if (state.Copy is not null)
        {
            footer.Add(new StyledSpan(" c ", Theme.OverlayKeyCap));
            footer.Add(new StyledSpan(" copy", Theme.OverlaySecondary));
            footer.Add(new StyledSpan("    ", Theme.Overlay));
        }

        footer.Add(new StyledSpan(" esc ", Theme.OverlayKeyCap));
        footer.Add(new StyledSpan(" cancel", Theme.OverlaySecondary));
        buffer.WriteSpans(inner, inner.Height - 1, 2, footer);
    }

    private void DrawInput(InputState state)
    {
        var card = CenteredCard(Math.Min(buffer.Width - 4, 84), 5);
        DrawCard(card, state.Prompt, [new StyledSpan("\u23ce applies \u00b7 esc cancels", Theme.OverlayMuted)]);
        var inner = card.Inset();

        buffer.WriteIn(inner, 1, 2, "\u276f ", Theme.OverlayAccent);
        if (state.Text.Length == 0)
        {
            buffer.WriteIn(inner, 1, 4, " ", Theme.Caret);
            buffer.WriteIn(inner, 1, 6, state.Placeholder, Theme.OverlayMuted.Italic());
        }
        else
        {
            buffer.WriteIn(inner, 1, 4, state.Text, Theme.Overlay);
            var caretColumn = 4 + TextWidth.Of(state.Text);
            if (caretColumn < inner.Width - 1)
            {
                buffer.WriteIn(inner, 1, caretColumn, " ", Theme.Caret);
            }
        }
    }

    // ---- small helpers -----------------------------------------------------------------------

    private string FormatDate(DateTimeOffset value, DateTimeOffset now) =>
        options.RelativeDates
            ? TextUtilities.ToRelativeTime(value, now)
            : value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string SearchModeLabel(SearchMode value) => value switch
    {
        SearchMode.Lexical => "keyword",
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
