// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the disk-backed collection endpoints. Collections live at
/// <c>~/.bowire/collections.json</c> via <see cref="CollectionStore"/>
/// so curated request groups survive browser changes and CLI usage
/// and can be checked into a repo for sharing. Mirrors the
/// recording-endpoint pattern (browser localStorage is the canonical
/// in-flight cache; this endpoint is the source of truth).
/// </summary>
internal static class BowireCollectionEndpoints
{
    public static IEndpointRouteBuilder MapBowireCollectionEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/collections", () =>
        {
            return Results.Content(CollectionStore.Load(), "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/collections", async (HttpContext ctx) =>
        {
            var json = await new StreamReader(ctx.Request.Body)
                .ReadToEndAsync(ctx.RequestAborted);
            try
            {
                CollectionStore.Save(json);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid collections JSON from PUT /api/collections");
                return Results.Json(
                    new { error = "Invalid JSON: " + ex.Message },
                    BowireEndpointHelpers.JsonOptions,
                    statusCode: 400);
            }
            catch (ArgumentException ex)
            {
                return Results.Json(
                    new { error = ex.Message },
                    BowireEndpointHelpers.JsonOptions,
                    statusCode: 400);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/collections", () =>
        {
            CollectionStore.Save("""{"collections":[]}""");
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
