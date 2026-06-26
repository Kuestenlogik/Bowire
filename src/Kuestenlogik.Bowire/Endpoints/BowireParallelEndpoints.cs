// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Parallel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// REST surface for #132 Phase 2 — parallel sessions across multiple
/// Bowire hosts (distributed).
///
/// <para>
/// Two routes:
/// </para>
/// <list type="bullet">
///   <item><c>POST {basePath}/api/parallel/start-local</c> — per-host
///   worker. Runs <c>sessionCount</c> concurrent in-process sessions
///   against the supplied target list and returns the aggregated
///   per-target + per-session results. Same path the coordinator
///   fans out to.</item>
///   <item><c>POST {basePath}/api/parallel/start</c> — coordinator.
///   Takes <c>hosts: [url, ...]</c>, shards the requested session
///   count across them, POSTs each host's <c>/start-local</c> in
///   parallel, and returns the merged response. With no hosts the
///   coordinator collapses to a pure in-process run — same shape
///   as <c>/start-local</c>.</item>
/// </list>
/// </summary>
internal static class BowireParallelEndpoints
{
    public static IEndpointRouteBuilder MapBowireParallelEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapPost($"{basePath}/api/parallel/start-local", async (HttpContext ctx) =>
        {
            BowireParallelLocalRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<BowireParallelLocalRequest>(
                    ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
            if (req is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Missing request body",
                    status: 400,
                    detail: "POST /api/parallel/start-local requires a JSON body { targets, sessionCount }.",
                    instance: ctx.Request.Path);
            }
            var config = ctx.RequestServices.GetService<IConfiguration>();
            var logger = BowireEndpointHelpers.GetLogger(ctx);
            var result = await BowireParallelRunner.RunAsync(
                req, config, logger, ctx.RequestAborted);
            return Results.Json(result, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        endpoints.MapPost($"{basePath}/api/parallel/start", async (HttpContext ctx) =>
        {
            BowireParallelDistributedRequest? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<BowireParallelDistributedRequest>(
                    ctx.Request.Body, BowireEndpointHelpers.JsonOptions, ctx.RequestAborted);
            }
            catch (JsonException ex)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Request body isn't valid JSON",
                    status: 400,
                    detail: ex.Message,
                    instance: ctx.Request.Path);
            }
            if (req is null)
            {
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:invalid-input",
                    title: "Missing request body",
                    status: 400,
                    detail: "POST /api/parallel/start requires a JSON body { targets, sessions, hosts? }.",
                    instance: ctx.Request.Path);
            }
            var config = ctx.RequestServices.GetService<IConfiguration>();
            var logger = BowireEndpointHelpers.GetLogger(ctx);
            var result = await BowireParallelCoordinator.RunAsync(
                req, config, logger, ctx.RequestAborted);
            return Results.Json(result, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }
}
