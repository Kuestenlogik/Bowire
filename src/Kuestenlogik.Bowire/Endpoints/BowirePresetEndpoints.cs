// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Disk-backed preset endpoints. Mirrors the collections /
/// recordings layout — the browser writes to localStorage as a
/// best-effort cache and PUTs the same payload here, so a workspace
/// folder's <c>presets/&lt;mode&gt;.json</c> file is the source of
/// truth that survives browser resets, rides the workspace export,
/// and syncs via git.
/// </summary>
internal static class BowirePresetEndpoints
{
    public static IEndpointRouteBuilder MapBowirePresetEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/presets", (HttpContext ctx) =>
        {
            var (mode, modeError) = ReadMode(ctx);
            if (mode is null) return modeError!;
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);
            try
            {
                var json = PresetStore.Load(workspaceId, storageRoot, mode);
                return Results.Content(json, "application/json");
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid preset request", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/presets", async (HttpContext ctx) =>
        {
            var (mode, modeError) = ReadMode(ctx);
            if (mode is null) return modeError!;
            var (workspaceId, storageRoot) = ReadWorkspace(ctx);

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            try
            {
                PresetStore.Save(workspaceId, storageRoot, mode, body);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid presets JSON for mode {Mode}",
                    BowireEndpointHelpers.SafeLog(mode));
                return Problem(ctx, "Invalid JSON", 400, ex.Message);
            }
            catch (ArgumentException ex)
            {
                return Problem(ctx, "Invalid preset payload", 400, ex.Message);
            }
        }).ExcludeFromDescription();

        return endpoints;
    }

    private static (string? mode, IResult? error) ReadMode(HttpContext ctx)
    {
        var mode = ctx.Request.Query["mode"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(mode))
        {
            return (null, Problem(ctx, "Missing 'mode' query parameter", 400,
                "Pass ?mode=<discover|benchmarks|mocks|proxy|security|flows>."));
        }
        return (mode, null);
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
