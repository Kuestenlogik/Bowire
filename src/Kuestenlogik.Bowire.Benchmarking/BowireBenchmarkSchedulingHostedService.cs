// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>
/// Fires scheduled benchmarks (#232).
/// <para>
/// Holds no schedule state of its own: it re-reads the store on every tick,
/// which is what makes a schedule survive a restart (the entry is on disk,
/// not in memory) and also means an operator adding, pausing or deleting a
/// schedule takes effect without a bounce.
/// </para>
/// <para>
/// A run is due when its next cron occurrence has passed since the last time
/// this service looked. Comparing against the schedule's own last-run
/// timestamp — rather than an in-memory timer — keeps a missed window (host
/// asleep, deploy in progress) from silently swallowing the run, and keeps a
/// restart from re-firing one that already happened.
/// </para>
/// </summary>
public sealed class BowireBenchmarkSchedulingHostedService : BackgroundService
{
    /// <summary>ActivitySource scheduled runs are reported on (#29 / #102).</summary>
    public static readonly ActivitySource ActivitySource = new("Kuestenlogik.Bowire.Benchmarking");

    private readonly BowireBenchmarkScheduleStore _store;
    private readonly IBowireBenchmarkProtocolResolver _protocols;
    private readonly ILogger<BowireBenchmarkSchedulingHostedService> _logger;
    private readonly TimeSpan _tick;

