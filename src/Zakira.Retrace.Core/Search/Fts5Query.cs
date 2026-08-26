using System.Text;

namespace Zakira.Retrace.Core.Search;

/// <summary>
/// Translates a user's plain search text into an FTS5 <c>MATCH</c> expression.
/// </summary>
/// <remarks>
/// <para>
/// FTS5's query language treats a long list of characters as syntax: unbalanced quotes, a bare
/// <c>-</c>, or a stray <c>*</c> all raise "fts5: syntax error near ..." and fail the whole query.
/// Users type search terms, not query expressions, so every token is quoted before it reaches
/// SQLite. That makes an arbitrary string safe by construction.
/// </para>
/// <para>
/// The one piece of syntax deliberately preserved is a double-quoted run, which becomes an exact
/// phrase. That is the single operator worth having and the one users already expect from every
/// other search box.
/// </para>
/// </remarks>
public static class Fts5Query
{
    /// <summary>Maximum tokens forwarded to SQLite, to bound the cost of a pathological query.</summary>
    private const int MaxTokens = 24;

    /// <summary>
    /// Very common English words carry almost no discriminating power but do dominate an OR query's
    /// candidate set. Dropping them keeps the shortlist focused on the terms that matter.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from", "how", "i", "if",
        "in", "is", "it", "of", "on", "or", "that", "the", "then", "there", "these", "this", "to",
        "was", "what", "when", "where", "which", "who", "why", "will", "with", "you", "your"
    };

    /// <summary>
    /// Builds a <c>MATCH</c> expression. Returns an empty string when nothing searchable remains,
    /// which callers treat as "no results" rather than as an error.
    /// </summary>
    /// <param name="query">Raw user input.</param>
    /// <param name="prefixMatch">Append <c>*</c> to each bare token so partial words match.</param>
    public static string Build(string? query, bool prefixMatch = true)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var terms = new List<string>();
        var hasPhrase = false;

        foreach (var token in Tokenize(query, ref hasPhrase))
        {
            if (terms.Count >= MaxTokens)
            {
                break;
            }

            terms.Add(token);
        }

        if (terms.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var index = 0; index < terms.Count; index++)
        {
            if (index > 0)
            {
                // OR rather than AND: recall matters more than precision here because the fused
                // ranking, not the match predicate, is what decides ordering.
                builder.Append(" OR ");
            }

            var term = terms[index];
            builder.Append('"').Append(term.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');

            // A prefix marker is only valid on a single-word token; FTS5 rejects it after a phrase.
            if (prefixMatch && !term.Contains(' ', StringComparison.Ordinal))
            {
                builder.Append('*');
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits input into search terms, preserving double-quoted runs as single phrase terms.
    /// </summary>
    public static IEnumerable<string> Tokenize(string query)
    {
        var hasPhrase = false;
        return Tokenize(query, ref hasPhrase);
    }

    private static List<string> Tokenize(string query, ref bool hasPhrase)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        void Flush(bool isPhrase)
        {
            if (current.Length == 0)
            {
                return;
            }

            var value = current.ToString().Trim();
            current.Clear();

            if (value.Length == 0)
            {
                return;
            }

            // Single characters match almost everything and are never worth a candidate slot.
            if (!isPhrase && (value.Length < 2 || StopWords.Contains(value)))
            {
                return;
            }

            tokens.Add(value);
        }

        foreach (var character in query)
        {
            if (character == '"')
            {
                Flush(inQuotes);
                if (inQuotes)
                {
                    hasPhrase = true;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && !char.IsLetterOrDigit(character) && character is not ('_' or '-' or '.' or '/' or '\\'))
            {
                Flush(isPhrase: false);
                continue;
            }

            current.Append(character);
        }

        Flush(inQuotes);
        return tokens;
    }
}
