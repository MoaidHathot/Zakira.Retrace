using System.Globalization;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Core.Search;

/// <summary>Shared date and rolling-window syntax for CLI flags and the browser search box.</summary>
public static class TimeBounds
{
    /// <summary>Parses an inclusive bound. Bare upper dates include the entire local day.</summary>
    public static DateTimeOffset? Parse(string? value, bool isLowerBound, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var current = now ?? DateTimeOffset.UtcNow;
        try
        {
            switch (trimmed.ToLowerInvariant())
            {
                case "day": return current.AddDays(-1);
                case "week": return current.AddDays(-7);
                case "month": return current.AddMonths(-1);
                case "year": return current.AddYears(-1);
            }

            if (trimmed.Length > 1 && char.IsDigit(trimmed[0]))
            {
                var unit = char.ToLowerInvariant(trimmed[^1]);
                if (unit is 'd' or 'h' or 'm' or 'w'
                    && double.TryParse(trimmed[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
                    && double.IsFinite(amount) && amount >= 0)
                {
                    return unit switch
                    {
                        'w' => current.AddDays(-amount * 7),
                        'd' => current.AddDays(-amount),
                        'h' => current.AddHours(-amount),
                        _ => current.AddMinutes(-amount)
                    };
                }
            }

            if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                var dateOnly = DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
                return !isLowerBound && dateOnly ? parsed.AddDays(1).AddTicks(-1) : parsed;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Report the same actionable input error for an overflowing duration or end date.
        }

        throw new RetraceException($"Could not parse '{value}' as a date or duration. Try 2026-01-31, 7d / 12h / 30m, week, or month.");
    }
}
