using Cronos;

namespace Backora.Domain.Scheduling;

public sealed record ScheduleDecision(bool ShouldRun, DateTimeOffset? ScheduledFor, DateTimeOffset? NextRunAt, string Reason);

/// <summary>
/// Pure cron evaluation. Uses Cronos, which follows the standard cron DST semantics: occurrences in a
/// skipped hour (spring forward) run once at the transition and ambiguous hours (fall back) run once.
/// </summary>
public static class SchedulePlanner
{
    public static bool TryParse(string? expression, out CronExpression? cron, out string? error)
    {
        cron = null;
        error = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            error = "Cron expression is empty.";
            return false;
        }
        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "Cron expression must have exactly five fields (minute hour day-of-month month day-of-week).";
            return false;
        }
        try
        {
            cron = CronExpression.Parse(string.Join(' ', fields), CronFormat.Standard);
            return true;
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryGetTimeZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException) { return false; }
        catch (InvalidTimeZoneException) { return false; }
    }

    public static DateTimeOffset? NextOccurrence(string expression, string timeZone, DateTimeOffset afterUtc)
    {
        if (!TryParse(expression, out var cron, out _)) return null;
        TryGetTimeZone(timeZone, out var zone);
        var next = cron!.GetNextOccurrence(afterUtc.UtcDateTime, zone, inclusive: false);
        return next is null ? null : new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc));
    }

    public static IReadOnlyList<DateTimeOffset> NextOccurrences(string expression, string timeZone, DateTimeOffset afterUtc, int count)
    {
        var list = new List<DateTimeOffset>(count);
        var cursor = afterUtc;
        for (var i = 0; i < count; i++)
        {
            var next = NextOccurrence(expression, timeZone, cursor);
            if (next is null) break;
            list.Add(next.Value);
            cursor = next.Value;
        }
        return list;
    }

    /// <summary>
    /// Decides whether a job is due. <paramref name="nextRunAt"/> is the persisted next fire time.
    /// A fire time that is overdue beyond the grace window is treated as missed (skipped) under
    /// <see cref="MissedRunPolicy.RunOnceWithinGrace"/>; with <see cref="MissedRunPolicy.Skip"/>, any
    /// occurrence older than two minutes is skipped. Multiple missed occurrences collapse into one run.
    /// </summary>
    public static ScheduleDecision Evaluate(
        string expression,
        string timeZone,
        DateTimeOffset? nextRunAt,
        DateTimeOffset nowUtc,
        MissedRunPolicy policy,
        int graceMinutes)
    {
        if (nextRunAt is null)
        {
            return new ScheduleDecision(false, null, NextOccurrence(expression, timeZone, nowUtc), "initialized");
        }

        if (nextRunAt > nowUtc)
        {
            return new ScheduleDecision(false, null, nextRunAt, "not-due");
        }

        var lateness = nowUtc - nextRunAt.Value;
        var tolerance = policy == MissedRunPolicy.Skip ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(Math.Max(1, graceMinutes));
        var upcoming = NextOccurrence(expression, timeZone, nowUtc);

        if (lateness > tolerance)
        {
            return new ScheduleDecision(false, nextRunAt, upcoming, $"missed by {lateness:g}");
        }

        return new ScheduleDecision(true, nextRunAt, upcoming, "due");
    }

    /// <summary>Human readable description used by the UI (e.g. "Daily at 02:00 (Europe/Berlin)").</summary>
    public static string Describe(string? expression, string timeZone)
    {
        if (string.IsNullOrWhiteSpace(expression)) return "Manual only";
        var f = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length != 5) return expression;
        var tz = timeZone == "UTC" ? "UTC" : timeZone;
        bool IsNum(string s) => int.TryParse(s, out _);
        if (IsNum(f[0]) && IsNum(f[1]))
        {
            var time = $"{int.Parse(f[1]):00}:{int.Parse(f[0]):00}";
            if (f[2] == "*" && f[3] == "*" && f[4] == "*") return $"Daily at {time} ({tz})";
            if (f[2] == "*" && f[3] == "*" && IsNum(f[4]))
            {
                string[] days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];
                var d = int.Parse(f[4]);
                if (d is >= 0 and <= 7) return $"Every {days[d]} at {time} ({tz})";
            }
            if (IsNum(f[2]) && f[3] == "*" && f[4] == "*") return $"Monthly on day {f[2]} at {time} ({tz})";
        }
        if (f[0].StartsWith("*/", StringComparison.Ordinal) && f[1] == "*" && f[2] == "*" && f[3] == "*" && f[4] == "*")
            return $"Every {f[0][2..]} minutes";
        if (IsNum(f[0]) && f[1] == "*" && f[2] == "*" && f[3] == "*" && f[4] == "*") return $"Hourly at :{int.Parse(f[0]):00}";
        if (IsNum(f[0]) && f[1].StartsWith("*/", StringComparison.Ordinal) && f[2] == "*" && f[3] == "*" && f[4] == "*")
            return $"Every {f[1][2..]} hours at :{int.Parse(f[0]):00} ({tz})";
        return $"{expression} ({tz})";
    }
}
