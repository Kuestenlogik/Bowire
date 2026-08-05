// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Disk-backed mock-configuration endpoints (#558). A schema mock's
/// refinement sidecar (per-field overrides + conditional rules + auth
/// requirement) is persisted per (workspace, mock) via
/// <see cref="MockConfigStore"/>; the workbench reads and writes it here so
/// the on-disk <c>mocks/&lt;mockId&gt;.json</c> is the source of truth that
/// survives browser resets, rides the workspace export, and syncs via git.
/// Mirrors <see cref="BowirePresetEndpoints"/>.
/// </summary>
internal static class BowireMockConfigEndpoints
{
    public static IEndpointRouteBuilder MapBowireMockConfigEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/mocks/{{mockId}}/config", (string mockId, HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            try
            {
                var json = MockConfigStore.Load(workspaceId, storageRoot, mockId);
                return Results.Content(json, "application/json");
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid mock-config request", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/mocks/{{mockId}}/config", async (string mockId, HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            try
            {
                MockConfigStore.Save(workspaceId, storageRoot, mockId, body);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid mock-config JSON for mock {MockId}",
                    BowireEndpointHelpers.SafeLog(mockId));
                return Problem(ctx, "Invalid JSON", 400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid mock-config payload", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static (string workspaceId, string? storageRoot) ReadWorkspace(HttpContext ctx)
    {
        var workspaceId = ctx.Request.Query["workspaceId"].FirstOrDefault() ?? string.Empty;
        var storageRoot = ctx.Request.Query["storageRoot"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(storageRoot)) storageRoot = null;
        return (workspaceId, storageRoot);
    }

    private static IResult Problem(HttpContext ctx, string title, int status, string detail)
    {
        return Results.Json(
            new { error = title + ": " + detail },
            BowireEndpointHelpers.JsonOptions,
            statusCode: status);
    }
}
