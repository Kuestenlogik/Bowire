// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Schema-change log endpoints (#185). The browser's schema watch
/// posts each poll's diff here; the workbench pill / rail badge /
/// change-log dropdown hydrate from the GET. Same workspace scoping
/// convention as the preset endpoints: <c>?workspaceId=</c> +
/// optional <c>?storageRoot=</c> query params.
/// </summary>
internal static class BowireSchemaChangeEndpoints
{
    // Element type is nullable on purpose: a JSON `null` array element
    // deserialises as a null entry, and the store turns that into the
    // ArgumentException → 400 it deserves.
    private sealed record AppendRequest(List<SchemaChangeEntry?>? Entries);

    public static IEndpointRouteBuilder MapBowireSchemaChangeEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/schema-changes", (HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            var store = ctx.RequestServices.GetRequiredService<SchemaChangeLogStore>();
            try
            {
                return Results.Json(store.Load(workspaceId, storageRoot), BowireEndpointHelpers.JsonOptions);
            }
            catch (ArgumentException ex)
            {
                return Problem("Invalid schema-change request", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/schema-changes", async (HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            var store = ctx.RequestServices.GetRequiredService<SchemaChangeLogStore>();
            // ReadFromJsonAsync throws InvalidOperationException (not
            // JsonException) on a non-JSON content type — gate up front
            // so garbage in stays a 4xx, like every other bad payload.
            if (!ctx.Request.HasJsonContentType())
                return Problem("Unsupported content type", 415, "POST application/json.");
            try
            {
                var body = await ctx.Request.ReadFromJsonAsync<AppendRequest>(
                    BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
                if (body?.Entries is not { Count: > 0 })
                    return Problem("Missing entries", 400, "POST body must be { \"entries\": [ ... ] }.");
                return Results.Json(
                    store.Append(workspaceId, storageRoot, body.Entries),
                    BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid schema-change JSON for workspace {WorkspaceId}",
                    BowireEndpointHelpers.SafeLog(workspaceId));
                return Problem("Invalid JSON", 400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Problem("Invalid schema-change payload", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/schema-changes/read", (HttpContext ctx) =>
        {
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            var store = ctx.RequestServices.GetRequiredService<SchemaChangeLogStore>();
            try
            {
                return Results.Json(store.MarkRead(workspaceId, storageRoot), BowireEndpointHelpers.JsonOptions);
            }
            catch (ArgumentException ex)
            {
                return Problem("Invalid schema-change request", 400, ex.Message);
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

    private static IResult Problem(string title, int status, string detail)
    {
        return Results.Json(
            new { error = title + ": " + detail },
            BowireEndpointHelpers.JsonOptions,
            statusCode: status);
    }
}
