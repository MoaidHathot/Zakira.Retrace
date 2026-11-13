using System.Globalization;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Tui.Terminal;

namespace Zakira.Retrace.Tui;

public sealed partial class SessionBrowser
{
    private static readonly string?[] TimeWindows = [null, "day", "week", "month", "90d", "year"];
    private static readonly BrowserSort[] SortOrders = Enum.GetValues<BrowserSort>();
    private static readonly BrowserGroup[] Groups = Enum.GetValues<BrowserGroup>();

    private IReadOnlyList<string> EffectiveProviders => parsedQuery.Providers ?? [.. backend.SourceIds.Where(selectedProviders.Contains)];

    private BrowserSort EffectiveSort => parsedQuery.Sort ?? sortOrder;

    private BrowserGroup EffectiveGroup => parsedQuery.Group ?? groupBy;

    private void OpenFilters(bool providers)
    {
        openWhenReady = false;
        filterCursor = providers ? 0 : backend.SourceIds.Count;
        filterScroll = 0;
        mode = Mode.Filters;
    }

    private void RemoveQueryFacets(params string[] fields)
    {
        query = BrowserQuery.RemoveFacets(query, fields);
        caret = Math.Min(caret, query.Length);
    }

    private void SetProviders(IEnumerable<string> providers)
    {
        var copy = providers.ToArray();
        RemoveQueryFacets("provider", "source", "-provider", "-source");
        selectedProviders.Clear();
        selectedProviders.UnionWith(copy);
        ScheduleQuery(immediate: true);
    }

    private void CycleSourceFilter()
    {
        var sources = backend.SourceIds;
        if (sources.Count == 0)
        {
            return;
        }

        var current = EffectiveProviders;
        var index = current.Count == 1 ? sources.ToList().FindIndex(id => id.Equals(current[0], StringComparison.OrdinalIgnoreCase)) : -1;
        SetProviders(index + 1 >= sources.Count ? sources : [sources[index + 1]]);
        SetStatus(EffectiveProviders.Count == sources.Count ? "all providers" : $"source {EffectiveProviders[0]}");
    }

    private void CycleTimeWindow(int direction)
    {
        var current = Array.IndexOf(TimeWindows, timeWindow);
        if (parsedQuery.Since is not null || parsedQuery.Until is not null || since is not null || until is not null)
        {
            current = direction > 0 ? -1 : 1;
        }

        RemoveQueryFacets("since", "until", "last");
        timeWindow = TimeWindows[(current + direction + TimeWindows.Length) % TimeWindows.Length];
        since = null;
        until = null;
        ScheduleQuery(immediate: true);
    }

    private void CycleSort(int direction = 1)
    {
        var index = Array.IndexOf(SortOrders, EffectiveSort);
        sortOrder = SortOrders[(index + direction + SortOrders.Length) % SortOrders.Length];
        RemoveQueryFacets("sort");
        parsedQuery = parsedQuery with { Sort = null };
        if (!lastQueryWasSearch || !ApplyPresentation())
        {
            ScheduleQuery(immediate: true);
        }
    }

    private void CycleGrouping(int direction = 1)
    {
        var index = Array.IndexOf(Groups, EffectiveGroup);
        groupBy = Groups[(index + direction + Groups.Length) % Groups.Length];
        RemoveQueryFacets("group");
        parsedQuery = parsedQuery with { Group = null };
        if (!ApplyPresentation())
        {
            ScheduleQuery(immediate: true);
        }
    }

    private bool ApplyPresentation()
    {
        if (!hasLoadedOnce || queryLoading || appliedQueryRevision != queryRevision)
        {
            return false;
        }

        // Grouping and reordering retrieved search hits need no database work or inference.
        // Browse sorting still re-queries, because its ordering must precede the list limit.
        var previous = Selected?.Session.Ref;
        rows = OrderRows(rows);
        RebuildListLines();
        selected = previous is null ? 0 : Math.Max(0, IndexOfRef(previous));
        listScroll = 0;
        appliedQueryRevision = ++queryRevision;
        EnsureSelectionVisible();
        dirty = true;
        return true;
    }

    private void ResetFilters()
    {
        baseFilter = SessionFilter.All;
        since = null;
        until = null;
        timeWindow = null;
        sortOrder = BrowserSort.Relevance;
        groupBy = BrowserGroup.None;
        here = false;
        includeArchived = false;
        RemoveQueryFacets();
        selectedProviders.Clear();
        selectedProviders.UnionWith(backend.SourceIds);
        ScheduleQuery(immediate: true);
        SetStatus("filters reset; all enabled providers", StatusKind.Ok);
    }

    private int FilterItemCount => backend.SourceIds.Count + 6;

