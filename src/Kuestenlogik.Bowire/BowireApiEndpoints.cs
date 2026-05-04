// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Endpoints;
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
            if (protocol is IBowireProtocolServices setup)
            {
                try { setup.MapDiscoveryEndpoints(endpoints); }
                catch (Exception ex)
                {
                    startupLogger.LogWarning(ex,
                        "Protocol plugin {Plugin} failed to map discovery endpoints",
                        protocol.Id);
                }
            }
        }
        BowireEndpointHelpers.SetRegistry(registry);

        var prefix = options.RoutePrefix.TrimStart('/').TrimEnd('/');

        // Serve the HTML UI at the root of the prefix
        endpoints.MapGet($"/{prefix}", (HttpContext ctx) =>
        {
            var html = BowireHtmlGenerator.GenerateIndexHtml(options, ctx.Request);
            return Results.Content(html, "text/html");
        }).ExcludeFromDescription();

        endpoints
            .MapBowireDiscoveryEndpoints(options, prefix)
            .MapBowireInvokeEndpoints(options, prefix)
            .MapBowireChannelEndpoints(options, prefix)
            .MapBowireUploadEndpoints(options, prefix)
            .MapBowireEnvironmentEndpoints(options, prefix)
            .MapBowireRecordingEndpoints(options, prefix)
            .MapBowireAuthEndpoints(options, prefix)
            .MapBowireWorkspaceEndpoints(prefix)
            .MapBowirePluginEndpoints(prefix);
    }
}
