// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Telemetry;
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
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        // List available protocol plugins (id, name, icon)
        endpoints.MapGet($"{basePath}/api/protocols", (HttpContext ctx) =>
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
        endpoints.MapGet($"{basePath}/api/services", async (HttpContext ctx) =>
        {
            // In embedded mode the request's host IS the API target — fall
            // back to it when no explicit serverUrl was provided. In
            // standalone mode the host is the workbench itself; falling
            // back probes the workbench's own URL, which the JSON-RPC
            // plugin then "matches" with a phantom stub service (#84).
            // So skip the fallback for standalone — leave serverUrl empty
            // and let the short-circuit fire.
            var rawServerUrl = ctx.Request.Query["serverUrl"].FirstOrDefault()
                ?? (options.Mode == BowireMode.Standalone
                    ? string.Empty
                    : BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request));

            // Optional 'hint@url' form: when present, narrow the
            // plugin loop below to the named plugin only. Saves the
            // ~12 s cost of probing every plugin against a URL the
            // caller already knows belongs to one of them.
            var (pluginHint, serverUrl) = BowireServerUrl.Parse(rawServerUrl);

            // Transport-variant hints (e.g. `grpcweb@`) map to an existing
            // plugin id plus a side-channel metadata entry. DiscoverAsync
            // takes no metadata bag, so we stitch the side-channel onto the
            // URL as a __bowireGrpcTransport=web marker; the gRPC plugin
            // strips it before opening the channel. Plain hints (no
            // transport variant) flow through unchanged.
            if (pluginHint is not null)
            {
                var (mappedId, transportMeta) = BowireEndpointHelpers.ResolveHint(pluginHint);
                pluginHint = mappedId;
                if (transportMeta is { } tm && string.Equals(mappedId, "grpc", StringComparison.OrdinalIgnoreCase))
                {
                    var sep = serverUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                    // grpc plugin's URL marker name — must stay aligned with
                    // GrpcChannelBuilder.TransportUrlMarker. Hard-coded as a
                    // string here so core doesn't take a plugin reference.
                    serverUrl = $"{serverUrl}{sep}__bowireGrpcTransport={Uri.EscapeDataString(tm.Value)}";
                }
            }

            // Standalone tool launched without --url and with no proto
            // uploads / sources to consult AND no runtime URL in the
            // request: there is genuinely nothing to discover. Returning
            // an empty list immediately keeps the first-run UI snappy —
            // without this the gRPC reflection path tries to handshake
            // with the local Bowire host (which doesn't ship gRPC
            // services), wedges for ~10 s, then fails. The serverUrl
            // check covers URLs added at runtime via the sidebar (#82);
            // the ServerUrls.Count check covers --url on the command line.
            if (options.Mode == BowireMode.Standalone
                && options.ServerUrls.Count == 0
                && options.ProtoSources.Count == 0
                && !ProtoUploadStore.HasUploads
                && string.IsNullOrEmpty(serverUrl))
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

            var protocolsToProbe = pluginHint is null
                ? registry.Protocols
                : registry.Protocols.Where(p =>
                    string.Equals(p.Id, pluginHint, StringComparison.OrdinalIgnoreCase));

            // Probe all matching plugins in parallel. Each plugin's
            // DiscoverAsync enforces its own per-probe timeout (HTTP
            // client, MQTT broker connect, gRPC reflection handshake,
            // …). Total wall-clock = max(per-probe) rather than the
            // sum — without this, with 12 bundled plugins probing
            // serially, an arbitrary URL takes ~30 s and blows past
            // the frontend's 12 s abort (#83). A linked CancellationToken
            // with a 10 s ceiling caps any single plugin so one wedge
            // can't drag the whole fanout past the frontend limit.
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
            // 8 s ceiling on any single plugin's probe — frontend
            // aborts the /api/services fetch at 12 s, so we leave a
            // ~4 s margin for the slowest plugin's TCP teardown +
            // JSON serialization of the merged result.
            probeCts.CancelAfter(TimeSpan.FromSeconds(8));
            var probeCt = probeCts.Token;
            var logger = BowireEndpointHelpers.GetLogger(ctx);

            var probeTasks = protocolsToProbe.Select(async protocol =>
            {
                var probeStart = Stopwatch.GetTimestamp();
                string discoverOutcome = "ok";
                int discoveredCount = 0;
                List<BowireServiceInfo> services = [];
                string? errorMessage = null;
                try
                {
                    services = await protocol.DiscoverAsync(serverUrl, options.ShowInternalServices, probeCt);
                    foreach (var svc in services)
                    {
                        svc.Source = protocol.Id;
                        // Tag every service with its origin URL so multi-URL setups
                        // can route invocations back to the right base. Plugins may
                        // have already set this (e.g. REST does); we only fill it in
                        // when missing.
                        svc.OriginUrl ??= serverUrl;
                        discoveredCount++;
                    }
                }
                // Plugin DiscoverAsync calls into third-party transports
                // (HTTP, gRPC reflection, MQTT broker connect, ...) and
                // can throw anything from HttpRequestException to
                // SocketException to plugin-author-defined types. The
                // probe fanout MUST tolerate any one plugin's failure
                // and report it via discoveryErrors instead of poisoning
                // the whole result.
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    discoverOutcome = ex is OperationCanceledException ? "canceled" : "error";
                    logger.LogWarning(ex,
                        "Discovery failed for protocol {Protocol} at {ServerUrl}",
                        protocol.Name, BowireEndpointHelpers.SafeLog(serverUrl));
                    errorMessage = $"{protocol.Name}: {ex.Message}";
                    services = [];
                }
                finally
                {
                    var elapsedMs = (Stopwatch.GetTimestamp() - probeStart)
                        / (double)Stopwatch.Frequency * 1000.0;
                    BowireTelemetry.DiscoverCount.Add(1, new TagList
                    {
                        { "protocol", protocol.Id },
                        { "outcome", discoverOutcome },
                        { "services_found", discoveredCount },
                    });
                    _ = elapsedMs; // wired up if/when we add a discover-duration histogram
                }
                return (services, errorMessage);
            }).ToArray();

            var probeResults = await Task.WhenAll(probeTasks);
            foreach (var (services, errorMessage) in probeResults)
            {
                allProtocolServices.AddRange(services);
                if (errorMessage is not null) discoveryErrors.Add(errorMessage);
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

            // No services from any source — surface as ProblemDetails so
            // the frontend can render the per-plugin failure list as
            // an actionable detail block (#88).
            if (discoveryErrors.Count > 0)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:discovery:no-match",
                    title: "No protocol plugin recognised this URL",
                    status: 502,
                    detail: "Every loaded plugin probed the URL and either returned no services or failed. See `attempts` for the per-plugin error message.",
                    instance: "/api/services",
                    extensions: new Dictionary<string, object?> {
                        ["serverUrl"] = serverUrl,
                        ["attempts"] = discoveryErrors,
                        ["hint"] = "Add a `protocol@` prefix (e.g. `rest@" + serverUrl + "`) to pin a specific plugin and skip the others' probes."
                    });
            }
            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:discovery:no-plugins",
                title: "No protocol plugins are loaded",
                status: 502,
                detail: "Bowire has no protocol plugins available to probe this URL. Upload .proto / OpenAPI / GraphQL SDL files via the Schema Files tab, or configure ProtoSources on the host.",
                instance: "/api/services");
        }).ExcludeFromDescription();

        return endpoints;
    }
}
