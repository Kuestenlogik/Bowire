// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Interceptor;
using Kuestenlogik.Bowire.Mocking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Workbench-facing endpoints for the in-process interceptor (#153).
/// Pair with <c>app.UseBowireInterceptor()</c>: the middleware captures
/// flows into the singleton <see cref="InterceptedFlowStore"/>, and
/// these endpoints expose them to the workbench's "Intercepted" rail
/// — listing, live SSE stream, detail fetch, send-to-recording.
/// </summary>
/// <remarks>
/// The shape intentionally mirrors
/// <see cref="BowireProxyEndpoints"/>: the workbench shares the same
/// detail-pane renderer between the proxy rail (standalone CLI) and
/// the intercepted rail (embedded middleware). Field names match —
/// <c>method</c>, <c>url</c>, <c>scheme</c>, <c>responseStatus</c>,
/// <c>latencyMs</c>, <c>requestHeaders</c>, &amp;c — so the renderer
/// doesn't need to branch on the source.
/// </remarks>
internal static class BowireInterceptorEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapBowireInterceptorEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // GET /api/intercepted/flows — newest-first snapshot for the rail listing.
        endpoints.MapGet($"{basePath}/api/intercepted/flows", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<InterceptedFlowStore>();
            if (store is null) return Results.Json(new { flows = Array.Empty<object>() }, s_jsonOpts);
            var snapshot = store.Snapshot().Select(ProjectSummary).ToArray();
            return Results.Json(new { flows = snapshot }, s_jsonOpts);
        }).ExcludeFromDescription();

        // GET /api/intercepted/flows/{id} — full flow (headers + bodies) for detail pane.
        endpoints.MapGet($"{basePath}/api/intercepted/flows/{{id:long}}", (long id, HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<InterceptedFlowStore>();
            if (store is null) return Results.NotFound();
            var flow = store.Get(id);
            return flow is null ? Results.NotFound() : Results.Json(ProjectFull(flow), s_jsonOpts);
        }).ExcludeFromDescription();

        // DELETE /api/intercepted/flows — workbench "Clear all" button.
        endpoints.MapDelete($"{basePath}/api/intercepted/flows", (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<InterceptedFlowStore>();
            store?.Clear();
            return Results.NoContent();
        }).ExcludeFromDescription();

        // GET /api/intercepted/stream — SSE live feed of new flows.
        endpoints.MapGet($"{basePath}/api/intercepted/stream", async (HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<InterceptedFlowStore>();
            if (store is null)
            {
                ctx.Response.StatusCode = 404;
                return;
            }

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            var reader = store.Subscribe(ctx.RequestAborted);
            try
            {
                await foreach (var flow in reader.ReadAllAsync(ctx.RequestAborted).ConfigureAwait(false))
                {
                    var payload = JsonSerializer.Serialize(ProjectSummary(flow), s_jsonOpts);
                    await ctx.Response.WriteAsync($"event: flow\ndata: {payload}\n\n", ctx.RequestAborted).ConfigureAwait(false);
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* client disconnected */ }
        }).ExcludeFromDescription();

        // POST /api/intercepted/flows/{id}/recording — convert an intercepted
        // flow into a Bowire recording (same shape the proxy endpoints emit, so
        // the workbench's "Send to recording" flow stays uniform).
        endpoints.MapPost($"{basePath}/api/intercepted/flows/{{id:long}}/recording", (long id, HttpContext ctx) =>
        {
            var store = ctx.RequestServices.GetService<InterceptedFlowStore>();
            if (store is null) return Results.NotFound();
            var flow = store.Get(id);
            if (flow is null) return Results.NotFound();

            var recording = ToRecording(flow);
            return Results.Json(recording, s_jsonOpts);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static object ProjectSummary(InterceptedFlow flow) => new
    {
        id = flow.Id,
        capturedAt = flow.CapturedAt,
        method = flow.Method,
        url = flow.Url,
        path = flow.Path,
        scheme = flow.Scheme,
        responseStatus = flow.ResponseStatus,
        latencyMs = flow.LatencyMs,
        error = flow.Error,
        streaming = flow.Streaming,
        requestBodySize = (flow.RequestBody?.Length)
            ?? (flow.RequestBodyBase64 is null ? 0 : flow.RequestBodyBase64.Length * 3 / 4),
        responseBodySize = (flow.ResponseBody?.Length)
            ?? (flow.ResponseBodyBase64 is null ? 0 : flow.ResponseBodyBase64.Length * 3 / 4),
        requestBodyTruncated = flow.RequestBodyTruncated,
        responseBodyTruncated = flow.ResponseBodyTruncated,
    };

    private static object ProjectFull(InterceptedFlow flow) => new
    {
        id = flow.Id,
        capturedAt = flow.CapturedAt,
        method = flow.Method,
        url = flow.Url,
        path = flow.Path,
        scheme = flow.Scheme,
        requestHeaders = flow.RequestHeaders,
        requestBody = flow.RequestBody,
        requestBodyBase64 = flow.RequestBodyBase64,
        requestBodyTruncated = flow.RequestBodyTruncated,
        responseStatus = flow.ResponseStatus,
        responseHeaders = flow.ResponseHeaders,
        responseBody = flow.ResponseBody,
        responseBodyBase64 = flow.ResponseBodyBase64,
        responseBodyTruncated = flow.ResponseBodyTruncated,
        streaming = flow.Streaming,
        latencyMs = flow.LatencyMs,
        error = flow.Error,
    };

    /// <summary>
    /// Project an intercepted flow into the on-disk Bowire-recording shape.
    /// Same surface as
    /// <c>BowireProxyEndpoints.ToRecording</c> so the workbench's
    /// import path doesn't have to branch on the source rail.
    /// </summary>
    private static BowireRecording ToRecording(InterceptedFlow flow)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in flow.RequestHeaders)
        {
            if (!headers.ContainsKey(k)) headers[k] = v;
        }

        Uri.TryCreate(flow.Url, UriKind.Absolute, out var parsed);
        var httpPath = parsed is null ? flow.Path : parsed.PathAndQuery;

        var step = new BowireRecordingStep
        {
            Id = Guid.NewGuid().ToString("N"),
            CapturedAt = flow.CapturedAt.ToUnixTimeMilliseconds(),
            Protocol = "rest",
            Service = parsed?.Host ?? "",
            Method = flow.Method,
            MethodType = "Unary",
            ServerUrl = parsed is null ? null : $"{parsed.Scheme}://{parsed.Authority}",
            HttpVerb = flow.Method,
            HttpPath = httpPath,
            Body = flow.RequestBody,
            Status = flow.ResponseStatus == 0
                ? "Error"
                : flow.ResponseStatus.ToString(CultureInfo.InvariantCulture),
            DurationMs = flow.LatencyMs,
            Response = flow.ResponseBody,
            Metadata = headers,
        };

        var recording = new BowireRecording
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"intercepted-{flow.Id}",
            Description = $"Captured by Bowire interceptor at {flow.CapturedAt:O}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        recording.Steps.Add(step);
        return recording;
    }
}
