// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Management;

/// <summary>
/// HTTP API surface for managing UI-driven mock servers. Five endpoints
/// under <c>{basePath}/api/mocks</c>:
/// <list type="bullet">
///   <item><c>POST /api/mocks</c> — start a mock. Accepts either an
///     inline recording payload (<c>{ recording, name?, port? }</c> —
///     the legacy embedded-host shape) OR a recording-id lookup
///     (<c>{ recordingId, label? }</c> — the "Use as mock" shape).</item>
///   <item><c>GET /api/mocks</c> — list running mocks.</item>
///   <item><c>GET /api/mocks/{id}</c> — single mock detail.</item>
///   <item><c>DELETE /api/mocks/{id}</c> — stop a mock.</item>
///   <item><c>GET /api/mocks/{id}/requests</c> — request-log tail (#57).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>One owner — <see cref="BowireMockHostManager"/>. Replaces the
/// v1.x split with a separate <c>MockRegistry</c> + the v2.x
/// host-manager-only <c>/api/mock/*</c> surface (#223).</para>
/// <para>Opt-in: embedded hosts that pull in <c>Kuestenlogik.Bowire.Mock</c>
/// call <c>builder.Services.AddBowireMockManagement()</c> +
/// <c>app.MapBowireMockManagement()</c> alongside the usual
/// <c>AddBowire()</c> / <c>MapBowire()</c>. Standalone <c>bowire</c>
/// CLI wires both automatically.</para>
/// </remarks>
public static class BowireMockManagementEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Mount the mock-management endpoints under <paramref name="basePath"/>
    /// (typically <c>"/bowire"</c> for the workbench mount).
    /// </summary>
    public static IEndpointRouteBuilder MapBowireMockManagement(
        this IEndpointRouteBuilder endpoints, string basePath = "/bowire")
    {
        endpoints.MapGet($"{basePath}/api/mocks", (BowireMockHostManager manager) =>
        {
            var snapshot = manager.List()
                .OrderByDescending(h => h.StartedAtUtc)
                .Select(MockSummary.From)
                .ToArray();
            return Results.Json(new { mocks = snapshot }, JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}",
            (string mockId, BowireMockHostManager manager) =>
        {
            var handle = manager.Get(mockId);
            return handle is null
                ? Results.NotFound(new { error = $"Mock {mockId} not running." })
                : Results.Json(MockSummary.From(handle), JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mocks",
            async (HttpContext ctx, BowireMockHostManager manager) =>
        {
            StartMockRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<StartMockRequest>(
                    ctx.Request.Body, JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return Results.Json(new { error = "Invalid JSON: " + ex.Message },
                    JsonOptions, statusCode: 400);
            }
            if (req is null)
            {
                return Results.Json(new { error = "Request body is required." },
                    JsonOptions, statusCode: 400);
            }

            // Two start shapes; pick based on which field arrived.
            //   { recording, name?, port? }    — inline payload (legacy + embedded)
            //   { recordingId, label? }        — lookup via IRecordingJsonProvider ("Use as mock")
            string? recordingJson;
            string recordingId;
            string label;

            if (!string.IsNullOrWhiteSpace(req.RecordingId))
            {
                var lookup = ctx.RequestServices.GetService<IRecordingJsonProvider>();
                if (lookup is null)
                {
                    return Results.Json(
                        new { error = "No IRecordingJsonProvider registered — embedded hosts must pass `recording` inline." },
                        JsonOptions, statusCode: 500);
                }
                try { recordingJson = lookup.TryGetRecordingJson(req.RecordingId); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                {
                    return Results.Json(
                        new { error = "Couldn't read the recording: " + ex.Message },
                        JsonOptions, statusCode: 500);
                }
                if (recordingJson is null)
                {
                    return Results.Json(
                        new { error = $"No recording with id '{req.RecordingId}'" },
                        JsonOptions, statusCode: 404);
                }
                recordingId = req.RecordingId;
                label = string.IsNullOrWhiteSpace(req.Label) ? req.RecordingId : req.Label!;
            }
            else if (!string.IsNullOrWhiteSpace(req.Recording))
            {
                recordingJson = req.Recording;
                recordingId = string.Empty;
                label = string.IsNullOrWhiteSpace(req.Name) ? "unnamed" : req.Name!;
            }
            else
            {
                return Results.Json(
                    new { error = "Body must carry either `recording` (inline JSON) or `recordingId` (lookup)." },
                    JsonOptions, statusCode: 400);
            }

            var port = req.Port ?? 0; // 0 = OS-assigned via the rolling allocator

#pragma warning disable CA1031 // mock-start spawns the full host pipeline; unbounded failure surface
            try
            {
                var handle = await manager.StartAsync(recordingJson!, recordingId, label, port, ctx.RequestAborted);
                return Results.Json(MockSummary.From(handle), JsonOptions, statusCode: 201);
            }
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ctx.RequestServices.GetService<ILoggerFactory>()
                    ?.CreateLogger("BowireMockManagement")
                    .LogError(ex, "Failed to start mock from POST /api/mocks");
                return Results.Json(new { error = ex.Message },
                    JsonOptions, statusCode: 500);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/mocks/{{mockId}}",
            async (string mockId, BowireMockHostManager manager, HttpContext ctx) =>
        {
            var stopped = await manager.StopAsync(mockId, ctx.RequestAborted);
            return stopped
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Mock {mockId} not running." });
        }).ExcludeFromDescription();

        // #57: per-mock request log. Query params:
        //   limit         -> max entries (default 100, capped at log capacity)
        //   since         -> minimum sequence number (>0 means "give me only
        //                    entries newer than the cursor I last saw")
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/requests",
            (string mockId, int? limit, long? since, BowireMockHostManager manager) =>
        {
            var log = manager.GetRequestLog(mockId);
            if (log is null)
            {
                return Results.NotFound(new { error = $"Mock {mockId} not running." });
            }
            var entries = log.Snapshot(limit ?? 100, since ?? 0);
            return Results.Json(new
            {
                mockId,
                total = log.TotalRequests,
                capacity = log.Capacity,
                entries
            }, JsonOptions);
        }).ExcludeFromDescription();

        // #170: per-method fault-injection rules of a RUNNING mock.
        // GET returns the live rule set in the exact mock-faults.json
        // shape (kebab-case enums); PUT replaces it — an empty rules
        // array clears injection. The body is validated by
        // FaultRuleSet.LoadJson, so a bad rule comes back as a 400 with
        // the offending rule named, never as a half-applied set.
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/faults",
            (string mockId, BowireMockHostManager manager) =>
        {
            var faults = manager.GetFaults(mockId);
            return faults is null
                ? Results.NotFound(new { error = $"Mock {mockId} not running." })
                : Results.Text(faults.ToJson(), "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/mocks/{{mockId}}/faults",
            async (string mockId, HttpRequest request, BowireMockHostManager manager) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted).ConfigureAwait(false);
            Chaos.FaultRuleSet faults;
            try
            {
                faults = Chaos.FaultRuleSet.LoadJson(body, allowEmpty: true);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            return manager.TrySetFaults(mockId, faults)
                ? Results.Text(faults.ToJson(), "application/json")
                : Results.NotFound(new { error = $"Mock {mockId} not running." });
        }).ExcludeFromDescription();

        return endpoints;
    }

    private sealed record StartMockRequest(
        string? Recording,
        string? Name,
        int? Port,
        string? RecordingId,
        string? Label);

    /// <summary>
    /// JSON-friendly projection of a <see cref="MockHostHandle"/>. Same
    /// shape the workbench rail consumes from <c>GET /api/mocks</c>.
    /// </summary>
    internal sealed record MockSummary(
        string MockId,
        string RecordingId,
        string RecordingName,
        string Label,
        int Port,
        string Url,
        DateTime StartedAt)
    {
        public static MockSummary From(MockHostHandle handle) => new(
            handle.MockId,
            handle.RecordingId,
            handle.Label,
            handle.Label,
            handle.Port,
            handle.Url,
            handle.StartedAtUtc);
    }
}
