// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Kuestenlogik.Bowire.Monitoring;

/// <summary>
/// Discoverable service-registration entry point for the Monitoring
/// workbench surface (#102). Picked up by Core's <c>AddBowire</c> assembly
/// scan via the <see cref="IBowireServiceContribution"/> seam. Registers
/// only the outcome ledger at the default root — the read path the
/// workbench rail needs. The probe engine (runner / scheduler) stays
/// opt-in via <see cref="MonitoringServiceCollectionExtensions.AddBowireMonitoring"/>,
/// which also wins the ledger registration when a host calls it with a
/// custom root before the scan runs.
/// </summary>
public sealed class BowireMonitoringServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(new OutcomeLedger(MonitoringServiceCollectionExtensions.DefaultLedgerRoot()));
    }
}

/// <summary>
/// Discoverable endpoint-mount entry point for the Monitoring workbench
/// surface (#102). Picked up by Core's BowireApiEndpoints scan via the
/// <see cref="IBowireEndpointContribution"/> seam — splices the read-only
/// /api/monitoring/* endpoints into the auth-gated bowireGroup.
/// </summary>
public sealed class BowireMonitoringEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapBowireMonitoringEndpoints(basePath);
    }
}
