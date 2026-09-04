// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

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

            // Opt-in success envelope (#544). A *successful* discovery can
            // now carry a diagnostic — `partial` means "services came back
            // AND something faulted" — but the 200 body has always been a
            // bare BowireServiceInfo[], so there was nowhere to put it and
            // the outcome was unobservable over HTTP. `?includeAttempts=1`
            // switches the body to { services, attempts }; without the flag
            // the array ships exactly as before, so no existing consumer
            // moves. The probe is stateless, so "fetch the attempts
            // afterwards" is not an option — it would mean probing twice.
            var includeAttempts = IsTruthy(ctx.Request.Query["includeAttempts"].FirstOrDefault());

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
                    serverUrl = $"{serverUrl}{sep}{BowireMetadataKeys.GrpcTransport}={Uri.EscapeDataString(tm.Value)}";
                }

                // Same side-channel idea for SSE: DiscoverAsync has no
                // "was I explicitly hinted?" parameter, but the plugin's
                // ad-hoc separate-target fallback must ONLY fire for
                // `sse@…` — on the hint-less fan-out any URL that happens
                // to answer text/event-stream (legacy MCP SSE transport,
                // graphql-sse, …) would otherwise grow a phantom
                // "SSE Endpoints" service next to the owning plugin's
                // real one. Marker name must stay aligned with
                // BowireSseProtocol.AdHocHintMarker.
                if (string.Equals(mappedId, "sse", StringComparison.OrdinalIgnoreCase))
                {
                    var sep = serverUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                    serverUrl = $"{serverUrl}{sep}__bowireSseAdHoc=1";
                }

                // SignalR's separate-target fallback (#510) is gated the
                // same way: negotiate-probe + ad-hoc service only for an
                // explicit signalr@ hint. Marker name must stay aligned
                // with BowireSignalRProtocol.AdHocHintMarker.
                if (string.Equals(mappedId, "signalr", StringComparison.OrdinalIgnoreCase))
                {
                    var sep = serverUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';
                    serverUrl = $"{serverUrl}{sep}__bowireSignalRAdHoc=1";
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
                // Nothing was probed, so the envelope's attempts array is
                // empty — but present, so an opted-in client has one shape
                // to read on every 200.
                return includeAttempts
                    ? Results.Json(new BowireDiscoveryEnvelope([], []), BowireEndpointHelpers.JsonOptions)
                    : Results.Json(Array.Empty<BowireServiceInfo>(), BowireEndpointHelpers.JsonOptions);
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

            // Try protocol plugins. The fanout itself lives in
            // BowireDiscoveryProbe so this endpoint, `bowire discover`
            // and the bowire.discover MCP tool all report the same
            // per-plugin outcomes instead of each swallowing a different
            // amount of the diagnosis (#534). The 8 s ceiling is a
            // frontend contract: the browser aborts /api/services at
            // 12 s, so one wedged plugin must not eat the whole budget —
            // probes run in parallel, so total wall-clock is
            // max(per-probe), not the sum (#83).
            var registry = BowireEndpointHelpers.GetRegistry();
            var probe = await BowireDiscoveryProbe.RunAsync(
                registry,
                serverUrl,
                pluginHint,
                options.ShowInternalServices,
                TimeSpan.FromSeconds(8),
                DescriptorSetMetadata(ctx),
                BowireEndpointHelpers.GetLogger(ctx),
                ctx.RequestAborted);

            // Same for proto-sourced services
            foreach (var svc in protoServices)
                svc.OriginUrl ??= serverUrl;

            // One payload variable, three sources — so the success shape
            // (bare array vs. { services, attempts }) is decided in exactly
            // one place instead of three.
            List<BowireServiceInfo>? payload = null;
            if (protoServices.Count > 0 && probe.Services.Count > 0)
                payload = BowireEndpointHelpers.MergeServices(protoServices, probe.Services);
            else if (protoServices.Count > 0)
                payload = protoServices;
            else if (probe.Services.Count > 0)
                payload = probe.Services;

            if (payload is not null)
            {
                return includeAttempts
                    ? Results.Json(new BowireDiscoveryEnvelope(payload, probe.Attempts), BowireEndpointHelpers.JsonOptions)
                    : Results.Json(payload, BowireEndpointHelpers.JsonOptions);
            }

            // No services from any source — surface as ProblemDetails so
            // the frontend can render the per-plugin failure list as
            // an actionable detail block (#88). `attempts` always carries
            // EVERY probed plugin, not just the ones that threw (#534):
            // a plugin that ran cleanly and returned nothing used to be
            // invisible next to one that failed, which made "the URL
            // isn't a gRPC endpoint" indistinguishable from "gRPC never
            // got a turn".
            // The `protocol@` hint only makes sense when we actually have
            // a URL to prefix. In embedded mode with no configured URL,
            // serverUrl is empty and the old unconditional text told the
            // operator to type the nonsense `rest@`.
            Dictionary<string, object?> NoMatchExtensions()
            {
                var ext = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["serverUrl"] = serverUrl,
                    ["attempts"] = probe.Attempts,
                };
                if (!string.IsNullOrEmpty(serverUrl))
                {
                    ext["hint"] = "Add a `protocol@` prefix (e.g. `rest@" + serverUrl
                        + "`) to pin a specific plugin and skip the others' probes.";
                }
                return ext;
            }

            if (probe.Attempts.Any(a => a.Outcome is BowireDiscoveryAttempt.OutcomeError
                                                  or BowireDiscoveryAttempt.OutcomeTimeout))
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:discovery:no-match",
                    title: "No protocol plugin recognised this URL",
                    status: 502,
                    detail: "Every loaded plugin probed the URL and either returned no services or failed. See `attempts` for the per-plugin outcome.",
                    instance: "/api/services",
                    extensions: NoMatchExtensions());
            }
            // "No plugins are loaded" must only claim that when it is
            // actually true. A plugin that ran and returned an empty list
            // (no error) must NOT land here — else e.g. `signalr@…` against
            // a remote host reports "No protocol plugins are loaded"
            // although the plugin was present and ran.
            if (registry.Protocols.Count == 0)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:discovery:no-plugins",
                    title: "No protocol plugins are loaded",
                    status: 502,
                    detail: "Bowire has no protocol plugins available to probe this URL. Upload .proto / OpenAPI / GraphQL SDL files via the Schema Files tab, or configure ProtoSources on the host.",
                    instance: "/api/services",
                    // Empty, but present — every no-services body carries an
                    // `attempts` array so clients can render one code path.
                    extensions: new Dictionary<string, object?> {
                        ["attempts"] = probe.Attempts,
                    });
            }

            // Attempts is empty when the hint matched no registered plugin —
            // the probe had nobody to fan out to.
            if (pluginHint is not null && probe.Attempts.Count == 0)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:discovery:unknown-plugin",
                    title: $"No plugin registered for hint '{pluginHint}'",
                    status: 502,
                    detail: "The `protocol@` prefix does not match any loaded plugin id. See `plugins` for the ids this host knows.",
                    instance: "/api/services",
                    extensions: new Dictionary<string, object?> {
                        ["serverUrl"] = serverUrl,
                        ["pluginHint"] = pluginHint,
                        ["plugins"] = registry.Protocols.Select(p => p.Id).ToArray(),
                    });
            }

            return BowireEndpointHelpers.Problem(
                type: "urn:bowire:discovery:no-match",
                title: "No protocol plugin recognised this URL",
                status: 502,
                detail: "The probed plugin(s) completed without errors but returned no services for this URL.",
                instance: "/api/services",
                extensions: NoMatchExtensions());
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Query-flag parsing for <c>?includeAttempts=…</c>. Present-but-empty
    /// counts as on (<c>?includeAttempts</c>), <c>0</c> / <c>false</c> as
    /// off — so a caller can pin the legacy shape explicitly rather than by
    /// omission.
    /// </summary>
    /// <summary>
    /// <c>?grpcDescriptorSet=&lt;path&gt;</c> as the metadata bag the gRPC
    /// plugin reads, or <c>null</c> when absent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discovery and invoke were answering different questions about the same
    /// server: <c>/api/invoke</c> honours a supplied descriptor set (#653), so
    /// a caller could invoke a method on a reflection-less server but could
    /// not get it listed. This closes that, which is what makes the same
    /// capability reachable from all three surfaces rather than two.
    /// </para>
    /// <para>
    /// A path, not bytes: a multi-kilobyte base64 blob does not belong in a
    /// query string, and the caller here is either the local workbench or a
    /// script on the same machine. The marker's JSON form still accepts
    /// inline bytes for callers that have them and no path.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string>? DescriptorSetMetadata(HttpContext ctx)
    {
        var path = ctx.Request.Query["grpcDescriptorSet"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(path)
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [BowireMetadataKeys.GrpcDescriptorSet] = path,
            };
    }

    private static bool IsTruthy(string? value)
    {
        if (value is null) return false;
        if (value.Length == 0) return true;
        return !string.Equals(value, "0", StringComparison.Ordinal)
            && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The opt-in <c>/api/services</c> success body (#544): the same service
/// list the bare-array shape returns, plus the per-plugin
/// <see cref="BowireDiscoveryAttempt"/> list that until now only existed on
/// the 502 problem+json. Without it a <c>partial</c> outcome — which by
/// definition implies services came back, i.e. a 200 — could never reach a
/// browser.
/// </summary>
internal sealed record BowireDiscoveryEnvelope(
    List<BowireServiceInfo> Services,
    List<BowireDiscoveryAttempt> Attempts);
