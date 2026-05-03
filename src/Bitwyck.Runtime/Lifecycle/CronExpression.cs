using System.Text.RegularExpressions;

namespace Bitwyck.Runtime.Lifecycle;

/// <summary>
/// Minimal 5-field cron expression parser: minute hour dom month dow
/// Supports: wildcard (*), literals (5), commas (1,5,10), ranges (9-17), steps (*/15, 0-59/5).
/// Day-of-week: 0=Sun, 1=Mon, ..., 6=Sat.
/// </summary>
public sealed class CronExpression
{
    private readonly CronField _minute;   // 0-59
    private readonly CronField _hour;     // 0-23
    private readonly CronField _dom;      // 1-31
    private readonly CronField _month;    // 1-12
    private readonly CronField _dow;      // 0-6

    private CronExpression(CronField minute, CronField hour, CronField dom, CronField month, CronField dow)
    {
        _minute = minute;
        _hour   = hour;
        _dom    = dom;
        _month  = month;
        _dow    = dow;
    }

    // ── Factory ──────────────────────────────────────────────────────────────

    public static CronExpression Parse(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            throw new FormatException("Cron expression must not be empty.");

        var parts = expr.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            throw new FormatException(
                $"Cron expression must have exactly 5 fields (got {parts.Length}): \"{expr}\"");

        return new CronExpression(
            CronField.Parse(parts[0], 0,  59,  "minute"),
            CronField.Parse(parts[1], 0,  23,  "hour"),
            CronField.Parse(parts[2], 1,  31,  "day-of-month"),
            CronField.Parse(parts[3], 1,  12,  "month"),
            CronField.Parse(parts[4], 0,  6,   "day-of-week"));
    }

    // ── Matching ─────────────────────────────────────────────────────────────

    /// <summary>Returns true if <paramref name="now"/> matches all five fields.</summary>
    public bool Matches(DateTimeOffset now)
    {
        // Normalise to local minute boundary
        return _minute.Matches(now.Minute)
            && _hour.Matches(now.Hour)
            && _dom.Matches(now.Day)
            && _month.Matches(now.Month)
            && _dow.Matches((int)now.DayOfWeek);  // DayOfWeek.Sunday==0, Monday==1, ...
    }

    // ── Next-fire computation ─────────────────────────────────────────────────

    /// <summary>Returns the next firing instant strictly after <paramref name="from"/>.</summary>
    public DateTimeOffset Next(DateTimeOffset from)
    {
        // Advance by one minute to ensure "strictly after"
        var candidate = new DateTimeOffset(
            from.Year, from.Month, from.Day,
            from.Hour, from.Minute, 0, from.Offset)
            .AddMinutes(1);

        // Search up to 4 years (cron must have at least one valid date in that window)
        var limit = from.AddYears(4);

        while (candidate <= limit)
        {
            if (!_month.Matches(candidate.Month))
            {
                // Jump to first day of next month
                candidate = new DateTimeOffset(candidate.Year, candidate.Month, 1, 0, 0, 0, candidate.Offset)
                    .AddMonths(1);
                continue;
            }

            if (!_dom.Matches(candidate.Day) || !_dow.Matches((int)candidate.DayOfWeek))
            {
                // Jump to midnight of the next day
                candidate = new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day, 0, 0, 0, candidate.Offset)
                    .AddDays(1);
                continue;
            }

            if (!_hour.Matches(candidate.Hour))
            {
                // Jump to top of the next hour
                candidate = new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day,
                    candidate.Hour, 0, 0, candidate.Offset).AddHours(1);
                continue;
            }

            if (!_minute.Matches(candidate.Minute))
            {
                candidate = candidate.AddMinutes(1);
                continue;
            }

            return candidate;
        }

        throw new InvalidOperationException(
            "Could not find a next firing time within 4 years for this cron expression.");
    }

    public override string ToString() =>
        $"{_minute} {_hour} {_dom} {_month} {_dow}";

    // ── Inner type ────────────────────────────────────────────────────────────

    private sealed class CronField
    {
        private readonly HashSet<int> _values;

        private CronField(HashSet<int> values) => _values = values;

        public bool Matches(int value) => _values.Contains(value);

        public static CronField Parse(string token, int rangeMin, int rangeMax, string fieldName)
        {
            var values = new HashSet<int>();

            foreach (var part in token.Split(','))
            {
                ParsePart(part.Trim(), rangeMin, rangeMax, fieldName, values);
            }

            return new CronField(values);
        }

        private static void ParsePart(string part, int rangeMin, int rangeMax, string fieldName, HashSet<int> values)
        {
            // step form: <range>/<step>
            int step = 1;
            string rangePart = part;

            int slashIndex = part.IndexOf('/');
            if (slashIndex >= 0)
            {
                string stepStr = part[(slashIndex + 1)..];
                if (!int.TryParse(stepStr, out step) || step < 1)
                    throw new FormatException(
                        $"Invalid step value \"{stepStr}\" in cron {fieldName} field.");
                rangePart = part[..slashIndex];
            }

            int from, to;

            if (rangePart == "*")
            {
                from = rangeMin;
                to   = rangeMax;
            }
            else if (rangePart.Contains('-'))
            {
                var dashParts = rangePart.Split('-');
                if (dashParts.Length != 2
                    || !int.TryParse(dashParts[0], out from)
                    || !int.TryParse(dashParts[1], out to))
                    throw new FormatException(
                        $"Invalid range \"{rangePart}\" in cron {fieldName} field.");

                ValidateBounds(from, rangeMin, rangeMax, fieldName);
                ValidateBounds(to,   rangeMin, rangeMax, fieldName);

                if (from > to)
                    throw new FormatException(
                        $"Range start ({from}) exceeds range end ({to}) in cron {fieldName} field.");
            }
            else
            {
                // Literal number — may optionally be combined with a step (e.g. "5/3" — unusual but valid)
                if (!int.TryParse(rangePart, out from))
                    throw new FormatException(
                        $"Invalid token \"{rangePart}\" in cron {fieldName} field.");

                ValidateBounds(from, rangeMin, rangeMax, fieldName);
                to = (slashIndex >= 0) ? rangeMax : from;
            }

            for (int v = from; v <= to; v += step)
                values.Add(v);
        }

        private static void ValidateBounds(int value, int min, int max, string fieldName)
        {
            if (value < min || value > max)
                throw new FormatException(
                    $"Value {value} is out of range [{min}–{max}] for cron {fieldName} field.");
        }

        public override string ToString() => string.Join(",", _values);
    }
}
