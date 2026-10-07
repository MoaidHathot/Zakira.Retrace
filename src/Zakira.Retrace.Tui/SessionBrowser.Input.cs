using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

public sealed partial class SessionBrowser
{
    private void HandleKey(KeyEvent key)
    {
        if (key.Kind == KeyKind.Focus)
        {
            return;
        }

        dirty = true;

        if (key.IsCtrl('c'))
        {
            Quit();
            return;
        }

        if (key.IsCtrl('l'))
        {
            screen.Invalidate();
            return;
        }

        switch (mode)
        {
            case Mode.Help:
                mode = Mode.List;
                return;

            case Mode.Info:
                if (key.Kind is KeyKind.Escape or KeyKind.Enter || key.Is('q') || key.Is('i'))
                {
                    mode = Mode.List;
                }

                return;

            case Mode.Confirm:
                HandleConfirmKey(key);
                return;

            case Mode.Input:
                HandleInputKey(key);
                return;

            case Mode.Reader:
                HandleReaderKey(key);
                return;
        }

        if (key.IsMouse)
        {
            HandleMouse(key);
            return;
        }

        if (searchFocused)
        {
            HandleSearchKey(key);
            return;
        }

        HandleListKey(key);
    }

    // ---- search box --------------------------------------------------------------------------

    private void HandleSearchKey(KeyEvent key)
    {
        switch (key.Kind)
        {
            case KeyKind.Escape:
                searchFocused = false;
                focus = Pane.List;
                return;

            case KeyKind.Tab:
                searchFocused = false;
                focus = Pane.List;
                return;

            case KeyKind.Enter:
                searchFocused = false;
                focus = Pane.List;
                if (rows.Count > 0)
                {
                    OpenSelected();
                }

                return;

            case KeyKind.Up:
                MoveSelection(-1);
                return;

            case KeyKind.Down:
                MoveSelection(1);
                return;

            case KeyKind.PageUp:
                MoveSelection(-ListPageSize());
                return;

            case KeyKind.PageDown:
                MoveSelection(ListPageSize());
                return;

            case KeyKind.Left:
                caret = Math.Max(0, caret - 1);
                return;

            case KeyKind.Right:
                caret = Math.Min(query.Length, caret + 1);
                return;

            case KeyKind.Home:
                caret = 0;
                return;

            case KeyKind.End:
                caret = query.Length;
                return;

            case KeyKind.Backspace:
                if (key.Ctrl || key.Alt)
                {
                    DeleteWordBeforeCaret();
                }
                else if (caret > 0)
                {
                    query = query.Remove(caret - 1, 1);
                    caret--;
                    ScheduleQuery();
                }

                return;

            case KeyKind.Delete:
                if (caret < query.Length)
                {
                    query = query.Remove(caret, 1);
                    ScheduleQuery();
                }

                return;

            case KeyKind.Paste when key.Text is { Length: > 0 } pasted:
                InsertText(pasted.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' '));
                return;

            case KeyKind.Function when key.Number == 1:
                mode = Mode.Help;
                return;

            case KeyKind.Function when key.Number == 5:
                RefreshIndex();
                return;
        }

        if (key.Kind != KeyKind.Character)
        {
            return;
        }

        if (key.Ctrl)
        {
            switch (key.Character)
            {
                case 'u':
                    query = string.Empty;
                    caret = 0;
                    ScheduleQuery(immediate: true);
                    break;

                case 'w':
                    DeleteWordBeforeCaret();
                    break;

                case 'a':
                    caret = 0;
                    break;

                case 'e':
                    caret = query.Length;
                    break;

                case 'r':
                    RefreshIndex();
                    break;

                case 'n':
                    MoveSelection(1);
                    break;

                case 'p':
                    MoveSelection(-1);
                    break;
            }

            return;
        }

        if (key.Alt)
        {
            return;
        }

