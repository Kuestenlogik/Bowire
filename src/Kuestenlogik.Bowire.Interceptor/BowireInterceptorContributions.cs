// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Interceptor;

/// <summary>
/// #325 (v2.1) — discoverable service-registration entry point for the
/// Interceptor package. Picked up by Core's
/// <c>BowireServiceCollectionExtensions.AddBowire</c> assembly scan via
/// the <see cref="IBowireServiceContribution"/> seam, so embedded hosts
/// that reference <c>Kuestenlogik.Bowire.Interceptor</c> get the flow
/// store + mock store + reverse-proxy registry registered automatically
/// — without Core taking a compile-time reference on the moved types.
/// </summary>
public sealed class BowireInterceptorServiceContribution : IBowireServiceContribution
{
    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Flow store + mock store always register so the workbench's
        // Traffic rail can resolve them (and surface an empty state if
        // no host opted the middleware in). Hosts opt the middleware
        // on with app.UseBowireInterceptor() — that's what costs
        // anything per request; these registrations are free.
        services.TryAddSingleton<InterceptedFlowStore>();
        services.TryAddSingleton<InterceptorMockStore>();
        services.AddOptions<BowireInterceptorOptions>();

        // Reverse-proxy registry. Singleton lifetime so the Tools
        // endpoints (start/stop/list) and the ApplicationStopping hook
        // share one source of truth across the host process. The
        // registry's ctor hooks IHostApplicationLifetime so every
        // reverse-proxy host started from the workbench dies when the
        // parent process exits — operators that need a daemon use
        // `bowire proxy` instead.
        services.TryAddSingleton<ReverseProxyRegistry>();
    }
}

/// <summary>
/// #325 (v2.1) — discoverable endpoint-mount entry point for the
/// Interceptor package. Picked up by Core's BowireApiEndpoints scan via
/// the <see cref="IBowireEndpointContribution"/> seam — splices the
/// /api/intercepted/* (+ /api/traffic/* alias) and /api/tools/reverse-
/// proxy/* endpoints into the auth-gated bowireGroup without Core
/// referencing the endpoint types directly.
/// </summary>
public sealed class BowireInterceptorEndpointContribution : IBowireEndpointContribution
{
    /// <inheritdoc />
    public void MapEndpoints(IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapBowireInterceptorEndpoints(basePath);
        endpoints.MapBowireToolsEndpoints(basePath);
    }
}
