// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Maps the disk-backed flow endpoints (#641).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a copy of the collection endpoints down to the error shapes.
/// Flows were the last major artifact with no server side at all, and the
/// reason that went unnoticed for so long is that each of its symptoms looked
/// like a separate bug: two MCP resources reading a file nothing wrote,
/// <c>bowire test</c> unable to see anything built in the workbench, flows
/// missing from a git-native workspace, flows outside the per-identity slot.
/// One store and one pair of endpoints, in the shape the others already use,
/// answers all four.
/// </para>
/// </remarks>
internal static class BowireFlowEndpoints
{
    public static IEndpointRouteBuilder MapBowireFlowEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/flows", (HttpContext ctx) =>
        {
            var scope = WorkspaceScopeQuery.From(ctx);
            if (scope.IsInvalid)
                return Results.Json(new { error = scope.Error }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

            return Results.Content(
                FlowStore.Load(scope.WorkspaceId, scope.StorageRoot),
                "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/flows", async (HttpContext ctx) =>
        {
            var scope = WorkspaceScopeQuery.From(ctx);
            if (scope.IsInvalid)
                return Results.Json(new { error = scope.Error }, BowireEndpointHelpers.JsonOptions, statusCode: 400);

            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync(ctx.RequestAborted);
            try
            {
                FlowStore.Save(json, scope.WorkspaceId, scope.StorageRoot);
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid flows JSON from PUT /api/flows");
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

        return endpoints;
    }
}
