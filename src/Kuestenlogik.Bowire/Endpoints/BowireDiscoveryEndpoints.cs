// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the discovery endpoints used by the browser UI to populate the
/// sidebar — the list of registered protocol plugins and the list of
/// services discovered from the configured server URL or uploaded
/// schema files.
/// </summary>
internal static class BowireDiscoveryEndpoints
{
    public static IEndpointRouteBuilder MapBowireDiscoveryEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string prefix)
    {
        // List available protocol plugins (id, name, icon)
        endpoints.MapGet($"/{prefix}/api/protocols", (HttpContext ctx) =>
        {
            var registry = BowireEndpointHelpers.GetRegistry();
            var protocols = registry.Protocols.Select(p => new
            {
                id = p.Id,
                name = p.Name,
                icon = p.IconSvg,
                settings = p.Settings.Select(s => new
                {
                    key = s.Key,
                    label = s.Label,
                    description = s.Description,
                    type = s.Type,
                    defaultValue = s.DefaultValue,
                    options = s.Options?.Select(o => new { value = o.Value, label = o.Label })
                })
            });
            return Results.Json(protocols, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // List all services (proto sources + protocol plugins, merged)
        endpoints.MapGet($"/{prefix}/api/services", async (HttpContext ctx) =>
        {
            var serverUrl = ctx.Request.Query["serverUrl"].FirstOrDefault()
                ?? BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);

            // Standalone tool launched without --url and with no proto
            // uploads / sources to consult: there is genuinely nothing
            // to discover. Returning an empty list immediately keeps the
            // first-run UI snappy — without this the gRPC reflection
            // path tries to handshake with the local Bowire host (which
            // doesn't ship gRPC services), wedges for ~10 s, then fails.
            // The ServerUrls.Count check covers the case where the user
            // passes --url on the command line.
            if (options.Mode == BowireMode.Standalone
                && options.ServerUrls.Count == 0
                && options.ProtoSources.Count == 0
                && !ProtoUploadStore.HasUploads)
            {
                return Results.Json(Array.Empty<BowireServiceInfo>(), BowireEndpointHelpers.JsonOptions);
            }

            // Collect proto-sourced services (code-configured + uploaded). Code-configured
            // protos via options.ProtoSources are not "uploads" — they're the host's own
            // schemas; only ProtoUploadStore entries get the IsUploaded flag.
            var protoServices = new List<BowireServiceInfo>();

            if (options.ProtoSources.Count > 0)
                protoServices.AddRange(ProtoFileParser.ParseAll(options.ProtoSources));

            if (ProtoUploadStore.HasUploads)
            {
                var uploaded = ProtoUploadStore.GetServices();
                foreach (var svc in uploaded) svc.IsUploaded = true;
                protoServices.AddRange(uploaded);
            }

            // Try protocol plugins
            var registry = BowireEndpointHelpers.GetRegistry();
            var allProtocolServices = new List<BowireServiceInfo>();
            var discoveryErrors = new List<string>();

            foreach (var protocol in registry.Protocols)
            {
                try
                {
                    var services = await protocol.DiscoverAsync(serverUrl, options.ShowInternalServices, ctx.RequestAborted);
                    foreach (var svc in services)
                    {
                        svc.Source = protocol.Id;
                        // Tag every service with its origin URL so multi-URL setups
                        // can route invocations back to the right base. Plugins may
                        // have already set this (e.g. REST does); we only fill it in
                        // when missing.
                        svc.OriginUrl ??= serverUrl;
                    }
                    allProtocolServices.AddRange(services);
                }
                catch (Exception ex)
                {
                    BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                        "Discovery failed for protocol {Protocol} at {ServerUrl}",
                        protocol.Name, BowireEndpointHelpers.SafeLog(serverUrl));
                    discoveryErrors.Add($"{protocol.Name}: {ex.Message}");
                }
            }

            // Same for proto-sourced services
            foreach (var svc in protoServices)
                svc.OriginUrl ??= serverUrl;

            if (protoServices.Count > 0 && allProtocolServices.Count > 0)
                return Results.Json(BowireEndpointHelpers.MergeServices(protoServices, allProtocolServices), BowireEndpointHelpers.JsonOptions);

            if (protoServices.Count > 0)
                return Results.Json(protoServices, BowireEndpointHelpers.JsonOptions);

            if (allProtocolServices.Count > 0)
                return Results.Json(allProtocolServices, BowireEndpointHelpers.JsonOptions);

            // No services from any source
            var errorMsg = discoveryErrors.Count > 0
                ? string.Join("; ", discoveryErrors)
                : "No protocol plugins loaded. Upload .proto files or configure ProtoSources.";

            return Results.Json(new { error = errorMsg }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