        InsertText(key.Text ?? key.Character.ToString());
    }

    private void InsertText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        query = query.Insert(Math.Clamp(caret, 0, query.Length), text);
        caret += text.Length;
        ScheduleQuery();
    }

    private void DeleteWordBeforeCaret()
    {
        if (caret == 0)
        {
            return;
        }

        var end = caret;
        var start = end;
        while (start > 0 && query[start - 1] == ' ')
        {
            start--;
        }

        while (start > 0 && query[start - 1] != ' ')
        {
            start--;
        }

        query = query.Remove(start, end - start);
        caret = start;
        ScheduleQuery();
    }

    // ---- list and preview --------------------------------------------------------------------

    private void HandleListKey(KeyEvent key)
    {
        if (focus == Pane.Preview && HandlePreviewKey(key))
        {
            return;
        }

        switch (key.Kind)
        {
            case KeyKind.Up:
                MoveSelection(-1);
                return;

            case KeyKind.Down:
                MoveSelection(1);
                return;

            case KeyKind.PageUp:
                MoveSelection(-ListPageSize());
                return;

            case KeyKind.PageDown:
                MoveSelection(ListPageSize());
                return;

            case KeyKind.Home:
                SelectIndex(0);
                return;

            case KeyKind.End:
                SelectIndex(rows.Count - 1);
                return;

            case KeyKind.Enter:
            case KeyKind.Right:
                OpenSelected();
                return;

            case KeyKind.Tab:
                if (ComputeLayout().ShowPreview)
                {
                    focus = focus == Pane.List ? Pane.Preview : Pane.List;
                }

                return;

            case KeyKind.Escape:
                if (focus == Pane.Preview)
                {
                    focus = Pane.List;
                }
                else if (query.Length > 0)
                {
                    query = string.Empty;
                    caret = 0;
                    ScheduleQuery(immediate: true);
                }

                return;

            case KeyKind.Function when key.Number == 1:
                mode = Mode.Help;
                return;

            case KeyKind.Function when key.Number == 5:
                RefreshIndex();
                return;
        }

        if (key.Kind != KeyKind.Character)
        {
            return;
        }

        if (key.Ctrl)
        {
            switch (key.Character)
            {
                case 'd':
                    MoveSelection(ListPageSize() / 2);
                    break;

                case 'u':
                    MoveSelection(-ListPageSize() / 2);
                    break;

                case 'f':
                    MoveSelection(ListPageSize());
                    break;

                case 'b':
                    MoveSelection(-ListPageSize());
                    break;

                case 'n':
                    MoveSelection(1);
                    break;

                case 'p':
                    MoveSelection(-1);
                    break;

                case 'r':
                    RefreshIndex();
                    break;
            }

            return;
        }

        if (key.Alt)
        {
            return;
        }

        switch (key.Character)
        {
            case '/':
                searchFocused = true;
                caret = query.Length;
                break;

            case 'j':
                MoveSelection(1);
                break;

            case 'k':
                MoveSelection(-1);
                break;

            case 'g':
                SelectIndex(0);
                break;

            case 'G':
                SelectIndex(rows.Count - 1);
                break;

            case 'l':
                OpenSelected();
                break;

            case 'J':
                ScrollPreview(3);
                break;

            case 'K':
                ScrollPreview(-3);
                break;

            case 'q':
                Quit();
                break;

            case '?':
                mode = Mode.Help;
                break;

            case 'i':
                if (Selected is not null)
                {
                    mode = Mode.Info;
                }

                break;

            case 'r':
                BeginResume(fork: false);
                break;

            case 'R':
                BeginResume(fork: true);
                break;

            case 'c':
                CopyResumeCommand();
                break;

            case 'y':
                CopyText(Selected?.Session.Ref.Uri, "URI");
                break;

            case 'Y':
                CopyText(Selected?.Session.Ref.NativeId, "session id");
                break;

            case 'd':
                CopyText(Selected?.Session.Workspace?.Path, "directory");
                break;

            case 'o':
                OpenWorkspaceFolder();
                break;

            case 'e':
                ExportTranscript();
                break;

            case 't':
                BeginTagInput();
                break;

            case 's':
                CycleSourceFilter();
                break;

            case 'w':
                here = !here;
                ScheduleQuery(immediate: true);
                break;

            case 'a':
                includeArchived = !includeArchived;
                ScheduleQuery(immediate: true);
                break;

            case 'm':
                CycleSearchMode();
                break;

            case 'x':
                showToolOutput = !showToolOutput;
                InvalidateTranscript();
                SetStatus(showToolOutput ? "tool output shown" : "tool output hidden");
                break;

            case 'z':
                showReasoning = !showReasoning;
                InvalidateTranscript();
                SetStatus(showReasoning ? "reasoning shown" : "reasoning hidden");
                break;
        }
    }

    private bool HandlePreviewKey(KeyEvent key)
    {
        switch (key.Kind)
        {
            case KeyKind.Up:
                ScrollPreview(-1);
                return true;

            case KeyKind.Down:
                ScrollPreview(1);
                return true;

            case KeyKind.PageUp:
                ScrollPreview(-PreviewPageSize());
                return true;

            case KeyKind.PageDown:
                ScrollPreview(PreviewPageSize());
                return true;

            case KeyKind.Home:
                previewScroll = 0;
                return true;

            case KeyKind.End:
                previewScroll = int.MaxValue / 2;
                return true;

            case KeyKind.Left:
                focus = Pane.List;
                return true;
        }

        if (key.Kind != KeyKind.Character || key.Alt)
        {
            return false;
        }

        if (key.Ctrl)
        {
            switch (key.Character)
            {
                case 'd':
                    ScrollPreview(PreviewPageSize() / 2);
                    return true;

                case 'u':
                    ScrollPreview(-PreviewPageSize() / 2);
                    return true;
            }

            return false;
        }

        switch (key.Character)
        {
            case 'j':
                ScrollPreview(1);
                return true;

            case 'k':
                ScrollPreview(-1);
                return true;

            case 'g':
                previewScroll = 0;
                return true;

            case 'G':
                previewScroll = int.MaxValue / 2;
                return true;

            case 'h':
                focus = Pane.List;
                return true;
        }

        return false;
    }

    private void HandleMouse(KeyEvent key)
    {
        var layout = ComputeLayout();

        if (key.Kind == KeyKind.MouseClick)
        {
            if (layout.Search.Contains(key.Row, key.Column))
            {
                searchFocused = true;
                caret = query.Length;
                return;
            }

            if (layout.List.Contains(key.Row, key.Column))
            {
                searchFocused = false;
                focus = Pane.List;
                var inner = layout.List.Inset();
                if (inner.Contains(key.Row, key.Column))
                {
                    var index = listScroll + (key.Row - inner.Top) / 2;
                    if (index < rows.Count)
                    {
                        if (index == selected)
                        {
                            OpenSelected();
                        }
                        else
                        {
                            SelectIndex(index);
                        }
                    }
                }

                return;
            }

            if (layout.ShowPreview && layout.Preview.Contains(key.Row, key.Column))
            {
                searchFocused = false;
                focus = Pane.Preview;
            }

            return;
        }

        var delta = key.Kind == KeyKind.MouseScrollUp ? -3 : 3;

        if (layout.ShowPreview && layout.Preview.Contains(key.Row, key.Column))
        {
            ScrollPreview(delta);
        }
        else
        {
            MoveSelection(delta / 3);
        }
    }

    private void MoveSelection(int delta)
    {
        if (rows.Count == 0)
        {
            return;
        }

        SelectIndex(selected + delta);
    }

    private void SelectIndex(int index)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var clamped = Math.Clamp(index, 0, rows.Count - 1);
        if (clamped == selected)
        {
            return;
        }

        selected = clamped;
        EnsureSelectionVisible();
        previewScroll = 0;
        SchedulePreview();
        dirty = true;
    }

    private void ScrollPreview(int delta)
    {
        previewScroll = Math.Max(0, previewScroll + delta);
        dirty = true;
    }

    private void OpenSelected()
    {
        if (Selected is null)
        {
            return;
        }

        if (options.Pick != PickKind.None)
        {
            Pick();
            return;
        }

        mode = Mode.Reader;
        readerScroll = 0;
        SchedulePreview(immediate: true);
    }

    private void Quit()
    {
        result = BrowserResult.Quit;
        running = false;
    }

    // ---- reader ------------------------------------------------------------------------------

    private void HandleReaderKey(KeyEvent key)
    {
        var page = ReaderPageSize();

        switch (key.Kind)
        {
            case KeyKind.Escape:
            case KeyKind.Left:
            case KeyKind.Backspace:
                mode = Mode.List;
                return;

            case KeyKind.Up:
                ScrollReader(-1);
                return;

            case KeyKind.Down:
                ScrollReader(1);
                return;

            case KeyKind.PageUp:
                ScrollReader(-page);
                return;

            case KeyKind.PageDown:
                ScrollReader(page);
                return;

            case KeyKind.Home:
                readerScroll = 0;
                return;

            case KeyKind.End:
                ScrollReader(int.MaxValue / 4);
                return;

            case KeyKind.MouseScrollUp:
                ScrollReader(-3);
                return;

            case KeyKind.MouseScrollDown:
                ScrollReader(3);
                return;

            case KeyKind.Function when key.Number == 1:
                mode = Mode.Help;
                return;
        }

        if (key.Kind != KeyKind.Character || key.Alt)
        {
            return;
        }

        if (key.Ctrl)
        {
            switch (key.Character)
            {
                case 'd':
                    ScrollReader(page / 2);
                    break;

                case 'u':
                    ScrollReader(-page / 2);
                    break;

                case 'f':
                    ScrollReader(page);
                    break;

                case 'b':
                    ScrollReader(-page);
                    break;
            }

            return;
        }

        switch (key.Character)
        {
            case 'q':
            case 'h':
                mode = Mode.List;
                break;

            case '/':
                mode = Mode.List;
                searchFocused = true;
                caret = query.Length;
                break;

            case 'j':
                ScrollReader(1);
                break;

            case 'k':
                ScrollReader(-1);
                break;

            case ' ':
                ScrollReader(page);
                break;

            case 'b':
                ScrollReader(-page);
                break;

            case 'g':
                readerScroll = 0;
                break;

            case 'G':
                ScrollReader(int.MaxValue / 4);
                break;

            case 'n':
                JumpToMatch(forward: true);
                break;

            case 'N':
                JumpToMatch(forward: false);
                break;

            case ']':
                JumpToTurn(forward: true);
                break;

            case '[':
                JumpToTurn(forward: false);
                break;

            case 'x':
                showToolOutput = !showToolOutput;
                InvalidateTranscript();
                SetStatus(showToolOutput ? "tool output shown" : "tool output hidden");
                break;

            case 'z':
                showReasoning = !showReasoning;
                InvalidateTranscript();
                SetStatus(showReasoning ? "reasoning shown" : "reasoning hidden");
                break;

            case '?':
                mode = Mode.Help;
                break;

            case 'i':
                mode = Mode.Info;
                break;

            case 'r':
                BeginResume(fork: false);
                break;

            case 'R':
                BeginResume(fork: true);
                break;

            case 'c':
                CopyResumeCommand();
                break;

            case 'y':
                CopyText(Selected?.Session.Ref.Uri, "URI");
                break;

            case 'Y':
                CopyText(Selected?.Session.Ref.NativeId, "session id");
                break;

            case 'd':
                CopyText(Selected?.Session.Workspace?.Path, "directory");
                break;

            case 'o':
                OpenWorkspaceFolder();
                break;

            case 'e':
                ExportTranscript();
                break;

            case 't':
                BeginTagInput();
                break;

            case 'J':
                MoveSelection(1);
                SchedulePreview(immediate: true);
                break;

            case 'K':
                MoveSelection(-1);
                SchedulePreview(immediate: true);
                break;
        }
    }

    private void ScrollReader(int delta)
    {
        var lines = FormattedLines(Math.Max(10, buffer.Width - 5));
        var page = ReaderPageSize();
        var max = Math.Max(0, lines.Count - page);
        var next = (int)Math.Clamp((long)readerScroll + delta, 0, max);

        if (delta > 0 && next >= max && transcript is { IsTruncated: true })
        {
            LoadMoreTurns();
        }

        readerScroll = next;
        dirty = true;
    }

    private void JumpToMatch(bool forward)
    {
        var lines = FormattedLines(Math.Max(10, buffer.Width - 5));
        if (lines.Count == 0)
        {
            return;
        }

        var start = readerScroll;
        if (forward)
        {
            for (var index = start + 1; index < lines.Count; index++)
            {
                if (lines[index].HasMatch)
                {
                    readerScroll = index;
                    return;
                }
            }

            SetStatus(transcript is { IsTruncated: true } ? "no more matches in the loaded part; scroll to load more" : "no more matches");
        }
        else
        {
            for (var index = start - 1; index >= 0; index--)
            {
                if (lines[index].HasMatch)
                {
                    readerScroll = index;
                    return;
                }
            }

            SetStatus("no earlier matches");
        }
    }

    private void JumpToTurn(bool forward)
    {
        var lines = FormattedLines(Math.Max(10, buffer.Width - 5));
        if (lines.Count == 0)
        {
            return;
        }

        if (forward)
        {
            for (var index = readerScroll + 1; index < lines.Count; index++)
            {
                if (lines[index].IsTurnStart)
                {
                    readerScroll = index;
                    return;
                }
            }

            if (transcript is { IsTruncated: true })
            {
                LoadMoreTurns();
            }
        }
        else
        {
            for (var index = readerScroll - 1; index >= 0; index--)
            {
                if (lines[index].IsTurnStart)
                {
                    readerScroll = index;
                    return;
                }
            }

            readerScroll = 0;
        }
    }

    // ---- overlays ----------------------------------------------------------------------------

    private void HandleConfirmKey(KeyEvent key)
    {
        if (confirm is null)
        {
            mode = Mode.List;
            return;
        }

        if (key.Kind == KeyKind.Enter || key.Is('y'))
        {
            var accept = confirm.Accept;
            confirm = null;
            mode = Mode.List;
            accept();
            return;
        }

        if (key.Is('c') && confirm.Copy is not null)
        {
            confirm.Copy();
            return;
        }

        if (key.Kind == KeyKind.Escape || key.Is('n') || key.Is('q'))
        {
            confirm = null;
            mode = Mode.List;
        }
    }

    private void HandleInputKey(KeyEvent key)
    {
        if (input is null)
        {
            mode = Mode.List;
            return;
        }

        switch (key.Kind)
        {
            case KeyKind.Escape:
                input = null;
                mode = Mode.List;
                return;

            case KeyKind.Enter:
            {
                var submit = input.Submit;
                var text = input.Text;
                input = null;
                mode = Mode.List;
                submit(text);
                return;
            }

            case KeyKind.Backspace:
                if (input.Text.Length > 0)
                {
                    input.Text = input.Text[..^1];
                }

                return;

            case KeyKind.Paste when key.Text is { Length: > 0 } pasted:
                input.Text += pasted.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');
                return;

            case KeyKind.Character when key.Ctrl && key.Character == 'u':
                input.Text = string.Empty;
                return;

            case KeyKind.Character when !key.Ctrl && !key.Alt:
                input.Text += key.Text ?? key.Character.ToString();
                return;
        }
    }

    // ---- filters -----------------------------------------------------------------------------

    private void CycleSourceFilter()
    {
        var sources = backend.SourceIds;
        if (sources.Count == 0)
        {
            return;
        }

        if (sourceFilter is null)
        {
            sourceFilter = sources[0];
        }
        else
        {
            var index = sources.ToList().FindIndex(id => id.Equals(sourceFilter, StringComparison.OrdinalIgnoreCase));
            sourceFilter = index < 0 || index + 1 >= sources.Count ? null : sources[index + 1];
        }

        SetStatus(sourceFilter is null ? "all sources" : $"source: {sourceFilter}");
        ScheduleQuery(immediate: true);
    }

    private void CycleSearchMode()
    {
        searchMode = searchMode switch
        {
            SearchMode.Hybrid => SearchMode.Lexical,
            SearchMode.Lexical => SearchMode.Semantic,
            _ => SearchMode.Hybrid
        };

        SetStatus($"search mode: {SearchModeLabel(searchMode)}");
        if (query.Trim().Length > 0)
        {
            ScheduleQuery(immediate: true);
        }
    }
}
