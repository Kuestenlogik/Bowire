// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the four protocol-agnostic channel endpoints used for duplex
/// and client-streaming calls — open, send, close, and the SSE stream
/// of incoming responses. State for the open channels lives in
/// <see cref="ChannelStore"/>.
/// </summary>
internal static class BowireChannelEndpoints
{
    public static IEndpointRouteBuilder MapBowireChannelEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string prefix)
    {
        // Open a channel
        endpoints.MapPost($"/{prefix}/api/channel/open", async (HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<ChannelOpenRequest>(
                ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);

            if (body is null || string.IsNullOrEmpty(body.Service) || string.IsNullOrEmpty(body.Method))
                return Results.BadRequest(new { error = "Missing 'service' or 'method'." });

            var serverUrl = ctx.Request.Query["serverUrl"].FirstOrDefault()
                ?? BowireEndpointHelpers.ResolveServerUrl(options, ctx.Request);
            (serverUrl, var channelMeta) = BowireEndpointHelpers.ApplyQueryAuthHints(serverUrl, body.Metadata);
            body = body with { Metadata = channelMeta };

            var registry = BowireEndpointHelpers.GetRegistry();
            var protocol = registry.GetById(body.Protocol ?? "grpc")
                ?? (registry.Protocols.Count > 0 ? registry.Protocols[0] : null);

            if (protocol is null)
                return Results.Json(new { error = "No protocol plugin available." }, BowireEndpointHelpers.JsonOptions, statusCode: 502);

            try
            {
                var channel = await protocol.OpenChannelAsync(
                    serverUrl, body.Service, body.Method,
                    options.ShowInternalServices, body.Metadata, ctx.RequestAborted);

                if (channel is null)
                    return Results.Json(new { error = "Protocol does not support channels." }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

                ChannelStore.Add(channel);

                return Results.Json(new
                {
                    channelId = channel.Id,
                    clientStreaming = channel.IsClientStreaming,
                    serverStreaming = channel.IsServerStreaming,
                    // Null for protocols that don't negotiate one —
                    // lets the recorder capture the picked value on
                    // WebSocket without polluting the other channels.
                    subProtocol = channel.NegotiatedSubProtocol
                }, BowireEndpointHelpers.JsonOptions);
            }
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Channel open failed for {Protocol} {Service}/{Method} at {ServerUrl}",
                    protocol.Id, BowireEndpointHelpers.SafeLog(body.Service), BowireEndpointHelpers.SafeLog(body.Method), BowireEndpointHelpers.SafeLog(serverUrl));
                return Results.Json(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 502);
            }
        }).ExcludeFromDescription();

        // Send a message to an open channel
        endpoints.MapPost($"/{prefix}/api/channel/{{id}}/send", async (string id, HttpContext ctx) =>
        {
            var body = await JsonSerializer.DeserializeAsync<ChannelSendRequest>(
                ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);

            if (body is null || string.IsNullOrEmpty(body.Message))
                return Results.BadRequest(new { error = "Missing 'message'." });

            var channel = ChannelStore.Get(id);
            if (channel is null)
                return Results.NotFound(new { error = "Channel not found." });

            if (channel.IsClosed)
                return Results.Json(new { error = "Channel is closed." }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

            try
            {
                var sent = await channel.SendAsync(body.Message, ctx.RequestAborted);
                return Results.Json(new { sent, sequence = channel.SentCount }, BowireEndpointHelpers.JsonOptions);
            }
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Channel send failed for channel {ChannelId}", BowireEndpointHelpers.SafeLog(id));
                return Results.Json(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 500);
            }
        }).ExcludeFromDescription();

        // Close the request stream
        endpoints.MapPost($"/{prefix}/api/channel/{{id}}/close", async (string id) =>
        {
            var channel = ChannelStore.Get(id);
            if (channel is null)
                return Results.NotFound(new { error = "Channel not found." });

            await channel.CloseAsync();
            return Results.Json(new { closed = true, sentCount = channel.SentCount }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        // SSE stream of responses from the channel
        endpoints.MapGet($"/{prefix}/api/channel/{{id}}/responses", async (string id, HttpContext ctx) =>
        {
            var channel = ChannelStore.Get(id);
            if (channel is null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("Channel not found.");
                return;
            }

            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            // Flush the headers immediately so the browser's EventSource
            // transitions from CONNECTING to OPEN before we block on
            // ReadResponsesAsync. Without this flush, the SSE connection
            // stays in CONNECTING state and never receives messages.
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            try
            {
                // timestampMs is the offset from the start of the SSE
                // subscription on the server clock. Paired with the sent-side
                // timestamps the client captures locally, this is enough for
                // Phase-2 mock replay to pace the duplex session at the
                // original cadence.
                var streamStartMs = Environment.TickCount64;
                var index = 0;
                await foreach (var response in channel.ReadResponsesAsync(ctx.RequestAborted))
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

                // Channel completed
                var doneData = JsonSerializer.Serialize(new
                {
                    sentCount = channel.SentCount,
                    durationMs = channel.ElapsedMs
                }, BowireEndpointHelpers.JsonOptions);
                await ctx.Response.WriteAsync($"event: done\ndata: {doneData}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            catch (OperationCanceledException)
            {
                // Client disconnected
            }
            catch (Exception ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Channel response stream failed for channel {ChannelId}", BowireEndpointHelpers.SafeLog(id));
                var errorData = JsonSerializer.Serialize(new { error = ex.Message }, BowireEndpointHelpers.JsonOptions);
                await ctx.Response.WriteAsync($"event: error\ndata: {errorData}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }
            finally
            {
                ChannelStore.Remove(id);
                await channel.DisposeAsync();
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    private sealed record ChannelOpenRequest(
        string Service,
        string Method,
        string? Protocol,
        Dictionary<string, string>? Metadata);

    private sealed record ChannelSendRequest(
        string Message);
}