    /// <summary>Create the service.</summary>
    /// <param name="store">Where schedules and their history live.</param>
    /// <param name="protocols">Resolves a protocol plugin by id at firing time.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="tickInterval">How often to look for due schedules; defaults to 30s.</param>
    public BowireBenchmarkSchedulingHostedService(
        BowireBenchmarkScheduleStore store,
        IBowireBenchmarkProtocolResolver protocols,
        ILogger<BowireBenchmarkSchedulingHostedService> logger,
        TimeSpan? tickInterval = null)
    {
        _store = store;
        _protocols = protocols;
        _logger = logger;
        _tick = tickInterval ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_tick);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A scheduler that dies on one bad tick stops every schedule.
                BenchmarkSchedulingLog.TickFailed(_logger, ex);
            }

            try { await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One scheduler pass: run everything that came due at or before
    /// <paramref name="nowUtc"/>. Public so a test can drive it directly
    /// rather than waiting on wall-clock ticks.
    /// </summary>
    public async Task<int> TickAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var schedules = await _store.LoadAllAsync(ct).ConfigureAwait(false);
        var fired = 0;

        foreach (var schedule in schedules)
        {
            ct.ThrowIfCancellationRequested();
            if (!schedule.Enabled) continue;

            if (!schedule.TryGetCronExpression(out _, out var cronError))
            {
                BenchmarkSchedulingLog.BadCron(_logger, schedule.Id, cronError ?? "unparseable");
                continue;
            }

            var runs = await _store.LoadRunsAsync(schedule.Id, ct).ConfigureAwait(false);
            // The reference point is the last run we recorded; for a brand-new
            // schedule it is the moment we first see it, so adding a schedule
            // does not immediately fire one for every occurrence since the
            // epoch.
            var since = runs.Count > 0 ? runs[0].StartedAt : nowUtc;
            var due = schedule.NextOccurrenceUtc(since);
            if (due is null || due > nowUtc) continue;

            await RunAsync(schedule, nowUtc, "schedule", ct).ConfigureAwait(false);
            fired++;
        }

        return fired;
    }

    /// <summary>
    /// Execute one schedule now and record the result. <paramref name="triggeredBy"/>
    /// distinguishes a cron firing from an operator pressing Run.
    /// </summary>
    public async Task<BowireBenchmarkScheduleRun?> RunAsync(
        BowireBenchmarkSchedule schedule, DateTime startedAtUtc, string triggeredBy, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var protocol = _protocols.Resolve(schedule.Protocol);
        if (protocol is null)
        {
            BenchmarkSchedulingLog.NoProtocol(_logger, schedule.Id, schedule.Protocol);
            return null;
        }

        using var activity = ActivitySource.StartActivity("bowire.benchmark.scheduled", ActivityKind.Client);
        activity?.SetTag("bowire.schedule.id", schedule.Id);
        activity?.SetTag("bowire.schedule.trigger", triggeredBy);
        activity?.SetTag("bowire.target.service", schedule.Service);
        activity?.SetTag("bowire.target.method", schedule.Method);

        BowireBenchmarkRun result;
        try
        {
            result = await BowireBenchmarkRunner.RunAsync(protocol, schedule.ToRequest(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One schedule failing outright must not take the scheduler down.
            BenchmarkSchedulingLog.RunFailed(_logger, schedule.Id, ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return null;
        }

        var thresholds = new List<BowireBenchmarkScheduleThreshold>();
        var passed = true;
        foreach (var spec in schedule.Thresholds)
        {
            if (!BowireBenchmarkThreshold.TryParse(spec, out var threshold, out var error))
            {
                BenchmarkSchedulingLog.BadThreshold(_logger, schedule.Id, spec, error ?? "unparseable");
                continue;
            }
            var verdict = threshold!.Evaluate(result.Stats);
            thresholds.Add(new BowireBenchmarkScheduleThreshold
            {
                Spec = threshold.ToString(),
                Actual = verdict.Actual,
                Ok = verdict.Ok,
            });
            if (!verdict.Ok) passed = false;
        }

        var run = new BowireBenchmarkScheduleRun
        {
            ScheduleId = schedule.Id,
            StartedAt = startedAtUtc,
            TriggeredBy = triggeredBy,
            DurationMs = result.ElapsedMs,
            Count = result.Stats.Count,
            Errors = result.Stats.Errors,
            P50 = result.Stats.P50,
            P95 = result.Stats.P95,
            P99 = result.Stats.P99,
            Throughput = result.Stats.Throughput,
            Passed = passed,
            FirstError = result.FirstError,
        };
        run.Thresholds.AddRange(thresholds);

        activity?.SetTag("bowire.benchmark.p95", result.Stats.P95);
        activity?.SetTag("bowire.benchmark.error_rate", result.Stats.ErrorRate);
        activity?.SetTag("bowire.benchmark.throughput", result.Stats.Throughput);
        activity?.SetTag("bowire.benchmark.passed", passed);
        if (!passed) activity?.SetStatus(ActivityStatusCode.Error, "threshold breached");

        await _store.AppendRunAsync(run, ct).ConfigureAwait(false);
        BenchmarkSchedulingLog.Fired(_logger, schedule.Id, result.Stats.P95, passed);
        return run;
    }
}

/// <summary>
/// Resolves a protocol plugin by id for the scheduler. An interface rather
/// than a direct registry call so the hosted service can be driven in a test
/// without a plugin scan, and so a host can narrow which protocols scheduled
/// runs may reach.
/// </summary>
public interface IBowireBenchmarkProtocolResolver
{
    /// <summary>The plugin with this id, or null when it isn't loaded.</summary>
    IBowireProtocol? Resolve(string protocolId);
}

internal static partial class BenchmarkSchedulingLog
{
    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning,
        Message = "Benchmark scheduler tick failed.")]
    public static partial void TickFailed(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning,
        Message = "Schedule '{ScheduleId}' has an unusable cron expression: {Error}")]
    public static partial void BadCron(ILogger logger, string scheduleId, string error);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Warning,
        Message = "Schedule '{ScheduleId}' targets protocol '{Protocol}', which is not loaded.")]
    public static partial void NoProtocol(ILogger logger, string scheduleId, string protocol);

    [LoggerMessage(EventId = 3204, Level = LogLevel.Warning,
        Message = "Scheduled benchmark '{ScheduleId}' failed to run.")]
    public static partial void RunFailed(ILogger logger, string scheduleId, Exception ex);

    [LoggerMessage(EventId = 3205, Level = LogLevel.Warning,
        Message = "Schedule '{ScheduleId}' has an unusable threshold '{Spec}': {Error}")]
    public static partial void BadThreshold(ILogger logger, string scheduleId, string spec, string error);

    [LoggerMessage(EventId = 3206, Level = LogLevel.Information,
        Message = "Scheduled benchmark '{ScheduleId}' ran: p95 {P95}ms, passed={Passed}.")]
    public static partial void Fired(ILogger logger, string scheduleId, double p95, bool passed);
}
