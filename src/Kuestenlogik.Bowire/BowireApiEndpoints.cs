// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Top-level entry point for Bowire's REST surface — wires the index
/// HTML route and dispatches to per-feature endpoint extension methods
/// in <see cref="Kuestenlogik.Bowire.Endpoints"/>. This file used to host all
/// 17 endpoints in a single 1000-line block; the per-feature classes
/// (Discovery, Invoke, Channel, Upload, Environment, Auth) are easier
/// to navigate and let related records / helpers stay private to the
/// file that uses them.
/// </summary>
internal static class BowireApiEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, BowireOptions options)
    {
        // Resolve a logger from the host's DI for one-shot startup messages
        // (mostly plugin-load failures from BowireProtocolRegistry). Falls
        // back to NullLogger if the host hasn't registered ILoggerFactory
        // (unusual for ASP.NET apps, but possible in minimal test hosts).
        // Per-request endpoint logging uses BowireEndpointHelpers.GetLogger
        // instead of a static field — see that method for the reasoning.
        var loggerFactory = endpoints.ServiceProvider.GetService<ILoggerFactory>();
        var startupLogger = loggerFactory?.CreateLogger("Kuestenlogik.Bowire") ?? NullLogger.Instance;

        // Discover protocols once at startup and hand the registry to the
        // helper class so every endpoint shares the same cached instance.
        // Plugins receive the app's service provider here so they can
        // resolve their own dependencies later.
        // Honour Bowire:DisabledPlugins (or options.DisabledPlugins
        // populated by the CLI's --disable-plugin flag) so a known-bad
        // plugin can be muted at host startup before its load path
        // ever runs.
        var registry = BowireProtocolRegistry.Discover(options.DisabledPlugins, startupLogger);
        foreach (var protocol in registry.Protocols)
        {
            protocol.Initialize(endpoints.ServiceProvider);

            // Let plugins that implement IBowireProtocolServices map their
            // discovery endpoints (e.g. gRPC Reflection). This is the Map-phase
            // counterpart to AddBowire()'s ConfigureServices calls.
            //
            // Only meaningful in Embedded mode: there, Bowire is mounted
            // inside the customer's gRPC / SignalR / whatever host, and the
            // plugin exposes that host's reflection / hub-list / etc. so the
            // workbench UI can introspect it through bowire's own endpoint.
            // In Standalone mode Bowire is the *client* — there's no host
            // surface to reflect, so calling MapGrpcReflectionService here
            // would just log a spurious "required services not registered"
            // warning on a no-op endpoint.
            if (options.Mode == BowireMode.Embedded && protocol is IBowireProtocolServices setup)
            {
                // Plugin MapDiscoveryEndpoints calls into 3rd-party
                // wiring — unbounded failure surface. A single bad plugin
                // must not abort host startup; log and continue.
#pragma warning disable CA1031 // Do not catch general exception types
                try { setup.MapDiscoveryEndpoints(endpoints); }
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    startupLogger.LogWarning(ex,
                        "Protocol plugin {Plugin} failed to map discovery endpoints",
                        protocol.Id);
                }
            }
        }
        BowireEndpointHelpers.SetRegistry(registry);

        // #294 — Discover the rail + module contributions and merge in
        // any descriptors that the host registered explicitly via
        // services.AddBowireRail<T>() / AddBowireModule<T>(). Built once
        // per host startup (the catalogue is static for the lifetime of
        // the process) and handed to BowireHtmlGenerator so the JS
        // bundle's `__BOWIRE_CONFIG__.rails` / `.modules` seed renders
        // data-driven instead of from the old hardcoded JS arrays.
        var railRegistry = BowireRailRegistry.Discover(startupLogger);
        foreach (var rail in endpoints.ServiceProvider.GetServices<IBowireRailContribution>())
        {
            railRegistry.Register(rail);
        }
        var moduleRegistry = BowireModuleRegistry.Discover(startupLogger);
        foreach (var module in endpoints.ServiceProvider.GetServices<IBowireModuleContribution>())
        {
            moduleRegistry.Register(module);
        }
        BowireHtmlGenerator.SetContributions(railRegistry, moduleRegistry);

        // basePath is the URL fragment all routes are anchored under:
        //   • RoutePrefix = "bowire"  →  basePath = "/bowire"  (embedded default)
        //   • RoutePrefix = ""        →  basePath = ""         (standalone CLI: workbench at root)
        // Pre-computing it once (vs. re-formatting per-route with
        // `$"/{prefix}"`) avoids the `"//api/..."` glitch when the prefix
        // collapses to empty.
        var trimmedPrefix = options.RoutePrefix.TrimStart('/').TrimEnd('/');
        var basePath = trimmedPrefix.Length == 0 ? string.Empty : "/" + trimmedPrefix;

        // Serve the HTML UI at the root of the prefix.
        var uiPath = basePath.Length == 0 ? "/" : basePath;
        endpoints.MapGet(uiPath, (HttpContext ctx) =>
        {
            var html = BowireHtmlGenerator.GenerateIndexHtml(options, ctx.Request);
            return Results.Content(html, "text/html");
        }).ExcludeFromDescription();

        // All Bowire feature endpoints land inside one anonymous-named
        // route group ("") so the auth-provider gate below can apply
        // .RequireAuthorization(...) once and have it propagate to every
        // child route. The HTML UI route (mapped above) is *outside*
        // the group on purpose: the bootstrap HTML has to load before
        // the user can sign in.
        var bowireGroup = endpoints.MapGroup(string.Empty);
        bowireGroup
            .MapBowireDiscoveryEndpoints(options, basePath)
            .MapBowireInvokeEndpoints(options, basePath)
            .MapBowireChannelEndpoints(options, basePath)
            .MapBowireUploadEndpoints(options, basePath)
            .MapBowireEnvironmentEndpoints(options, basePath)
            .MapBowireRecordingEndpoints(options, basePath)
            .MapBowireRecordingSessionEndpoints(basePath)
            .MapBowireCollectionEndpoints(options, basePath)
            .MapBowireParallelEndpoints(basePath)
            .MapBowirePresetEndpoints(basePath)
            .MapBowireAuthEndpoints(options, basePath)
            .MapBowireWorkspaceEndpoints(basePath)
            .MapBowirePluginEndpoints(basePath)
            .MapBowireSemanticsEndpoints(basePath)
            .MapBowireSecurityEndpoints(basePath)
            .MapBowireHelpEndpoints(basePath)
            .MapBowireCatalogueEndpoints(basePath);

        // #325 (v2.1) — Endpoint contributions discovered from sibling
        // packages. The Kuestenlogik.Bowire.Interceptor package
        // contributes /api/intercepted/* (+ /api/traffic/* alias) and
        // /api/tools/reverse-proxy/* via this seam; Core no longer
        // references the interceptor endpoint types directly. The
        // contributions land inside the auth-gated bowireGroup so
        // --token / IBowireAuthProvider gates them automatically.
        DiscoverAndMapEndpointContributions(bowireGroup, basePath, startupLogger);

        // Apply the auth gate exactly once when an IBowireAuthProvider
        // is registered (AddBowireAuth resolved it from the
        // --auth-provider flag / Bowire:Auth:ProviderId config). Without
        // a registered provider Bowire stays open — same as today's
        // laptop default.
        var authProvider = endpoints.ServiceProvider.GetService<IBowireAuthProvider>();
        if (authProvider is not null)
        {
            bowireGroup.RequireAuthorization(BowireAuthPolicies.Default);
            AuthGateLog.GateActive(startupLogger, authProvider.Name, authProvider.Id);
        }
    }

    /// <summary>
    /// #325 — Walk every loaded Kuestenlogik.Bowire.* assembly for
    /// public concrete <see cref="IBowireEndpointContribution"/>
    /// implementations, instantiate each via its parameterless ctor,
    /// and call <see cref="IBowireEndpointContribution.MapEndpoints"/>.
    /// Mirrors the rail / module / protocol-services discovery shape so
    /// sibling packages can splice endpoints into the workbench's auth-
    /// gated group without Core taking a compile-time reference on
    /// their types (the interceptor surface lives in
    /// <c>Kuestenlogik.Bowire.Interceptor</c> since v2.1; Core doesn't
    /// even see those types at build time).
    /// </summary>
    private static void DiscoverAndMapEndpointContributions(
        IEndpointRouteBuilder bowireGroup, string basePath, ILogger startupLogger)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.FullName?.Contains("Bowire", StringComparison.Ordinal) == true))
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                // Cheap guard for CA1873: only build the FullName string
                // when the debug level is actually enabled.
                if (startupLogger.IsEnabled(LogLevel.Debug))
                {
                    startupLogger.LogDebug(ex,
                        "Skipped Bowire assembly during endpoint-contribution scan: {Assembly}",
                        assembly.FullName);
                }
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IBowireEndpointContribution).IsAssignableFrom(type)) continue;
                try
                {
                    if (Activator.CreateInstance(type) is IBowireEndpointContribution contribution)
                    {
                        contribution.MapEndpoints(bowireGroup, basePath);
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    startupLogger.LogWarning(ex,
                        "Endpoint contribution {Type} failed to map", type.FullName);
                }
            }
        }
    }
}

/// <summary>Source-generated logger for the auth-gate startup message.</summary>
internal static partial class AuthGateLog
{
    [LoggerMessage(
        EventId = 100,
        Level = LogLevel.Information,
        Message = "Bowire auth provider active: {Provider} ({Id}). Workbench API requires authentication.")]
    public static partial void GateActive(ILogger logger, string provider, string id);
}
