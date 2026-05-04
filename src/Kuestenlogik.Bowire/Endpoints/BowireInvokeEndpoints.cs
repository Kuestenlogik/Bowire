// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
        this IEndpointRouteBuilder endpoints, BowireOptions options, string prefix)
    {
        // Invoke a unary or client-streaming call
        endpoints.MapPost($"/{prefix}/api/invoke", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<InvokeRequest>(
                ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);

            if (body is null)
                return Results.BadRequest(new { error = "Invalid request body." });

            var rawServerUrl = ctx.Request.Query["serverUrl"].FirstOrDefault()
                ?? BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);

            // Strip an optional 'hint@url' prefix before any URL
            // manipulation runs — query-auth append, plugin selection,
            // recording capture all want the bare URL. The hint
            // overrides body.Protocol when present.
            var (urlHint, urlAfterHint) = BowireServerUrl.Parse(rawServerUrl);
            var serverUrl = urlAfterHint;
            if (urlHint is not null)
                body = body with { Protocol = urlHint };

            // ---- Auth: query-string API key ----
            // The JS apikey helper with location='query' marks its entries
            // with a magic prefix so the metadata dict can carry both real
            // headers and "this needs to go on the URL" hints. Strip the
            // prefix here, append the values to the server URL, and pass
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
                        : null
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
        endpoints.MapGet($"/{prefix}/api/invoke/stream", async (HttpContext ctx) =>
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
            // Strip 'hint@url' prefix; hint overrides protocolId.
            var (urlHint, urlAfterHint) = BowireServerUrl.Parse(rawServerUrl);
            var serverUrl = urlAfterHint;
            if (urlHint is not null) protocolId = urlHint;
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
                        var eventData = JsonSerializer.Serialize(new
                        {
                            index,
                            data = frame.Json,
                            timestampMs = Environment.TickCount64 - streamStartMs,
                            responseBinary = frame.Binary is null
                                ? null
                                : Convert.ToBase64String(frame.Binary)
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
                        var eventData = JsonSerializer.Serialize(new
                        {
                            index,
                            data = response,
                            timestampMs = Environment.TickCount64 - streamStartMs
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
}
