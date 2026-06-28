// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Plugins;

/// <summary>
/// Contract a Bowire-sibling package implements to register its services
/// into the host's <see cref="IServiceCollection"/> at
/// <c>AddBowire()</c> time (#325, v2.1).
/// </summary>
/// <remarks>
/// <para>
/// Introduced when Welle 2 of the v2.1 package cleanup lifted the
/// interceptor surface out of Core into
/// <c>Kuestenlogik.Bowire.Interceptor</c>. Core's
/// <c>BowireServiceCollectionExtensions.AddBowire</c> no longer calls
/// the interceptor's <c>AddBowireInterceptorCore</c> directly — it
/// discovers every public implementation of this interface from loaded
/// <c>Kuestenlogik.Bowire.*</c> assemblies and invokes
/// <see cref="ConfigureServices"/> on each.
/// </para>
/// <para>
/// Implementations must have a parameterless constructor;
/// implementations are expected to be idempotent (TryAddSingleton
/// where appropriate) because a host that explicitly opted in via
/// <c>app.UseBowireInterceptor()</c> already registered the supporting
/// services.
/// </para>
/// <para>
/// Failure to configure (any exception escaping
/// <see cref="ConfigureServices"/>) is swallowed silently — same
/// posture as the <c>IBowireProtocolServices</c> auto-discovery pass.
/// A misbehaving plugin can't take down host startup.
/// </para>
/// </remarks>
public interface IBowireServiceContribution
{
    /// <summary>
    /// Register this contribution's services. Called once per host
    /// startup from <c>BowireServiceCollectionExtensions.AddBowire</c>.
    /// </summary>
    /// <param name="services">The host's service collection.</param>
    void ConfigureServices(IServiceCollection services);
}
