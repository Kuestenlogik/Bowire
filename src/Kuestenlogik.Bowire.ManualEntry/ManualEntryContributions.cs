// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.ManualEntry;

/// <summary>Registers gRPC with JSON transcoding for the ManualEntry service.</summary>
public sealed class BowireManualEntryServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Idempotent, as for Interop: a host that runs gRPC of its own keeps
        // its options; transcoding only adds the HTTP routes.
        services.AddGrpc().AddJsonTranscoding();
    }
}

/// <summary>
/// Maps the ManualEntry service inside the workbench's route group, behind
/// the same base path and auth gate as every other Bowire endpoint.
/// </summary>
public sealed class BowireManualEntryEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = string.IsNullOrEmpty(basePath) ? endpoints : endpoints.MapGroup(basePath);
        group.MapGrpcService<ManualEntryGrpcService>();
    }
}