    private void HandleFilterKey(KeyEvent key)
    {
        if (key.Kind == KeyKind.Function && key.Number == 1)
        {
            OpenHelp();
            return;
        }

        if (key.Kind is KeyKind.Enter or KeyKind.Escape || key.Is('q'))
        {
            mode = Mode.List;
            return;
        }

        if (key.Is('a'))
        {
            SetProviders(backend.SourceIds);
            return;
        }

        if (key.Is('n'))
        {
            SetProviders([]);
            return;
        }

        if (key.Is('0'))
        {
            ResetFilters();
            return;
        }

        if (key.Kind is KeyKind.Up or KeyKind.MouseScrollUp || key.Is('k') || (key.Kind == KeyKind.Tab && key.Shift))
        {
            filterCursor = Math.Max(0, filterCursor - 1);
        }
        else if (key.Kind is KeyKind.Down or KeyKind.MouseScrollDown or KeyKind.Tab || key.Is('j'))
        {
            filterCursor = Math.Min(FilterItemCount - 1, filterCursor + 1);
        }
        else if (key.Kind == KeyKind.Home)
        {
            filterCursor = 0;
        }
        else if (key.Kind == KeyKind.End)
        {
            filterCursor = FilterItemCount - 1;
        }
        else if (key.Kind is KeyKind.Left or KeyKind.Right || key.Is(' '))
        {
            ChangeFilter(key.Kind == KeyKind.Left ? -1 : 1);
        }
        else if (key.Kind == KeyKind.MouseClick)
        {
            var inner = FilterCard().Inset();
            var slot = key.Row - inner.Top - 2;
            if (inner.Contains(key.Row, key.Column) && slot >= 0 && slot < FilterVisibleItems(inner) && filterScroll + slot < FilterItemCount)
            {
                filterCursor = filterScroll + slot;
                ChangeFilter(1);
            }
        }
    }

    private void ChangeFilter(int direction)
    {
        var count = backend.SourceIds.Count;
        if (filterCursor < count)
        {
            var provider = backend.SourceIds[filterCursor];
            var selection = new HashSet<string>(EffectiveProviders, StringComparer.OrdinalIgnoreCase);
            if (!selection.Remove(provider))
            {
                selection.Add(provider);
            }

            SetProviders(selection);
            return;
        }

        switch (filterCursor - count)
        {
            case 0: CycleTimeWindow(direction); break;
            case 1: CycleSort(direction); break;
            case 2: CycleGrouping(direction); break;
            case 3: here = !here; ScheduleQuery(immediate: true); break;
            case 4: includeArchived = !includeArchived; ScheduleQuery(immediate: true); break;
            case 5: ResetFilters(); break;
        }
    }

    private Rect FilterCard() => CenteredCard(88, FilterItemCount + 8);

    private static int FilterVisibleItems(Rect inner) => Math.Max(1, inner.Height - 5);

    private void DrawFilters()
    {
        var card = FilterCard();
        DrawCard(card, "Providers & filters", [new StyledSpan(card.Width < 60 ? "Esc closes" : "changes apply immediately", Theme.OverlayMuted)]);
        var inner = card.Inset();
        buffer.WriteIn(inner, 0, 1, inner.Width < 55 ? "Space/←→ change · Esc close" : "Space toggles · left/right cycles · Enter/Esc closes", Theme.OverlayMuted);

        var labels = backend.SourceIds.Select(id => $"[{(EffectiveProviders.Contains(id, StringComparer.OrdinalIgnoreCase) ? 'x' : ' ')}] {id}").ToList();
        labels.AddRange([
            $"Time window     {TimeLabel()}",
            $"Order results   {SortLabel(EffectiveSort)}",
            $"Group results   {EffectiveGroup.ToString().ToLowerInvariant()}",
            $"[{(here ? 'x' : ' ')}] current directory",
            $"[{(includeArchived ? 'x' : ' ')}] include archived",
            "Reset all filters (keep search text)"
        ]);

        var visible = FilterVisibleItems(inner);
        filterScroll = Math.Clamp(filterScroll, Math.Max(0, filterCursor - visible + 1), filterCursor);
        for (var slot = 0; slot < visible && filterScroll + slot < labels.Count; slot++)
        {
            var index = filterScroll + slot;
            var selectedItem = index == filterCursor;
            var style = selectedItem ? Theme.OverlayKeyCap : Theme.Overlay;
            buffer.WriteIn(inner, slot + 2, 1, TextWidth.Fit((selectedItem ? "› " : "  ") + labels[index], inner.Width - 2), style);
        }

        buffer.DrawScrollbar(new Rect(inner.Right - 1, inner.Top + 2, 1, visible), FilterItemCount, visible, filterScroll, Theme.ScrollTrack, Theme.ScrollThumb);
        buffer.WriteIn(inner, inner.Height - 2, 1, inner.Width < 60 ? "a all · n none · 0 reset" : "a all providers · n none · 0 reset", Theme.OverlayAccent);
        buffer.WriteIn(inner, inner.Height - 1, 1, inner.Width < 60 ? "↑↓ move · F1 syntax help" : "Query: provider:opencode since:7d sort:newest group:provider", Theme.OverlayMuted);
    }

    private string TimeLabel()
    {
        var lower = parsedQuery.Since ?? since;
        var upper = parsedQuery.Until ?? until;
        if (lower is not null || upper is not null)
        {
            return $"{(lower is { } from ? from.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "any")} .. {(upper is { } to ? to.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "now")}";
        }

        return timeWindow switch
        {
            "day" => "last 24 hours",
            "week" => "last 7 days",
            "month" => "last month",
            "90d" => "last 90 days",
            "year" => "last year",
            _ => "all time"
        };
    }

