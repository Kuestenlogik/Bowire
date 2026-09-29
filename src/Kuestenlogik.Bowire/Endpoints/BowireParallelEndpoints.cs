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
/// Bowire hosts (distributed) — with the #313 hardening.
///
/// <para>
/// Two routes:
/// </para>
/// <list type="bullet">
///   <item><c>POST {basePath}/api/parallel/start-local</c> — per-host
///   worker. Runs <c>sessionCount</c> concurrent in-process sessions
///   against the supplied target list and returns the aggregated
///   per-target + per-session results. Same path the coordinator
///   fans out to. Requires the configured token, if there is one, and
///   refuses targets outside the configured allowlist (403).</item>
///   <item><c>POST {basePath}/api/parallel/start</c> — coordinator.
///   Takes <c>hosts: [url, ...]</c>, shards the requested session
///   count across them, POSTs each host's <c>/start-local</c> in
///   parallel, and returns the merged response. With no hosts the
///   coordinator collapses to a pure in-process run — same shape
///   as <c>/start-local</c>, under this host's allowlist.</item>
/// </list>
/// <para>
/// Both write to <see cref="BowireParallelAuditLog"/> when the host
/// registered one (<c>AddBowire</c> does).
/// </para>
/// </summary>
internal static class BowireParallelEndpoints
{
    /// <summary>Header the coordinator tags each executor call with, so both sides' audit lines join up.</summary>
    internal const string JobHeader = "X-Bowire-Parallel-Job";

    public static IEndpointRouteBuilder MapBowireParallelEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapPost($"{basePath}/api/parallel/start-local", async (HttpContext ctx) =>
        {
            var config = ctx.RequestServices.GetService<IConfiguration>();
            var policy = BowireParallelPolicy.From(config);
            var audit = ctx.RequestServices.GetService<BowireParallelAuditLog>();
            var caller = ctx.Connection.RemoteIpAddress?.ToString();
            var job = ctx.Request.Headers[JobHeader].FirstOrDefault();

            // Before the body is read: an unauthorised caller learns nothing
            // about what would have been accepted.
            if (!policy.Authorizes(ctx.Request.Headers.Authorization.FirstOrDefault()))
            {
                audit?.Record("unauthorized", new { caller, job });
                return BowireEndpointHelpers.Problem(
                    type: "urn:bowire:parallel-unauthorized",
                    title: "Missing or wrong parallel-run token",
                    status: 401,
                    detail: "This executor requires Authorization: Bearer <Bowire:Parallel:Token>.",
                    instance: ctx.Request.Path);
            }

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

            if (Refuse(policy, req, audit, caller, job, ctx) is { } refusal) return refusal;

            var logger = BowireEndpointHelpers.GetLogger(ctx);
            var result = await BowireParallelRunner.RunAsync(
                req, config, logger, ctx.RequestAborted);
            audit?.Record("run", new
            {
                caller,
                job,
                targets = req.Targets.Select(t => t.Url).Distinct(StringComparer.Ordinal).ToArray(),
                sessions = req.SessionCount,
                pass = result.PassCount,
                fail = result.FailCount,
                durationMs = result.TotalDurationMs,
            });
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
            var policy = BowireParallelPolicy.From(config);
            var audit = ctx.RequestServices.GetService<BowireParallelAuditLog>();
            var caller = ctx.Connection.RemoteIpAddress?.ToString();
            var job = Guid.NewGuid().ToString("N");

            // With no hosts this host runs the targets itself, so its own
            // allowlist governs them. With hosts, each executor applies its own.
            var hasHosts = req.Hosts?.Any(h => !string.IsNullOrWhiteSpace(h)) == true;
            if (!hasHosts && Refuse(policy, req, audit, caller, job, ctx) is { } refusal) return refusal;

            var logger = BowireEndpointHelpers.GetLogger(ctx);
            var result = await BowireParallelCoordinator.RunAsync(
                req, config, logger, ctx.RequestAborted, policy, job);
            audit?.Record("dispatch", new
            {
                caller,
                job,
                targets = req.Targets.Select(t => t.Url).Distinct(StringComparer.Ordinal).ToArray(),
                sessions = req.SessionCount,
                requireSignedExecutor = policy.RequireSignedExecutor,
                hosts = result.Hosts?.Select(h => new { host = h.Host, sessions = h.SessionCount, pass = h.PassCount, fail = h.FailCount, error = h.Error }).ToArray(),
                pass = result.PassCount,
                fail = result.FailCount,
            });
            return Results.Json(result, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>A 403 naming the targets outside the allowlist, each also written to the audit log; null when none is.</summary>
    private static IResult? Refuse(
        BowireParallelPolicy policy, BowireParallelLocalRequest req, BowireParallelAuditLog? audit,
        string? caller, string? job, HttpContext ctx)
    {
        var refused = policy.RefusedTargets(req.Targets.Select(t => t.Url));
        if (refused.Count == 0) return null;
        foreach (var target in refused)
            audit?.Record("refused", new { caller, job, target, reason = "not in Bowire:Parallel:TargetAllowlist" });
        return BowireEndpointHelpers.Problem(
            type: "urn:bowire:parallel-target-refused",
            title: "Targets outside this host's allowlist",
            status: 403,
            detail: "Bowire:Parallel:TargetAllowlist does not cover: " + string.Join(", ", refused),
            instance: ctx.Request.Path);
    }
}
