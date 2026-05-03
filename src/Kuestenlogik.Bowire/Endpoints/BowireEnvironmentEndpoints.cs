// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the disk-backed environment endpoints. Environments live at
/// <c>~/.bowire/environments.json</c> via <see cref="EnvironmentStore"/>
/// so config survives browser changes and CLI usage. The browser still
/// keeps a localStorage cache for instant updates without server
/// round-trips — these endpoints are the source of truth.
/// </summary>
internal static class BowireEnvironmentEndpoints
{
    public static IEndpointRouteBuilder MapBowireEnvironmentEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string prefix)
    {
        endpoints.MapGet($"/{prefix}/api/environments", () =>
        {
            return Results.Content(EnvironmentStore.Load(), "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"/{prefix}/api/environments", async (HttpContext ctx) =>
        {
            var json = await new StreamReader(ctx.Request.Body).ReadToEndAsync(ctx.RequestAborted);
            try
            {
                EnvironmentStore.Save(json);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid environments JSON from PUT /api/environments");
                return Results.Json(new { error = "Invalid JSON: " + ex.Message }, BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"/{prefix}/api/environments", () =>
        {
            EnvironmentStore.Save("""{"globals":{},"environments":[],"activeEnvId":""}""");
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
