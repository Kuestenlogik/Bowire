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
/// HTTP API surface for managing UI-driven mock servers (issue #56).
/// Maps four endpoints under <c>{basePath}/api/mocks</c>:
/// <list type="bullet">
///   <item><c>POST /api/mocks</c> — start a mock from a recording payload.</item>
///   <item><c>GET /api/mocks</c> — list running mocks (Mocks panel data source).</item>
///   <item><c>GET /api/mocks/{id}</c> — single mock detail.</item>
///   <item><c>DELETE /api/mocks/{id}</c> — stop a mock.</item>
/// </list>
/// The SSE event stream (request-count tail) is part of issue #57 and
/// not wired here yet.
/// </summary>
/// <remarks>
/// Opt-in: embedded hosts that pull in <c>Kuestenlogik.Bowire.Mock</c>
/// call <c>builder.Services.AddBowireMockManagement()</c> +
/// <c>app.MapBowireMockManagement()</c> alongside the usual
/// <c>AddBowire()</c> / <c>MapBowire()</c>. Standalone <c>bowire</c>
/// CLI wires both automatically. The endpoints stay outside Core
/// because they require the mock package (recordings ingest, Kestrel
/// host) — that's a one-way dependency by design.
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
        endpoints.MapGet($"{basePath}/api/mocks", (MockRegistry registry) =>
        {
            var snapshot = registry.List()
                .OrderByDescending(m => m.StartedAt)
                .Select(MockSummary.From)
                .ToArray();
            return Results.Json(new { mocks = snapshot }, JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}",
            (string mockId, MockRegistry registry) =>
        {
            var inst = registry.Get(mockId);
            return inst is null
                ? Results.NotFound(new { error = $"Mock {mockId} not running." })
                : Results.Json(MockSummary.From(inst), JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/mocks",
            async (HttpContext ctx, MockRegistry registry) =>
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

            if (req is null || string.IsNullOrWhiteSpace(req.Recording))
            {
                return Results.Json(new { error = "recording (JSON document) is required." },
                    JsonOptions, statusCode: 400);
            }

            var displayName = string.IsNullOrWhiteSpace(req.Name) ? "unnamed" : req.Name;
            var port = req.Port ?? 0; // 0 = OS-assigned; resolved via MockServer.Port

            try
            {
                var inst = await registry.StartAsync(req.Recording, displayName, port, ctx.RequestAborted);
                return Results.Json(MockSummary.From(inst), JsonOptions, statusCode: 201);
            }
            catch (Exception ex)
            {
                ctx.RequestServices.GetService<ILoggerFactory>()
                    ?.CreateLogger("BowireMockManagement")
                    .LogError(ex, "Failed to start mock from POST /api/mocks");
                return Results.Json(new { error = ex.Message },
                    JsonOptions, statusCode: 500);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/mocks/{{mockId}}",
            async (string mockId, MockRegistry registry) =>
        {
            var stopped = await registry.StopAsync(mockId);
            return stopped
                ? Results.NoContent()
                : Results.NotFound(new { error = $"Mock {mockId} not running." });
        }).ExcludeFromDescription();

        return endpoints;
    }

    private sealed record StartMockRequest(string Recording, string? Name, int? Port);

    /// <summary>
    /// JSON-friendly projection of a <see cref="MockInstance"/>. Hides
    /// the live <see cref="MockServer"/> reference — the UI doesn't
    /// need it and serializing the host graph would loop.
    /// </summary>
    internal sealed record MockSummary(
        string MockId,
        string RecordingName,
        int Port,
        DateTimeOffset StartedAt)
    {
        public static MockSummary From(MockInstance instance) => new(
            instance.MockId,
            instance.RecordingDisplayName,
            instance.Port,
            instance.StartedAt);
    }
}
