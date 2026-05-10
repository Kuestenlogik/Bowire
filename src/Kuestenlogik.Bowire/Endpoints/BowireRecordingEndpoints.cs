// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the disk-backed recording endpoints. Recordings live at
/// <c>~/.bowire/recordings.json</c> via <see cref="RecordingStore"/>
/// so a captured "scenario" survives browser changes and CLI usage and
/// can be checked into a repo for sharing. The browser still keeps a
/// localStorage cache for instant updates without server round-trips —
/// these endpoints are the source of truth.
/// </summary>
internal static class BowireRecordingEndpoints
{
    public static IEndpointRouteBuilder MapBowireRecordingEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/recordings", () =>
        {
            return Results.Content(RecordingStore.Load(), "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/recordings", async (HttpContext ctx) =>
        {
            var json = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
            try
            {
                RecordingStore.Save(json);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid recordings JSON from PUT /api/recordings");
                return Results.Json(new { error = "Invalid JSON: " + ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/recordings", () =>
        {
            RecordingStore.Save("""{"recordings":[]}""");
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
