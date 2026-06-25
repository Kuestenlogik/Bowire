// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;
using Kuestenlogik.Bowire.Telemetry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the unary + server-streaming invoke endpoints. The unary path
/// also handles the gRPC-HTTP-transcoding bridge: when the JS sends an
/// inline <see cref="TranscodedMethodInfo"/>, the request is dispatched
/// through <see cref="IInlineHttpInvoker"/> instead of the normal
/// protocol plugin.
/// </summary>
internal static class BowireInvokeEndpoints
{
    public static IEndpointRouteBuilder MapBowireInvokeEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        // Invoke a unary or client-streaming call
        endpoints.MapPost($"{basePath}/api/invoke", async (HttpContext ctx) =>
        {
            InvokeRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<InvokeRequest>(
                    ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: "/api/invoke");
            }

            if (body is null)
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body is missing or malformed",
                    status: 400,
                    detail: "The /api/invoke endpoint requires a JSON body with { protocol, service, method, messages, ... }.",
                    instance: "/api/invoke");

            var rawServerUrl = ctx.Request.Query["serverUrl"].FirstOrDefault()
                ?? BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);

            // Strip an optional 'hint@url' basePath before any URL
            // manipulation runs — query-auth append, plugin selection,
            // recording capture all want the bare URL. The hint
            // overrides body.Protocol when present.
            var (urlHint, urlAfterHint) = BowireServerUrl.Parse(rawServerUrl);
            var serverUrl = urlAfterHint;
            if (urlHint is not null)
            {
                // Transport-variant hints (e.g. `grpcweb@`) resolve to an
                // existing plugin id plus a side-channel metadata entry. The
                // plugin id pins dispatch; the metadata entry is added to
                // body.Metadata so the plugin sees both knobs at once.
                var (mappedId, transportMeta) = BowireEndpointHelpers.ResolveHint(urlHint);
                body = body with { Protocol = mappedId };
                if (transportMeta is { } tm)
                {
                    var meta = body.Metadata is null
                        ? new Dictionary<string, string>(StringComparer.Ordinal)
                        : new Dictionary<string, string>(body.Metadata, StringComparer.Ordinal);
                    meta[tm.Key] = tm.Value;
                    body = body with { Metadata = meta };
                }
            }

            // ---- Auth: query-string API key ----
            // The JS apikey helper with location='query' marks its entries
            // with a magic basePath so the metadata dict can carry both real
            // headers and "this needs to go on the URL" hints. Strip the
            // basePath here, append the values to the server URL, and pass
            // only the actual headers down to the plugin.
            (serverUrl, var sanitizedMeta) = BowireEndpointHelpers.ApplyQueryAuthHints(serverUrl, body.Metadata);
            body = body with { Metadata = sanitizedMeta };

            // ---- #290 — Binary upload smuggled via metadata ----
            // The Hopp-bar's binary body picker reads the chosen File as
            // base64 and ships it on body.BodyBinary. To avoid plumbing a
            // new field through every protocol plugin's InvokeAsync
            // signature (50+ implementations), we stash the base64 + the
            // content-type + the filename onto the metadata dict under
            // reserved X-Bowire-* keys. The REST plugin's HttpInvoker
            // sniffs these out and writes raw bytes to the wire; other
            // plugins ignore them — so the same path that handles a Hopp
            // JSON body keeps working unchanged for non-binary cases.
            if (!string.IsNullOrEmpty(body.BodyBinary))
            {
                var binMeta = body.Metadata is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(body.Metadata, StringComparer.Ordinal);
                binMeta["X-Bowire-Body-Binary"] = body.BodyBinary;
                binMeta["X-Bowire-Body-Binary-Content-Type"] =
                    body.BodyBinaryContentType ?? "application/octet-stream";
                if (!string.IsNullOrEmpty(body.BodyBinaryName))
                {
                    binMeta["X-Bowire-Body-Binary-Name"] = body.BodyBinaryName;
                }
                body = body with { Metadata = binMeta };
            }

            // ---- Transcoded HTTP path ----
            // When the JS sends an inline TranscodedMethod (because the user
            // toggled "via HTTP" on a transcoded gRPC method), look up an
            // IInlineHttpInvoker in the protocol registry and dispatch via it.
            // The REST plugin implements that interface — when REST isn't loaded
            // we return a clear error so users know which package to install.
            if (body.TranscodedMethod is not null
                && !string.IsNullOrEmpty(body.TranscodedMethod.HttpMethod)
                && !string.IsNullOrEmpty(body.TranscodedMethod.HttpPath))
            {
                var httpInvoker = BowireEndpointHelpers.GetRegistry().FindHttpInvoker();
                if (httpInvoker is null)
                {
                    return BowireEndpointHelpers.Problem(
                        type: "urn:bowire:invoke:rest-plugin-required",
                        title: "HTTP transcoding needs the REST plugin",
                        status: 501,
                        detail: "Transcoded gRPC invocation goes through the REST plugin's IInlineHttpInvoker. Install Kuestenlogik.Bowire.Protocol.Rest (`bowire plugin install Kuestenlogik.Bowire.Protocol.Rest`) and re-discover.",
                        instance: "/api/invoke");
                }

                try
                {
                    var transcodedInfo = body.TranscodedMethod.ToBowireMethodInfo(body.Service, body.Method);
                    var httpResult = await httpInvoker.InvokeHttpAsync(
                        serverUrl, transcodedInfo,
                        body.Messages ?? ["{}"], body.Metadata, ctx.RequestAborted);

                    return Results.Json(new
                    {
                        response = httpResult.Response,
                        duration_ms = httpResult.DurationMs,
                        status = httpResult.Status,
                        metadata = httpResult.Metadata
                    }, BowireEndpointHelpers.JsonOptions);
                }
                // REST plugin's InvokeHttpAsync surface: HTTP transport
                // errors, malformed transcoded path, JSON marshaling
                // failures — plugin-author-defined types possible.
#pragma warning disable CA1031 // Do not catch general exception types
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                        "Transcoded HTTP invoke failed for {Service}/{Method} at {ServerUrl}",
                        BowireEndpointHelpers.SafeLog(body.Service), BowireEndpointHelpers.SafeLog(body.Method), BowireEndpointHelpers.SafeLog(serverUrl));
                    return BowireEndpointHelpers.Problem(
                        type: "urn:bowire:invoke:upstream-error",
                        title: $"Transcoded HTTP invoke failed: {body.Service}.{body.Method}",
                        status: 502,
                        detail: ex.Message + (ex.InnerException is { } inner ? "\n\nInner: " + inner.Message : string.Empty),
                        instance: "/api/invoke",
                        extensions: new Dictionary<string, object?> {
                            ["service"] = body.Service,
                            ["method"] = body.Method,
                            ["serverUrl"] = serverUrl,
                            ["exceptionType"] = ex.GetType().Name,
                        });
                }
            }

            // ---- Protocol-plugin dispatch (the normal path) ----
            var registry = BowireEndpointHelpers.GetRegistry();
            var protocol = registry.GetById(body.Protocol ?? "grpc")
                ?? (registry.Protocols.Count > 0 ? registry.Protocols[0] : null);

            if (protocol is null)
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invoke:no-plugin",
                    title: "No protocol plugin available to dispatch this call",
                    status: 502,
                    detail: $"The requested protocol '{body.Protocol ?? "(default)"}' isn't loaded and no fallback is available. Install the matching plugin or pin a loaded one via the protocol@ URL hint.",
                    instance: "/api/invoke",
                    extensions: new Dictionary<string, object?> {
                        ["protocol"] = body.Protocol,
                    });

            // #29 self-telemetry. Bracket the invoke with the
            // bowire.invoke.* instruments. Cheap when no OTel listener
            // is attached (the SDK's no-op fast path keeps the cost
            // negligible). Tags carry protocol + service + method so
            // dashboards can break down by any of them -- the operator
            // can drop the high-cardinality dims via the privacy
            // switch (--telemetry-strip-method-labels).
            using var activity = BowireTelemetry.ActivitySource.StartActivity(
                "bowire.invoke", ActivityKind.Client);
            activity?.SetTag("protocol", body.Protocol);
            activity?.SetTag("service", body.Service);
            activity?.SetTag("method", body.Method);
            var invokeStart = Stopwatch.GetTimestamp();
            string outcome = "ok";
            try
            {
                var result = await protocol.InvokeAsync(
                    serverUrl, body.Service, body.Method,
                    body.Messages ?? ["{}"], options.ShowInternalServices,
                    body.Metadata, ctx.RequestAborted);

                // Frame-semantics auto-detector hook on the unary path —
                // streaming already feeds every frame to ObserveFrame, but
                // unary calls used to bypass the prober entirely, so a
                // method that only ever runs as unary (e.g. TacticalAPI's
                // GetSituationObjects, REST GETs, gRPC unary CRUD) never
                // accumulated annotations and the WGS84 / future Phase-3
                // viewers never auto-mounted. ObserveFrame is a dictionary
                // lookup on the repeat path, so the cost stays negligible.
                var prober = ctx.RequestServices.GetService<IFrameProber>();
                FrameProbingMiddleware.Observe(prober, body.Service, body.Method, result.Response);

                // Phase-5 unary interpretations channel — same shape as the
                // SSE envelope. The workbench JS picks the field off the
                // /api/invoke response and stores it on the recording step
                // so replay re-emits the same widget state.
                var annotationStore = ctx.RequestServices.GetService<IAnnotationStore>();
                var interpretations = ResolveLiveInterpretations(
                    annotationStore, body.Service, body.Method, result.Response);

                return Results.Json(new
                {
                    response = result.Response,
                    duration_ms = result.DurationMs,
                    status = result.Status,
                    metadata = result.Metadata,
                    // Base64 of the raw response wire bytes when the protocol
                    // has a binary form distinct from its JSON representation
                    // (gRPC protobuf). Recorded on the step so mock-server
                    // replay can emit the bytes 1:1.
                    response_binary = result.ResponseBinary is { } rb
                        ? Convert.ToBase64String(rb)
                        : null,
                    discriminator = AnnotationKey.Wildcard,
                    interpretations,
                }, BowireEndpointHelpers.JsonOptions);
            }
            // Plugin InvokeAsync surface: 3rd-party transport, plugin-author
            // defined types possible. See transcoded path above.
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                outcome = ex is OperationCanceledException ? "canceled" : "error";
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Invoke failed for {Protocol} {Service}/{Method} at {ServerUrl}",
                    protocol.Id, BowireEndpointHelpers.SafeLog(body.Service), BowireEndpointHelpers.SafeLog(body.Method), BowireEndpointHelpers.SafeLog(serverUrl));
                var detailBuilder = new System.Text.StringBuilder(ex.Message);
                if (ex.InnerException is { } inner) detailBuilder.Append("\n\nInner: ").Append(inner.Message);
                return BowireEndpointHelpers.Problem(
                    type: outcome == "canceled" ? "urn:bowire:canceled" : "urn:bowire:invoke:upstream-error",
                    title: outcome == "canceled"
                        ? $"Call canceled: {body.Service}.{body.Method}"
                        : $"Upstream error invoking {body.Service}.{body.Method}",
                    status: 502,
                    detail: detailBuilder.ToString(),
                    instance: "/api/invoke",
                    extensions: new Dictionary<string, object?> {
                        ["protocol"] = protocol.Id,
                        ["service"] = body.Service,
                        ["method"] = body.Method,
                        ["serverUrl"] = serverUrl,
                        ["exceptionType"] = ex.GetType().Name,
                    });
            }
            finally
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - invokeStart)
                    / (double)Stopwatch.Frequency * 1000.0;
                var tags = new TagList
                {
                    { "protocol", body.Protocol },
                    { "service", body.Service },
                    { "method", body.Method },
                    { "outcome", outcome },
                };
                BowireTelemetry.InvokeCount.Add(1, tags);
                BowireTelemetry.InvokeDuration.Record(elapsedMs, tags);
            }
        }).ExcludeFromDescription();

        // SSE endpoint for server-streaming and duplex calls
        endpoints.MapGet($"{basePath}/api/invoke/stream", async (HttpContext ctx) =>
        {
            var service = ctx.Request.Query["service"].ToString();
            var method = ctx.Request.Query["method"].ToString();
            var messagesJson = ctx.Request.Query["messages"].ToString();
            var metadataJson = ctx.Request.Query["metadata"].ToString();
            var protocolId = ctx.Request.Query["protocol"].FirstOrDefault();

            if (string.IsNullOrEmpty(service) || string.IsNullOrEmpty(method))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync("Missing 'service' or 'method' query parameter.");
                return;
            }

            List<string> messages;
            try
            {
                messages = string.IsNullOrEmpty(messagesJson)
                    ? ["{}"]
                    : JsonSerializer.Deserialize<List<string>>(messagesJson, BowireEndpointHelpers.JsonOptions) ?? ["{}"];
            }
            catch (JsonException)
            {
                // Treat malformed messages query param as "no messages"
                messages = ["{}"];
            }

            Dictionary<string, string>? metadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                try
                {
                    metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, BowireEndpointHelpers.JsonOptions);
                }
                catch (JsonException)
                {
                    // Invalid metadata JSON — ignore and proceed without
                }
            }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var rawServerUrl = ctx.Request.Query["serverUrl"].FirstOrDefault()
                ?? BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);
            // Strip 'hint@url' basePath; hint overrides protocolId.
            var (urlHint, urlAfterHint) = BowireServerUrl.Parse(rawServerUrl);
            var serverUrl = urlAfterHint;
            if (urlHint is not null)
            {
                // Transport-variant hints (e.g. `grpcweb@`) resolve to an
                // existing plugin id plus a side-channel metadata entry.
                var (mappedId, transportMeta) = BowireEndpointHelpers.ResolveHint(urlHint);
                protocolId = mappedId;
                if (transportMeta is { } tm)
                {
                    metadata ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    metadata[tm.Key] = tm.Value;
                }
            }
            (serverUrl, metadata) = BowireEndpointHelpers.ApplyQueryAuthHints(serverUrl, metadata);

            var registry = BowireEndpointHelpers.GetRegistry();
            var protocol = registry.GetById(protocolId ?? "grpc")
                ?? (registry.Protocols.Count > 0 ? registry.Protocols[0] : null);

            if (protocol is null)
            {
                var errorData = JsonSerializer.Serialize(new { error = "No protocol plugin available." }, BowireEndpointHelpers.JsonOptions);
                await ctx.Response.WriteAsync($"event: error\ndata: {errorData}\n\n", ctx.RequestAborted);
                return;
            }

            try
            {
                // Start of the stream on the server clock — each frame's
                // timestampMs is the offset from this anchor. Recordings
                // persist this so Phase-2 mock replay can pace frames at the
                // original cadence regardless of the system clock on the
                // replay host.
                var streamStartMs = Environment.TickCount64;
                var index = 0;

                // Frame-semantics auto-detector hook. The prober's
                // ObserveFrame call is a single dictionary lookup on
                // the repeat path (already-probed triples), so it
                // doesn't slow high-frequency streams. Null when no
                // detectors are registered (DisableBuiltInDetectors
                // opt-out path).
                var prober = ctx.RequestServices.GetService<IFrameProber>();

                // Phase-5 interpretations channel: the annotation store
                // resolves each frame's effective tags + their typed
                // payloads, which the recorder persists alongside the
                // raw frame so replay can re-emit widgets without
                // re-running detection. Null when AddBowire wasn't
                // called — degrades gracefully (no `interpretations`
                // field in the envelope).
                var annotationStore = ctx.RequestServices.GetService<IAnnotationStore>();

                // Plugins that expose wire bytes (gRPC today) route through
                // InvokeStreamWithFramesAsync so the recorder can persist
                // `responseBinary` per frame — needed for Phase-2d gRPC
                // streaming replay. Everyone else stays on the JSON-only
                // path, responseBinary left null.
                if (protocol is IBowireStreamingWithWireBytes binStream)
                {
                    await foreach (var frame in binStream.InvokeStreamWithFramesAsync(
                        serverUrl, service, method, messages, options.ShowInternalServices, metadata, ctx.RequestAborted))
                    {
                        FrameProbingMiddleware.Observe(prober, service, method, frame.Json);
                        var interpretations = ResolveLiveInterpretations(
                            annotationStore, service, method, frame.Json);
                        var eventData = JsonSerializer.Serialize(new
                        {
                            index,
                            data = frame.Json,
                            timestampMs = Environment.TickCount64 - streamStartMs,
                            responseBinary = frame.Binary is null
                                ? null
                                : Convert.ToBase64String(frame.Binary),
                            discriminator = AnnotationKey.Wildcard,
                            interpretations,
                        }, BowireEndpointHelpers.JsonOptions);

                        await ctx.Response.WriteAsync($"data: {eventData}\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        index++;
                    }
                }
                else
                {
                    await foreach (var response in protocol.InvokeStreamAsync(
                        serverUrl, service, method, messages, options.ShowInternalServices, metadata, ctx.RequestAborted))
                    {
                        FrameProbingMiddleware.Observe(prober, service, method, response);
                        var interpretations = ResolveLiveInterpretations(
                            annotationStore, service, method, response);
                        var eventData = JsonSerializer.Serialize(new
                        {
                            index,
                            data = response,
                            timestampMs = Environment.TickCount64 - streamStartMs,
                            discriminator = AnnotationKey.Wildcard,
                            interpretations,
                        }, BowireEndpointHelpers.JsonOptions);

                        await ctx.Response.WriteAsync($"data: {eventData}\n\n", ctx.RequestAborted);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                        index++;
                    }
                }

                await ctx.Response.WriteAsync("event: done\ndata: {}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected — normal for streaming
            }
            // Plugin streaming surface + SSE write surface: 3rd-party
            // transport plus IOException for client disconnect.
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception ex)
#pragma warning restore CA1031
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Streaming invoke failed for {Protocol} {Service}/{Method} at {ServerUrl}",
                    protocol.Id, BowireEndpointHelpers.SafeLog(service), BowireEndpointHelpers.SafeLog(method), BowireEndpointHelpers.SafeLog(serverUrl));
                var errorData = JsonSerializer.Serialize(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions);
                await ctx.Response.WriteAsync($"event: error\ndata: {errorData}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    private sealed record InvokeRequest(
        string Service,
        string Method,
        List<string>? Messages,
        Dictionary<string, string>? Metadata,
        string? Protocol)
    {
        /// <summary>
        /// Optional inline transcoding hint. When present, BowireInvokeEndpoints
        /// dispatches directly to <see cref="IInlineHttpInvoker"/> using this method
        /// info instead of looking up the protocol plugin's cache. Set by the
        /// JS layer when the user toggles a transcoded gRPC method to "via HTTP".
        /// </summary>
        public TranscodedMethodInfo? TranscodedMethod { get; init; }

        /// <summary>
        /// #290 — base64-encoded binary request body for Hopp-bar binary
        /// uploads. When present + non-empty the binary payload is materialised
        /// onto a synthetic <c>X-Bowire-Body-Binary</c> metadata triple
        /// (<c>X-Bowire-Body-Binary-Content-Type</c> + filename) and shipped
        /// downstream to the protocol plugin alongside <see cref="Messages"/>.
        /// REST plugin sniffs these out and writes raw bytes to the wire;
        /// other plugins ignore them. base64-in-JSON was chosen over multipart
        /// because it preserves the existing single JSON-body shape — no new
        /// endpoint, no Content-Type-sniffing branch in the POST handler, and
        /// the proxy / recording / telemetry pipeline keeps working unchanged.
        /// Trade-off: ~33% wire overhead. Acceptable for typical Hopp-bar
        /// uploads (a config file, an image, a serialised proto message).
        /// Streaming / multi-MB uploads should switch to a dedicated endpoint.
        /// </summary>
        public string? BodyBinary { get; init; }

        /// <summary>
        /// Content-Type to use for the binary body when <see cref="BodyBinary"/>
        /// is present. Falls back to <c>application/octet-stream</c> when the
        /// caller didn't pin one.
        /// </summary>
        public string? BodyBinaryContentType { get; init; }

        /// <summary>
        /// Original filename of the binary upload. Surfaced to the protocol
        /// plugin via metadata so any downstream Content-Disposition header
        /// the plugin emits carries the original name. Optional.
        /// </summary>
        public string? BodyBinaryName { get; init; }
    }

    /// <summary>
    /// Minimal subset of <see cref="BowireMethodInfo"/> shipped over
    /// the wire when the JS sends a transcoding-mode invoke. Contains only
    /// the fields HttpInvoker actually needs (verb, path, input field names
    /// and their HTTP source bucket). Lets us avoid serializing the entire
    /// gRPC message tree on every call.
    /// </summary>
    private sealed record TranscodedMethodInfo(
        string HttpMethod,
        string HttpPath,
        List<TranscodedFieldInfo>? Fields)
    {
        /// <summary>
        /// Builds a synthetic <see cref="BowireMethodInfo"/> that's
        /// just shaped enough for <see cref="IInlineHttpInvoker"/> to dispatch.
        /// </summary>
        public BowireMethodInfo ToBowireMethodInfo(string service, string method)
        {
            var bowireFields = new List<BowireFieldInfo>();
            if (Fields is not null)
            {
                var i = 1;
                foreach (var f in Fields)
                {
                    bowireFields.Add(new BowireFieldInfo(
                        Name: f.Name,
                        Number: i++,
                        Type: f.Type ?? "string",
                        Label: "optional",
                        IsMap: false,
                        IsRepeated: false,
                        MessageType: null,
                        EnumValues: null)
                    {
                        Source = f.Source
                    });
                }
            }

            var inputType = new BowireMessageInfo(
                Name: method + "Request",
                FullName: service + "." + method + "Request",
                Fields: bowireFields);
            var outputType = new BowireMessageInfo(
                Name: method + "Response",
                FullName: service + "." + method + "Response",
                Fields: []);

            return new BowireMethodInfo(
                Name: method,
                FullName: service + "/" + method,
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: inputType,
                OutputType: outputType,
                MethodType: "Unary")
            {
                HttpMethod = HttpMethod,
                HttpPath = HttpPath
            };
        }
    }

    private sealed record TranscodedFieldInfo(string Name, string? Type, string? Source);

    /// <summary>
    /// Phase-5 helper — resolve the per-frame interpretations against the
    /// effective annotation store. Returns <c>null</c> when the input is
    /// missing or non-JSON so the SSE envelope omits the field (rather than
    /// shipping an empty array on every frame, which would inflate the
    /// recording file). The recorder treats the absence of the field the
    /// same as the absence of annotations.
    /// </summary>
    /// <remarks>
    /// Plugin-side discriminator wiring isn't in place yet — Phase 5 plumbs
    /// <see cref="AnnotationKey.Wildcard"/> as the discriminator value for
    /// every frame, matching the live-detection path's behaviour. Concrete
    /// discriminator resolution is a Phase 6+ concern; the field is on the
    /// SSE envelope (and on the recording-step model) so it round-trips
    /// when a future phase fills it.
    /// </remarks>
    private static IReadOnlyList<RecordedInterpretation>? ResolveLiveInterpretations(
        IAnnotationStore? store, string service, string method, string? frameJson)
    {
        if (store is null) return null;
        if (string.IsNullOrEmpty(frameJson)) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(frameJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (doc)
        {
            return RecordingInterpretationBuilder.Build(
                store, service, method, AnnotationKey.Wildcard, doc.RootElement);
        }
    }
}
