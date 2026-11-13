using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;
using Zakira.Retrace.Core.Search;

namespace Zakira.Retrace.Tui;

/// <summary>Ordering of the retrieved results; relevance uses recency when browsing without text.</summary>
public enum BrowserSort
{
    Relevance,
    Newest,
    Oldest,
    Provider,
    Title,
    Size
}

/// <summary>Visual sections in the result list, based on the session's last update.</summary>
public enum BrowserGroup
{
    None,
    Provider,
    Day,
    Week,
    Month
}

/// <summary>Search text and optional facets. Null means use the browser's current control value.</summary>
public sealed record BrowserQuery
{
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<string>? Providers { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }

    public BrowserSort? Sort { get; init; }

    public BrowserGroup? Group { get; init; }

    public string? Repository { get; init; }

    public string? Agent { get; init; }

    public string? Model { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    private sealed record Token(string Raw, string? Field, string Value);

    /// <summary>
    /// Recognises facets only at token boundaries and outside quoted phrases. Other colons (URLs,
    /// paths and ordinary prose) remain searchable. Provider facets replace the picker selection.
    /// </summary>
    public static BrowserQuery Parse(string text, IReadOnlyList<string> availableProviders, DateTimeOffset now)
    {
        var result = new BrowserQuery();
        var words = new List<string>();
        var providers = new List<string>();
        var excluded = new List<string>();
        var hasProvider = false;
        var none = false;
        var tags = new List<string>();

        foreach (var token in Tokens(text))
        {
            if (token.Field is null)
            {
                words.Add(token.Raw);
                continue;
            }

            if (token.Value.Length == 0)
            {
                throw new RetraceException($"{token.Field}: needs a value. F2 selects providers; F3 opens filters.");
            }

            switch (token.Field)
            {
                case "provider" or "source":
                    hasProvider = true;
                    if (token.Value.Equals("none", StringComparison.OrdinalIgnoreCase))
                    {
                        none = true;
                    }
                    else
                    {
                        providers.Add(token.Value);
                    }

                    break;
                case "-provider" or "-source": excluded.Add(token.Value); break;
                case "since" or "last": result = result with { Since = TimeBounds.Parse(token.Value, true, now) }; break;
                case "until": result = result with { Until = TimeBounds.Parse(token.Value, false, now) }; break;
                case "sort": result = result with { Sort = ParseSort(token.Value) }; break;
                case "group": result = result with { Group = ParseGroup(token.Value) }; break;
                case "repo": result = result with { Repository = token.Value }; break;
                case "agent": result = result with { Agent = token.Value }; break;
                case "model": result = result with { Model = token.Value }; break;
                case "tag": tags.Add(token.Value.TrimStart('#')); break;
            }
        }

        if (hasProvider || excluded.Count > 0)
        {
            var included = hasProvider ? SourceSelection.Resolve(providers, availableProviders) : availableProviders;
            var omitted = SourceSelection.Resolve(excluded, availableProviders);
            result = result with { Providers = none ? [] : [.. included.Except(omitted, StringComparer.OrdinalIgnoreCase)] };
        }

        if (result.Since > result.Until)
        {
            throw new RetraceException("since: must be earlier than until:.");
        }

        return result with { Text = string.Join(' ', words), Tags = tags };
    }

    /// <summary>Removes facets when the corresponding UI control changes; preserves literal search terms.</summary>
    public static string RemoveFacets(string text, params string[] fields) => string.Join(' ', Tokens(text)
        .Where(token => token.Field is null || (fields.Length > 0 && !fields.Contains(token.Field, StringComparer.Ordinal)))
        .Select(token => token.Raw));

    private static BrowserSort ParseSort(string value) => value.ToLowerInvariant() switch
    {
        "relevance" => BrowserSort.Relevance,
        "newest" or "recent" or "updated" => BrowserSort.Newest,
        "oldest" => BrowserSort.Oldest,
        "provider" or "source" => BrowserSort.Provider,
        "title" => BrowserSort.Title,
        "size" => BrowserSort.Size,
        _ => throw new RetraceException("sort: expects relevance, newest, oldest, provider, title, or size.")
    };

    private static BrowserGroup ParseGroup(string value) => value.ToLowerInvariant() switch
    {
        "none" => BrowserGroup.None,
        "provider" or "source" => BrowserGroup.Provider,
        "day" => BrowserGroup.Day,
        "week" => BrowserGroup.Week,
        "month" => BrowserGroup.Month,
        _ => throw new RetraceException("group: expects none, provider, day, week, or month.")
    };

    private static IEnumerable<Token> Tokens(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            if (char.IsWhiteSpace(text[start]))
            {
                start++;
                continue;
            }

            var end = start;
            var quoted = false;
            while (end < text.Length && (quoted || !char.IsWhiteSpace(text[end])))
            {
                if (text[end] == '"' && (end == start || text[end - 1] != '\\'))
                {
                    quoted = !quoted;
                }

                end++;
            }

            var raw = text[start..end];
            var colon = raw.IndexOf(':');
            var field = colon > 0 ? raw[..colon].ToLowerInvariant() : null;
            if (field is not ("provider" or "source" or "-provider" or "-source" or "since" or "until" or "last" or "sort" or "group" or "repo" or "agent" or "model" or "tag"))
            {
                field = null;
            }

            var value = field is null ? raw : raw[(colon + 1)..].Trim('"');
            yield return new Token(raw, field, value);
            start = end;
        }
    }
}
