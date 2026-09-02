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
    /// <summary>
    /// What the host declared through <c>AddBowireEnvironment</c>, or nothing
    /// — which is every host that never called it.
    /// </summary>
    private static IReadOnlyList<BowireProvisionedEnvironment> Provisioned(HttpContext ctx)
        => ctx.RequestServices.GetServices<BowireProvisionedEnvironment>().ToList();


    public static IEndpointRouteBuilder MapBowireEnvironmentEndpoints(
        this IEndpointRouteBuilder endpoints, BowireOptions options, string basePath)
    {
        endpoints.MapGet($"{basePath}/api/environments", (HttpContext ctx) =>
        {
            // #49 — whatever the host declared rides along with what the
            // person saved. Marked so the workbench can render it as the
            // host's rather than as something to edit.
            var provisioned = Provisioned(ctx);
            return Results.Content(
                BowireProvisionedEnvironments.Merge(EnvironmentStore.Load(), provisioned),
                "application/json");
        }).ExcludeFromDescription();

        endpoints.MapPut($"{basePath}/api/environments", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var json = await reader.ReadToEndAsync(ctx.RequestAborted);
            try
            {
                // #49 — and they must not come back the other way. The
                // workbench sends the whole envelope on every change, so
                // without this a declared environment would be saved on the
                // first edit anybody made and then exist twice: once declared,
                // once stored, diverging as soon as the host's configuration
                // moved.
                EnvironmentStore.Save(BowireProvisionedEnvironments.Strip(json, Provisioned(ctx)));
                return Results.Json(new { saved = true }, BowireEndpointHelpers.JsonOptions);
            }
            catch (JsonException ex)
            {
                BowireEndpointHelpers.GetLogger(ctx).LogWarning(ex,
                    "Rejected invalid environments JSON from PUT /api/environments");
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
        }).ExcludeFromDescription();

        endpoints.MapDelete($"{basePath}/api/environments", () =>
        {
            EnvironmentStore.Save("""{"globals":{},"environments":[],"activeEnvId":""}""");
            return Results.Json(new { cleared = true }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
