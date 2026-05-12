// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;
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
            var body = await JsonSerializer.DeserializeAsync<InvokeRequest>(
                ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);

            if (body is null)
                return Results.BadRequest(new { error = "Invalid request body." });

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
                    return Results.Json(new
                    {
                        error = "HTTP transcoding invocation requires the REST plugin. "
                            + "Install Kuestenlogik.Bowire.Protocol.Rest (or run `bowire plugin install Kuestenlogik.Bowire.Protocol.Rest`) "
                            + "to enable invoking transcoded gRPC methods via HTTP."
                    }, BowireEndpointHelpers.JsonOptions, statusCode: 501);
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
                catch (Exception ex)
                {
                    BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                        "Transcoded HTTP invoke failed for {Service}/{Method} at {ServerUrl}",
                        BowireEndpointHelpers.SafeLog(body.Service), BowireEndpointHelpers.SafeLog(body.Method), BowireEndpointHelpers.SafeLog(serverUrl));
                    return Results.Json(new
                    {
                        error = ex.Message,
                        details = ex.InnerException?.Message,
                        type = ex.GetType().Name
                    }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
                }
            }

            // ---- Protocol-plugin dispatch (the normal path) ----
            var registry = BowireEndpointHelpers.GetRegistry();
            var protocol = registry.GetById(body.Protocol ?? "grpc")
                ?? (registry.Protocols.Count > 0 ? registry.Protocols[0] : null);

            if (protocol is null)
                return Results.Json(new { error = "No protocol plugin available." }, BowireEndpointHelpers.JsonOptions, statusCode: 502);

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
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Invoke failed for {Protocol} {Service}/{Method} at {ServerUrl}",
                    protocol.Id, BowireEndpointHelpers.SafeLog(body.Service), BowireEndpointHelpers.SafeLog(body.Method), BowireEndpointHelpers.SafeLog(serverUrl));
                return Results.Json(new
                {
                    error = ex.Message,
                    details = ex.InnerException?.Message,
                    type = ex.GetType().Name,
                    stack = ex.StackTrace
                }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
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
            catch
            {
                messages = ["{}"];
            }

            Dictionary<string, string>? metadata = null;
            if (!string.IsNullOrEmpty(metadataJson))
            {
                try
                {
                    metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metadataJson, BowireEndpointHelpers.JsonOptions);
                }
                catch
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
            catch (Exception ex)
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

        try
        {
            return RecordingInterpretationBuilder.Build(
                store, service, method, AnnotationKey.Wildcard, doc.RootElement);
        }
        finally
        {
            doc.Dispose();
        }
    }
}
