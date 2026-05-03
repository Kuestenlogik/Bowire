// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional extension of <see cref="IBowireProtocol"/> that allows a protocol
/// plugin to register required services and map discovery endpoints automatically.
/// <para>
/// When a user calls <c>builder.Services.AddBowire()</c>, Bowire scans loaded
/// assemblies for <see cref="IBowireProtocol"/> implementations that also
/// implement this interface and calls <see cref="ConfigureServices"/> on each one.
/// Similarly, <c>app.MapBowire()</c> calls <see cref="MapDiscoveryEndpoints"/>.
/// </para>
/// <para>
/// This eliminates the need for protocol-specific boilerplate.  For example, the
/// gRPC plugin uses this to call <c>AddGrpcReflection()</c> and
/// <c>MapGrpcReflectionService()</c> automatically — but only when the gRPC
/// protocol package is actually referenced.
/// </para>
/// </summary>
public interface IBowireProtocolServices
{
    /// <summary>
    /// Register any services this protocol requires for discovery to work.
    /// Called during <c>builder.Services.AddBowire()</c>.
    /// <para>
    /// Implementations should be idempotent — if the user has already called
    /// the underlying registration (e.g. <c>AddGrpcReflection()</c>), calling
    /// it again must be harmless.
    /// </para>
    /// </summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Map any endpoints this protocol needs for auto-discovery.
    /// Called during <c>app.MapBowire()</c>.
    /// <para>
    /// For example, the gRPC plugin maps <c>MapGrpcReflectionService()</c> here.
    /// Return without mapping anything if the protocol doesn't need its own endpoints.
    /// </para>
    /// </summary>
    void MapDiscoveryEndpoints(IEndpointRouteBuilder endpoints) { }
}
