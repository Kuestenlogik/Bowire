// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Benchmarking;

/// <summary>
/// Registers the scheduling backbone (#232). Discovered by Core's service
/// contribution scan, so referencing the Benchmarking package is all a host
/// has to do to get scheduled runs — and a host that doesn't reference it
/// starts no scheduler at all.
/// </summary>
public sealed class BowireBenchmarkSchedulingServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<BowireBenchmarkScheduleStore>(_ => new BowireBenchmarkScheduleStore());
        services.AddSingleton<IBowireBenchmarkProtocolResolver, BowireRegistryProtocolResolver>();
        services.AddHostedService<BowireBenchmarkSchedulingHostedService>();
    }
}

/// <summary>
/// Resolves scheduled runs' protocol plugins through the process-wide
/// protocol registry — the same set the workbench and CLI discover.
/// </summary>
public sealed class BowireRegistryProtocolResolver : IBowireBenchmarkProtocolResolver
{
    /// <inheritdoc />
    public IBowireProtocol? Resolve(string protocolId)
    {
        if (string.IsNullOrWhiteSpace(protocolId)) return null;
        var registry = BowireProtocolRegistry.Discover();
        return registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, protocolId, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Mounts the schedule API for the workbench (#232).
/// </summary>
public sealed class BowireBenchmarkSchedulingEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapBowireBenchmarkScheduleEndpoints(basePath);
    }
}

/// <summary>Pause / resume body for the schedule endpoint.</summary>
/// <param name="Enabled">Whether the schedule should fire.</param>
public sealed record BowireScheduleEnabledRequest(bool Enabled);

/// <summary>The benchmark-schedule HTTP surface.</summary>
public static class BowireBenchmarkScheduleEndpoints
{
    /// <summary>
    /// Map the schedule endpoints under <paramref name="basePath"/>:
    /// list them with their next firing time, and pause / resume one.
    /// <para>
    /// Deliberately no "create" endpoint yet: a schedule carries a target
    /// URL the server will call unattended, so authoring one belongs on the
    /// CLI where the operator is explicit about it, while the workbench
    /// reads and pauses. That keeps an unattended outbound call from being
    /// two clicks away in a browser.
    /// </para>
    /// </summary>
    public static IEndpointRouteBuilder MapBowireBenchmarkScheduleEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet($"{basePath}/api/benchmarks/schedules",
            async (HttpContext http, CancellationToken ct) =>
            {
                var store = ResolveStore(http);
                var now = DateTime.UtcNow;
                var schedules = await store.LoadAllAsync(ct).ConfigureAwait(false);
                var payload = new List<object>(schedules.Count);
                foreach (var schedule in schedules)
                {
                    var runs = await store.LoadRunsAsync(schedule.Id, ct).ConfigureAwait(false);
                    payload.Add(ToPayload(schedule, runs, now));
                }
                return Results.Ok(payload);
            })
            .ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/benchmarks/schedules/{{id}}/enabled",
            async (string id, BowireScheduleEnabledRequest body, HttpContext http, CancellationToken ct) =>
            {
                var store = ResolveStore(http);
                var schedule = await store.LoadAsync(id, ct).ConfigureAwait(false);
                if (schedule is null) return Results.NotFound();

                schedule.Enabled = body?.Enabled ?? true;
                await store.SaveAsync(schedule, ct).ConfigureAwait(false);

                var runs = await store.LoadRunsAsync(schedule.Id, ct).ConfigureAwait(false);
                return Results.Ok(ToPayload(schedule, runs, DateTime.UtcNow));
            })
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// The store for this request. Taken from DI when the host registered the
    /// scheduling services, otherwise constructed on the spot.
    /// <para>
    /// Deliberately NOT a handler parameter: a host that calls
    /// <c>MapBowire()</c> without the scheduling service registration would
    /// leave the type unknown to minimal-API binding, which then treats it as
    /// a body parameter and throws while mapping — taking down the whole
    /// route group, index page included. Resolving it here keeps the endpoint
    /// self-sufficient, which is also what a rail package owes an embedded
    /// host: degrade, never break the pane.
    /// </para>
    /// </summary>
    private static BowireBenchmarkScheduleStore ResolveStore(HttpContext http)
        => http.RequestServices.GetService(typeof(BowireBenchmarkScheduleStore)) as BowireBenchmarkScheduleStore
            ?? new BowireBenchmarkScheduleStore();

    /// <summary>
    /// Wire shape for the rail: the schedule, when it fires next, and the
    /// last run's headline so the list answers "is this healthy?" without a
    /// second request.
    /// </summary>
    public static object ToPayload(
        BowireBenchmarkSchedule schedule, IReadOnlyList<BowireBenchmarkScheduleRun> runs, DateTime nowUtc)
    {
        var last = runs.Count > 0 ? runs[0] : null;
        return new
        {
            id = schedule.Id,
            name = schedule.Name,
            cron = schedule.Cron,
            timezone = string.IsNullOrWhiteSpace(schedule.Timezone) ? "UTC" : schedule.Timezone,
            enabled = schedule.Enabled,
            target = $"{schedule.Service}/{schedule.Method}",
            serverUrl = schedule.ServerUrl,
            iterations = schedule.Iterations,
            concurrency = schedule.Concurrency,
            thresholds = schedule.Thresholds,
            // Null while paused or when the cron doesn't parse — the UI
            // shows "paused" / "invalid" rather than inventing a time.
            nextRun = schedule.NextOccurrenceUtc(nowUtc),
            lastRun = last is null ? null : new
            {
                startedAt = last.StartedAt,
                triggeredBy = last.TriggeredBy,
                p50 = last.P50,
                p95 = last.P95,
                p99 = last.P99,
                errors = last.Errors,
                count = last.Count,
                throughput = last.Throughput,
                passed = last.Passed,
                thresholds = last.Thresholds.Select(t => new { spec = t.Spec, actual = t.Actual, ok = t.Ok }),
            },
            runCount = runs.Count,
        };
    }
}
