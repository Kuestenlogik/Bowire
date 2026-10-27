// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Interop;

/// <summary>Registers gRPC with JSON transcoding for the Interop service.</summary>
public sealed class BowireInteropServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Both calls are idempotent: an embedded host that runs gRPC of its
        // own keeps its options; transcoding only adds the HTTP routes.
        services.AddGrpc().AddJsonTranscoding();
    }
}

/// <summary>
/// Maps the Interop service inside the workbench's route group, so its gRPC
/// and its transcoded HTTP routes sit under the Bowire base path and behind
/// the same auth gate and workspace scope as every other Bowire endpoint.
/// </summary>
public sealed class BowireInteropEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = string.IsNullOrEmpty(basePath) ? endpoints : endpoints.MapGroup(basePath);
        group.MapGrpcService<InteropGrpcService>();
    }
}
