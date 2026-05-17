// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Proxy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Workbench-side proxy endpoints. The CLI <c>bowire proxy</c> writes
/// flows into a shared <see cref="CapturedFlowStore"/>; the workbench
/// reads them back here for the "Proxy" tab (listing + live SSE stream)
/// and converts a chosen flow into a Bowire recording via
/// <c>POST /api/proxy/flows/{id}/recording</c>.
/// </summary>
internal static class BowireProxyEndpoints
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapBowireProxyEndpoints(this IEndpointRouteBuilder endpoints, string basePath, CapturedFlowStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        // GET /api/proxy/flows — newest-first snapshot for the workbench tab.
        endpoints.MapGet($"{basePath}/api/proxy/flows", () =>
        {
            var snapshot = store.Snapshot().Select(ProjectSummary).ToArray();
            return Results.Json(new { flows = snapshot }, s_jsonOpts);
        }).ExcludeFromDescription();

        // GET /api/proxy/flows/{id} — full flow (headers + body) for the detail panel.
        endpoints.MapGet($"{basePath}/api/proxy/flows/{{id:long}}", (long id) =>
        {
            var flow = store.Get(id);
            return flow is null
                ? Results.NotFound()
                : Results.Json(ProjectFull(flow), s_jsonOpts);
        }).ExcludeFromDescription();

        // DELETE /api/proxy/flows — workbench "Clear" button.
        endpoints.MapDelete($"{basePath}/api/proxy/flows", () =>
        {
            store.Clear();
            return Results.NoContent();
        }).ExcludeFromDescription();

        // GET /api/proxy/stream — SSE live feed of newly captured flows.
        endpoints.MapGet($"{basePath}/api/proxy/stream", async (HttpContext ctx) =>
        {
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

        // POST /api/proxy/flows/{id}/recording — convert a captured flow into a Bowire recording.
        endpoints.MapPost($"{basePath}/api/proxy/flows/{{id:long}}/recording", (long id) =>
        {
            var flow = store.Get(id);
            if (flow is null) return Results.NotFound();

            var recording = ToRecording(flow);
            return Results.Json(recording, s_jsonOpts);
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static object ProjectSummary(CapturedFlow flow) => new
    {
        id = flow.Id,
        capturedAt = flow.CapturedAt,
        method = flow.Method,
        url = flow.Url,
        scheme = flow.Scheme,
        responseStatus = flow.ResponseStatus,
        latencyMs = flow.LatencyMs,
        error = flow.Error,
        requestBodySize = (flow.RequestBody?.Length) ?? (flow.RequestBodyBase64 is null ? 0 : (flow.RequestBodyBase64.Length * 3 / 4)),
        responseBodySize = (flow.ResponseBody?.Length) ?? (flow.ResponseBodyBase64 is null ? 0 : (flow.ResponseBodyBase64.Length * 3 / 4)),
    };

    private static object ProjectFull(CapturedFlow flow) => new
    {
        id = flow.Id,
        capturedAt = flow.CapturedAt,
        method = flow.Method,
        url = flow.Url,
        scheme = flow.Scheme,
        requestHeaders = flow.RequestHeaders,
        requestBody = flow.RequestBody,
        requestBodyBase64 = flow.RequestBodyBase64,
        responseStatus = flow.ResponseStatus,
        responseHeaders = flow.ResponseHeaders,
        responseBody = flow.ResponseBody,
        responseBodyBase64 = flow.ResponseBodyBase64,
        latencyMs = flow.LatencyMs,
        error = flow.Error,
    };

    /// <summary>Project a captured flow into the on-disk Bowire-recording shape.
    /// The workbench POSTs this back to its own recording-store / opens it in the editor.</summary>
    private static BowireRecording ToRecording(CapturedFlow flow)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in flow.RequestHeaders)
        {
            if (!headers.ContainsKey(k)) headers[k] = v;
        }

        Uri.TryCreate(flow.Url, UriKind.Absolute, out var parsed);
        var httpPath = parsed is null ? flow.Url : parsed.PathAndQuery;

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
            Status = flow.ResponseStatus == 0 ? "Error" : flow.ResponseStatus.ToString(System.Globalization.CultureInfo.InvariantCulture),
            DurationMs = flow.LatencyMs,
            Response = flow.ResponseBody,
            Metadata = headers,
        };

        var recording = new BowireRecording
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"proxy-capture-{flow.Id}",
            Description = $"Captured by bowire proxy at {flow.CapturedAt:O}",
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        recording.Steps.Add(step);
        return recording;
    }
}
