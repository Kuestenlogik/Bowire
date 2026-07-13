// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// When a probe runs (#102, Decision 1). v1 accepts an interval
/// (<c>every 60s</c>, <c>every 5m</c>, <c>every 2h</c>) optionally bounded by a
/// UTC time-of-day window and a weekday set (<c>every 5m, 09:00-17:00 UTC,
/// Mon-Fri</c>). Anything the parser can't model returns a clear error rather
/// than silently approximating — the operator sees "unsupported schedule" and
/// the probe simply isn't scheduled. Full cron expressions are a follow-up.
/// </summary>
public sealed partial class ProbeSchedule
{
    private ProbeSchedule(TimeSpan interval, ProbeWindow? window)
    {
        Interval = interval;
        Window = window;
    }

    /// <summary>The base cadence — the gap between successive runs.</summary>
    public TimeSpan Interval { get; }

    /// <summary>Optional bounding window; <c>null</c> means "any time, any day".</summary>
    public ProbeWindow? Window { get; }

    [GeneratedRegex(@"^every\s+(?<n>\d+)\s*(?<unit>s|m|h)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntervalPattern();

    [GeneratedRegex(@"^(?<sh>\d{1,2}):(?<sm>\d{2})\s*-\s*(?<eh>\d{1,2}):(?<em>\d{2})\s*(?:UTC)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowPattern();

    /// <summary>
    /// Parse a schedule spec. Returns <c>false</c> with a human-readable
    /// <paramref name="error"/> when the spec can't be modelled — the caller
    /// logs it and skips scheduling that probe (visible, non-silent).
    /// </summary>
    public static bool TryParse(string? spec, out ProbeSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;
        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "Empty schedule.";
            return false;
        }

        var parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var intervalMatch = IntervalPattern().Match(parts[0]);
        if (!intervalMatch.Success)
        {
            error = $"Unsupported schedule '{spec}': expected 'every <n>s|m|h' (optionally ', HH:mm-HH:mm UTC' and ', Mon-Fri').";
            return false;
        }

        var n = int.Parse(intervalMatch.Groups["n"].Value, CultureInfo.InvariantCulture);
        if (n <= 0)
        {
            error = $"Unsupported schedule '{spec}': interval must be positive.";
            return false;
        }
        var interval = intervalMatch.Groups["unit"].Value.ToUpperInvariant() switch
        {
            "S" => TimeSpan.FromSeconds(n),
            "M" => TimeSpan.FromMinutes(n),
            "H" => TimeSpan.FromHours(n),
            _ => TimeSpan.Zero,
        };

        ProbeWindow? window = null;
        if (parts.Length > 1)
        {
            if (!TryParseWindow(parts.Skip(1).ToArray(), out window, out error))
            {
                return false;
            }
        }

        schedule = new ProbeSchedule(interval, window);
        return true;
    }

    private static bool TryParseWindow(string[] windowParts, out ProbeWindow? window, out string? error)
    {
        window = null;
        error = null;

        TimeOnly? start = null, end = null;
        var days = new HashSet<DayOfWeek>();

        foreach (var part in windowParts)
        {
            var m = WindowPattern().Match(part);
            if (m.Success)
            {
                var sh = int.Parse(m.Groups["sh"].Value, CultureInfo.InvariantCulture);
                var sm = int.Parse(m.Groups["sm"].Value, CultureInfo.InvariantCulture);
                var eh = int.Parse(m.Groups["eh"].Value, CultureInfo.InvariantCulture);
                var em = int.Parse(m.Groups["em"].Value, CultureInfo.InvariantCulture);
                if (sh > 23 || eh > 23 || sm > 59 || em > 59)
                {
                    error = $"Invalid time window '{part}': hours 0-23, minutes 0-59.";
                    return false;
                }
                start = new TimeOnly(sh, sm);
                end = new TimeOnly(eh, em);
                continue;
            }

            // Otherwise treat as a day-range / day-list token (Mon-Fri, Sat, ...).
            if (!TryParseDays(part, days, out error))
            {
                return false;
            }
        }

        if (start is null || end is null)
        {
            error = "Time window requires an 'HH:mm-HH:mm' range.";
            return false;
        }
        if (days.Count == 0)
        {
            // A window with no explicit day list applies every day.
            foreach (var d in Enum.GetValues<DayOfWeek>()) days.Add(d);
        }

        window = new ProbeWindow(start.Value, end.Value, days);
        return true;
    }

    private static readonly string[] DayOrder =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private static bool TryParseDays(string token, HashSet<DayOfWeek> days, out string? error)
    {
        error = null;
        var range = token.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (range.Length == 2)
        {
            var from = IndexOfDay(range[0]);
            var to = IndexOfDay(range[1]);
            if (from < 0 || to < 0)
            {
                error = $"Unknown day range '{token}'.";
                return false;
            }
            // Walk forward from 'from' to 'to' inclusive, wrapping the week.
            for (var i = from; ; i = (i + 1) % 7)
            {
                days.Add((DayOfWeek)i);
                if (i == to) break;
            }
            return true;
        }

        var single = IndexOfDay(token);
        if (single < 0)
        {
            error = $"Unknown day '{token}'.";
            return false;
        }
        days.Add((DayOfWeek)single);
        return true;
    }

    private static int IndexOfDay(string s)
        => Array.IndexOf(DayOrder, s.Trim().ToUpperInvariant());

    /// <summary>
    /// The instant the probe should next run, given its last recorded run and
    /// the current time (Decision 2). A never-run probe (<paramref name="lastRun"/>
    /// null) runs immediately; missed ticks collapse to a single liveness run
    /// at <paramref name="now"/> (no back-fill); a window, when present, advances
    /// the candidate to the next in-window instant.
    /// </summary>
    public DateTimeOffset NextRunAt(DateTimeOffset? lastRun, DateTimeOffset now)
    {
        var candidate = lastRun is { } lr ? lr + Interval : now;
        if (candidate < now) candidate = now; // one liveness run, not N catch-ups
        return Window?.NextWithin(candidate) ?? candidate;
    }

    /// <summary>Non-negative delay from <paramref name="now"/> until the next run.</summary>
    public TimeSpan DelayUntilNext(DateTimeOffset? lastRun, DateTimeOffset now)
    {
        var at = NextRunAt(lastRun, now);
        var delay = at - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}

/// <summary>
/// A UTC time-of-day window plus a weekday set. A probe only fires inside the
/// window; outside it, the next run is deferred to the next in-window instant.
/// </summary>
public sealed class ProbeWindow
{
    public ProbeWindow(TimeOnly start, TimeOnly end, IReadOnlySet<DayOfWeek> days)
    {
        Start = start;
        End = end;
        Days = days;
    }

    public TimeOnly Start { get; }
    public TimeOnly End { get; }
    public IReadOnlySet<DayOfWeek> Days { get; }

    /// <summary>True when <paramref name="instant"/> (in UTC) falls on an allowed day inside [Start, End).</summary>
    public bool Contains(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        if (!Days.Contains(utc.DayOfWeek)) return false;
        var t = TimeOnly.FromDateTime(utc.DateTime);
        return t >= Start && t < End;
    }

    /// <summary>
    /// The earliest in-window instant at or after <paramref name="from"/>.
    /// Scans up to a week ahead; returns <paramref name="from"/> unchanged if no
    /// allowed day exists (defensive — a window always has ≥ 1 day).
    /// </summary>
    public DateTimeOffset NextWithin(DateTimeOffset from)
    {
        var utc = from.ToUniversalTime();
        for (var i = 0; i < 8; i++)
        {
            var day = utc.AddDays(i);
            if (!Days.Contains(day.DayOfWeek)) continue;
            var startAt = new DateTimeOffset(day.Year, day.Month, day.Day, Start.Hour, Start.Minute, 0, TimeSpan.Zero);
            var endAt = new DateTimeOffset(day.Year, day.Month, day.Day, End.Hour, End.Minute, 0, TimeSpan.Zero);
            if (i == 0)
            {
                if (utc >= startAt && utc < endAt) return utc; // already inside today's window
                if (utc < startAt) return startAt;             // before today's window opens
                // utc >= endAt → today's window has closed; fall through to a later day
            }
            else
            {
                return startAt; // first future allowed day, at window open
            }
        }
        return from;
    }
}
