// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Lighthouse.Tests;

/// <summary>
/// Coverage for <see cref="ProbeSchedule"/> — the interval + window parser and
/// the next-run maths (Decision 1 + 2). Parsing is total: anything unmodellable
/// returns a clear error rather than a silent approximation.
/// </summary>
public sealed class ProbeScheduleTests
{
    [Theory]
    [InlineData("every 60s", 60)]
    [InlineData("every 5m", 5 * 60)]
    [InlineData("every 2h", 2 * 60 * 60)]
    [InlineData("EVERY 30S", 30)]
    public void Parses_interval(string spec, int expectedSeconds)
    {
        Assert.True(ProbeSchedule.TryParse(spec, out var s, out var err));
        Assert.Null(err);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), s!.Interval);
        Assert.Null(s.Window);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hourly")]
    [InlineData("every 5x")]
    [InlineData("every 0s")]
    [InlineData("5m")]
    public void Rejects_unsupported_spec_with_error(string spec)
    {
        Assert.False(ProbeSchedule.TryParse(spec, out var s, out var err));
        Assert.Null(s);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Fact]
    public void Parses_window_with_days()
    {
        Assert.True(ProbeSchedule.TryParse("every 5m, 09:00-17:00 UTC, Mon-Fri", out var s, out _));
        Assert.Equal(TimeSpan.FromMinutes(5), s!.Interval);
        Assert.NotNull(s.Window);
        Assert.Equal(new TimeOnly(9, 0), s.Window!.Start);
        Assert.Equal(new TimeOnly(17, 0), s.Window.End);
        Assert.Contains(DayOfWeek.Monday, s.Window.Days);
        Assert.Contains(DayOfWeek.Friday, s.Window.Days);
        Assert.DoesNotContain(DayOfWeek.Sunday, s.Window.Days);
    }

    [Fact]
    public void Window_without_days_applies_every_day()
    {
        Assert.True(ProbeSchedule.TryParse("every 10m, 00:00-23:59", out var s, out _));
        Assert.NotNull(s!.Window);
        Assert.Equal(7, s.Window!.Days.Count);
    }

    [Theory]
    [InlineData("every 5m, 25:00-26:00 UTC")]
    [InlineData("every 5m, 09:00-17:00 UTC, Funday")]
    public void Rejects_bad_window(string spec)
    {
        Assert.False(ProbeSchedule.TryParse(spec, out _, out var err));
        Assert.False(string.IsNullOrEmpty(err));
    }

    // ---------------------------- next-run maths ----------------------------

    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Never_run_probe_runs_now()
    {
        var s = Parse("every 5m");
        Assert.Equal(Now, s.NextRunAt(lastRun: null, Now));
        Assert.Equal(TimeSpan.Zero, s.DelayUntilNext(null, Now));
    }

    [Fact]
    public void Normal_cadence_is_last_plus_interval()
    {
        var s = Parse("every 5m");
        var last = Now; // just ran
        Assert.Equal(Now.AddMinutes(5), s.NextRunAt(last, Now));
        Assert.Equal(TimeSpan.FromMinutes(5), s.DelayUntilNext(last, Now));
    }

    [Fact]
    public void Missed_ticks_collapse_to_a_single_liveness_run_now()
    {
        var s = Parse("every 5m");
        // Last ran an hour ago → many missed ticks, but the next run is a single
        // catch-up at 'now', not N back-fills.
        var last = Now.AddHours(-1);
        Assert.Equal(Now, s.NextRunAt(last, Now));
        Assert.Equal(TimeSpan.Zero, s.DelayUntilNext(last, Now));
    }

    [Fact]
    public void Window_defers_run_to_next_open_slot()
    {
        // 21:00 UTC, window 09:00-17:00 Mon-Fri → next run is 09:00 the next
        // in-window day.
        var s = Parse("every 5m, 09:00-17:00 UTC, Mon-Sun");
        var evening = new DateTimeOffset(2026, 7, 13, 21, 0, 0, TimeSpan.Zero); // Monday 21:00
        var next = s.NextRunAt(lastRun: null, evening);
        Assert.Equal(new TimeOnly(9, 0), TimeOnly.FromDateTime(next.UtcDateTime));
        Assert.Equal(14, next.Day); // Tuesday
    }

    [Fact]
    public void Inside_window_runs_immediately()
    {
        var s = Parse("every 5m, 09:00-17:00 UTC");
        var midday = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(midday, s.NextRunAt(null, midday));
    }

    private static ProbeSchedule Parse(string spec)
    {
        Assert.True(ProbeSchedule.TryParse(spec, out var s, out _));
        return s!;
    }
}
