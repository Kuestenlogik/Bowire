// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Auth;

/// <summary>
/// Wires the Bowire auth seam into the host's
/// <see cref="IServiceCollection"/>. Pairs with the
/// <c>.RequireAuthorization(BowireAuthPolicies.Default)</c> calls
/// inside <c>BowireApiEndpoints.Map</c>.
/// </summary>
public static class BowireAuthServiceCollectionExtensions
{
    /// <summary>
    /// Register Bowire's auth seam. Reads <c>Bowire:Auth</c> from
    /// <paramref name="configuration"/> for the active provider id;
    /// optional <paramref name="configure"/> wins over the config
    /// file. When no provider is selected, registers a
    /// <c>null</c>-valued <see cref="IBowireAuthProvider"/> sentinel
    /// so <c>BowireApiEndpoints.Map</c> can branch without throwing.
    /// </summary>
    public static IServiceCollection AddBowireAuth(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BowireAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var opts = new BowireAuthOptions();
        configuration.GetSection("Bowire:Auth").Bind(opts);
        configure?.Invoke(opts);

        services.TryAddSingleton(opts);

        // Register the authentication + authorization scheme services
        // unconditionally so the pipeline's app.UseAuthentication() /
        // UseAuthorization() always find what they need, even when no
        // provider is active. The Default policy below is a no-op (no
        // requirements) until a provider takes over —
        // BowireApiEndpoints.Map only calls .RequireAuthorization when
        // a provider was actually registered.
        //
        // The AddAuthentication() call is load-bearing: without it,
        // app.UseAuthentication() fails to build the middleware with
        // 'Unable to resolve service for type
        // IAuthenticationSchemeProvider'. ASP.NET's
        // no-op-when-no-schemes claim only holds once the scheme
        // provider itself is registered.
        services.AddAuthentication();
        services.AddAuthorization(o =>
        {
            o.AddPolicy(BowireAuthPolicies.Default, p => p.RequireAssertion(_ => true));
        });

        // Provider lookup needs a logger; pull a transient one out of
        // the services that are already registered. Failure here is
        // fatal — operators that pass --auth-provider X should get a
        // clear error at startup, not a silent fallback to "no auth".
        var loggerFactory = services
            .Where(d => d.ServiceType == typeof(ILoggerFactory))
            .Select(d => d.ImplementationInstance as ILoggerFactory)
            .FirstOrDefault();
        var logger = loggerFactory?.CreateLogger("Kuestenlogik.Bowire.Auth");

        BowireAuthProviderRegistry.ApplyAuthentication(services, configuration, opts, logger);
        return services;
    }
}
