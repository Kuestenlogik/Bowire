// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Workbench-facing read endpoints for the Monitoring rail (#102). The rail
/// is strictly read-only: probes are authored as files and run by
/// <c>bowire monitor run</c> (or an embedded scheduler); the workbench only
/// renders the outcome ledger — live status, per-probe sparkline, and the
/// historical outcome table. Both endpoints read the ledger on request, so
/// the surface stays live against a monitor process appending from another
/// process — no cache, no push channel needed at the probe cadences involved.
/// </summary>
public static class MonitoringEndpoints
{
    /// <summary>
    /// Rows the overview endpoint returns per probe for the sparkline —
    /// enough for a dense strip without shipping GB-scale ledgers wholesale.
    /// </summary>
    internal const int SparklineRows = 60;

    /// <summary>Default (and maximum) rows the detail endpoint returns.</summary>
    internal const int MaxDetailRows = 500;

    public static IEndpointRouteBuilder MapBowireMonitoringEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // GET {basePath}/api/monitoring/probes — every probe in the ledger
        // root with its last outcome + a sparkline tail. One call feeds the
        // sidebar list and the overview cards.
        endpoints.MapGet($"{basePath}/api/monitoring/probes", (HttpContext ctx) =>
        {
            var ledger = ResolveLedger(ctx);
            var probes = ledger.ListProbeNames().Select(name =>
            {
                var history = ledger.ReadOutcomes(name, SparklineRows);
                return new
                {
                    name,
                    last = history.Count > 0 ? history[^1] : null,
                    history,
                };
            });
            return Results.Json(new { probes }, OutcomeLedger.Json);
        }).ExcludeFromDescription();

        // GET {basePath}/api/monitoring/probes/{name}/outcomes?limit=N —
        // the full rows (assertions + error detail) for the outcome table,
        // oldest first. {name} is a ledger file stem; PathFor re-sanitises,
        // so a hostile value can't escape the ledger root.
        endpoints.MapGet($"{basePath}/api/monitoring/probes/{{name}}/outcomes",
            (HttpContext ctx, string name, int? limit) =>
        {
            var ledger = ResolveLedger(ctx);
            var rows = Math.Clamp(limit ?? MaxDetailRows, 1, MaxDetailRows);
            var outcomes = ledger.ReadOutcomes(name, rows);
            return Results.Json(new { name, outcomes }, OutcomeLedger.Json);
        }).ExcludeFromDescription();

        return endpoints;
    }

    // The service contribution registers the default-root ledger, but a
    // host that composed its own container (or swapped the root via
    // AddBowireMonitoring) wins — same resolve-or-default posture as the
    // CLI path.
    private static OutcomeLedger ResolveLedger(HttpContext ctx)
        => ctx.RequestServices.GetService<OutcomeLedger>()
            ?? new OutcomeLedger(MonitoringServiceCollectionExtensions.DefaultLedgerRoot());
}