    private static string SortLabel(BrowserSort value) => value == BrowserSort.Size ? "size (messages)" : value.ToString().ToLowerInvariant();

    private List<string> FilterSummaryLines()
    {
        if (filterSummaryRevision == queryRevision && filterSummaryWidth == buffer.Width)
        {
            return filterSummary;
        }

        var providers = EffectiveProviders;
        var label = providers.Count == 0 ? "none" : providers.Count == backend.SourceIds.Count ? "all" : string.Join(", ", providers);
        var lines = TextWidth.Wrap($"F2 providers: {label} · time: {TimeLabel()}", Math.Max(10, buffer.Width - 2)).ToList();
        var details = $"F3 filters · order: {SortLabel(EffectiveSort)} · group: {EffectiveGroup.ToString().ToLowerInvariant()}";
        var filter = BuildFilter(0);
        if (here)
        {
            details += " · here";
        }

        if (includeArchived)
        {
            details += " · archived";
        }

        if (filter.Repository is { } repo)
        {
            details += $" · repo: {repo}";
        }

        if (filter.Agent is { } agent)
        {
            details += $" · agent: {agent}";
        }

        if (filter.Model is { } model)
        {
            details += $" · model: {model}";
        }

        if (filter.Tags.Count > 0)
        {
            details += " · tags: " + string.Join(",", filter.Tags);
        }

        if (filter.Branch is { } branch)
        {
            details += $" · branch: {branch}";
        }

        if (filter.MinMessages is { } minimum)
        {
            details += $" · min messages: {minimum}";
        }

        lines.AddRange(TextWidth.Wrap(details, Math.Max(10, buffer.Width - 2)));
        filterSummary = lines;
        filterSummaryRevision = queryRevision;
        filterSummaryWidth = buffer.Width;
        return lines;
    }

    private IReadOnlyList<Row> OrderRows(IReadOnlyList<Row> fetched)
    {
        IEnumerable<Row> ordered = EffectiveSort switch
        {
            BrowserSort.Newest => fetched.OrderByDescending(row => row.Session.UpdatedAt).ThenBy(row => row.Session.Ref.Uri, StringComparer.Ordinal),
            BrowserSort.Oldest => fetched.OrderBy(row => row.Session.UpdatedAt).ThenBy(row => row.Session.Ref.Uri, StringComparer.Ordinal),
            BrowserSort.Provider => fetched.OrderBy(row => row.Session.Ref.SourceId, StringComparer.OrdinalIgnoreCase).ThenByDescending(row => row.Session.UpdatedAt),
            BrowserSort.Title => fetched.OrderBy(row => row.Session.Title, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Session.Ref.Uri, StringComparer.Ordinal),
            BrowserSort.Size => fetched.OrderByDescending(row => row.Session.Stats.MessageCount ?? 0).ThenByDescending(row => row.Session.UpdatedAt),
            _ when fetched.Any(row => row.Hit is not null) => fetched.OrderByDescending(row => row.Hit?.Score ?? 0).ThenBy(row => row.Session.Ref.Uri, StringComparer.Ordinal),
            _ when baseFilter.Sort == SessionSortOrder.Created => fetched.OrderByDescending(row => row.Session.CreatedAt),
            _ => fetched.OrderByDescending(row => row.Session.UpdatedAt)
        };
        if (EffectiveGroup != BrowserGroup.None)
        {
            var groups = ordered.GroupBy(row => GroupKey(row.Session));
            ordered = EffectiveGroup == BrowserGroup.Provider
                ? groups.OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase).SelectMany(group => group)
                : groups.OrderByDescending(group => group.Key, StringComparer.Ordinal).SelectMany(group => group);
        }

        return [.. ordered];
    }

    private string GroupKey(SessionSummary session)
    {
        var date = session.UpdatedAt.ToLocalTime().Date;
        return EffectiveGroup switch
        {
            BrowserGroup.Provider => session.Ref.SourceId,
            BrowserGroup.Day => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            BrowserGroup.Week => "Week of " + date.AddDays(-((int)date.DayOfWeek + 6) % 7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            BrowserGroup.Month => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => string.Empty
        };
    }

    private void RebuildListLines()
    {
        var lines = new List<DisplayLine>();
        rowOffsets = new int[rows.Count];
        var counts = rows.GroupBy(row => GroupKey(row.Session)).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string? previous = null;
        for (var index = 0; index < rows.Count; index++)
        {
            var group = GroupKey(rows[index].Session);
            if (EffectiveGroup != BrowserGroup.None && group != previous)
            {
                lines.Add(new DisplayLine(-1, Heading: $"{group} · {counts[group]}"));
            }

            previous = group;
            rowOffsets[index] = lines.Count;
            lines.Add(new DisplayLine(index));
            lines.Add(new DisplayLine(index, Detail: true));
        }

        listLines = lines;
    }
}
