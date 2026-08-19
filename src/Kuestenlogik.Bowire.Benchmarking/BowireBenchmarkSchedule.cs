// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;
using Cronos;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>
/// A benchmark that fires on a cron schedule (#232).
/// <para>
/// The schedule carries the whole request rather than pointing at a
/// workbench envelope on purpose: envelopes live in the browser's
/// localStorage, and a hosted service running in the server process cannot
/// read those. Persisting what to call — and how hard — next to the cron
/// expression is what lets a schedule survive a restart and run with no
/// operator present, which is the point of the ticket.
/// </para>
/// </summary>
public sealed class BowireBenchmarkSchedule
{
    /// <summary>Stable id — the file name in the store and the CLI / API handle.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Human-readable name for the workbench list.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Standard 5-field cron expression (minute hour day month day-of-week).</summary>
    [JsonPropertyName("cron")]
    public string Cron { get; set; } = "";

    /// <summary>
    /// IANA timezone id the cron expression is interpreted in (e.g.
    /// <c>Europe/Berlin</c>). Empty means UTC — never the host's local zone,
    /// which would make the same schedule fire at different wall-clock times
    /// on a developer laptop and a CI runner.
    /// </summary>
    [JsonPropertyName("timezone")]
    public string Timezone { get; set; } = "";

    /// <summary>Whether the schedule fires. A paused schedule is kept, not deleted.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Target server URL.</summary>
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "";

    /// <summary>Protocol plugin id (rest, grpc, …).</summary>
    [JsonPropertyName("protocol")]
    public string Protocol { get; set; } = "";

    /// <summary>Service to call.</summary>
    [JsonPropertyName("service")]
    public string Service { get; set; } = "";

    /// <summary>Method to call.</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    /// <summary>Request body, if the method takes one.</summary>
    [JsonPropertyName("body")]
    public string? Body { get; set; }

    /// <summary>Calls per run.</summary>
    [JsonPropertyName("iterations")]
    public int Iterations { get; set; } = 50;

    /// <summary>Calls in flight at once.</summary>
    [JsonPropertyName("concurrency")]
    public int Concurrency { get; set; } = 1;

    /// <summary>Calls made and discarded before measuring.</summary>
    [JsonPropertyName("warmup")]
    public int Warmup { get; set; }

    /// <summary>Threshold specs (<c>p95 &lt; 200</c>) evaluated after each run.</summary>
    [JsonPropertyName("thresholds")]
    public List<string> Thresholds { get; init; } = [];

    /// <summary>
    /// Parse <see cref="Cron"/>, returning false with a message rather than
    /// throwing — a bad expression in a stored file must not take down the
    /// hosted service on boot.
    /// </summary>
    public bool TryGetCronExpression(out CronExpression? expression, out string? error)
    {
        expression = null;
        error = null;
        if (string.IsNullOrWhiteSpace(Cron))
        {
            error = "empty cron expression";
            return false;
        }
        try
        {
            expression = CronExpression.Parse(Cron.Trim());
            return true;
        }
        catch (CronFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Resolve <see cref="Timezone"/>, falling back to UTC when it is empty
    /// or the host doesn't know the id — an unknown zone should shift the
    /// firing time, not stop the schedule.
    /// </summary>
    public TimeZoneInfo ResolveTimeZone()
    {
        if (string.IsNullOrWhiteSpace(Timezone)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(Timezone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// Next firing time after <paramref name="afterUtc"/>, or null when the
    /// schedule is disabled or its expression doesn't parse. Cronos handles
    /// the DST cases — a 02:30 daily schedule has no occurrence on a
    /// spring-forward night and two on the way back.
    /// </summary>
    public DateTime? NextOccurrenceUtc(DateTime afterUtc)
    {
        if (!Enabled) return null;
        if (!TryGetCronExpression(out var expression, out _)) return null;
        return expression!.GetNextOccurrence(
            new DateTimeOffset(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc)),
            ResolveTimeZone())?.UtcDateTime;
    }

    /// <summary>Project the schedule onto a runner request.</summary>
    public BowireBenchmarkRequest ToRequest() => new()
    {
        ServerUrl = ServerUrl,
        Service = Service,
        Method = Method,
        Body = Body,
        Iterations = Iterations,
        Concurrency = Concurrency,
        Warmup = Warmup,
    };
}

/// <summary>One recorded execution of a scheduled benchmark (#232).</summary>
public sealed class BowireBenchmarkScheduleRun
{
    /// <summary>Schedule this run belongs to.</summary>
    [JsonPropertyName("scheduleId")]
    public string ScheduleId { get; set; } = "";

    /// <summary>When the run started (UTC).</summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    /// <summary>
    /// What caused the run — <c>schedule</c> for a cron firing, <c>manual</c>
    /// for an operator pressing Run. Kept so a history entry that looks
    /// surprising can be traced to its trigger.
    /// </summary>
    [JsonPropertyName("triggeredBy")]
    public string TriggeredBy { get; set; } = "schedule";

    /// <summary>Wall-clock duration of the run.</summary>
    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    /// <summary>Completed calls.</summary>
    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>Failed calls.</summary>
    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    /// <summary>Median latency, milliseconds.</summary>
    [JsonPropertyName("p50")]
    public double P50 { get; set; }

    /// <summary>95th-percentile latency, milliseconds.</summary>
    [JsonPropertyName("p95")]
    public double P95 { get; set; }

    /// <summary>99th-percentile latency, milliseconds.</summary>
    [JsonPropertyName("p99")]
    public double P99 { get; set; }

    /// <summary>Completed calls per second.</summary>
    [JsonPropertyName("throughput")]
    public double Throughput { get; set; }

    /// <summary>Threshold verdicts, newest run's own budgets.</summary>
    [JsonPropertyName("thresholds")]
    public List<BowireBenchmarkScheduleThreshold> Thresholds { get; init; } = [];

    /// <summary>True when every threshold held.</summary>
    [JsonPropertyName("passed")]
    public bool Passed { get; set; } = true;

    /// <summary>First error observed, if any.</summary>
    [JsonPropertyName("firstError")]
    public string? FirstError { get; set; }
}

/// <summary>A threshold verdict recorded with a scheduled run.</summary>
public sealed class BowireBenchmarkScheduleThreshold
{
    /// <summary>The budget as written.</summary>
    [JsonPropertyName("spec")]
    public string Spec { get; set; } = "";

    /// <summary>What the run measured.</summary>
    [JsonPropertyName("actual")]
    public double Actual { get; set; }

    /// <summary>Whether the run stayed within budget.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }
}
