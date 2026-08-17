// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Linting;
using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Endpoints;

/// <summary>
/// Design-time lint endpoint (#189). <c>POST {basePath}/api/lint</c> takes the
/// discovered service list and returns the findings the shared
/// <see cref="BowireSchemaLinter"/> produces, honouring <c>.bowire/rules.json</c>.
/// <para>
/// The workbench Lint rail POSTs the services it already holds (from
/// <c>/api/services</c>), so this is a thin adapter over the exact Core engine
/// <c>bowire lint</c> drives — CLI / UI parity, and no second discovery pass.
/// </para>
/// </summary>
internal static class BowireLintEndpoints
{
    public static IEndpointRouteBuilder MapBowireLintEndpoints(
        this IEndpointRouteBuilder endpoints, string basePath)
    {
        endpoints.MapPost($"{basePath}/api/lint", async (HttpContext ctx) =>
        {
            LintRequest? request;
            try
            {
                request = await ctx.Request.ReadFromJsonAsync<LintRequest>(
                    BowireEndpointHelpers.JsonOptions, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException)
            {
                return Results.Json(
                    new { error = "Malformed lint request: " + ex.Message },
                    BowireEndpointHelpers.JsonOptions, statusCode: 400);
            }

            var services = request?.Services ?? [];
            var config = TryLoadConfig();
            var findings = BowireSchemaLinter.CreateDefault().Lint(services, config);

            return Results.Json(new
            {
                findings = findings.Select(f => new
                {
                    ruleId = f.RuleId,
                    severity = f.Severity.ToString(),
                    service = f.Service,
                    method = f.Method,
                    field = f.Field,
                    message = f.Message,
                }),
                summary = new
                {
                    total = findings.Count,
                    high = findings.Count(f => f.Severity == BowireLintSeverity.High),
                    medium = findings.Count(f => f.Severity == BowireLintSeverity.Medium),
                    low = findings.Count(f => f.Severity == BowireLintSeverity.Low),
                    info = findings.Count(f => f.Severity == BowireLintSeverity.Info),
                },
            }, BowireEndpointHelpers.JsonOptions);
        }).ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Load <c>.bowire/rules.json</c> if one is discoverable from the host's
    /// working directory. A missing or broken config is non-fatal — the lint
    /// surface must not break because a rules file is malformed.
    /// </summary>
    private static BowireLintConfig? TryLoadConfig()
    {
        var path = BowireLintConfigLoader.DiscoverPath();
        if (path is null) return null;
        try
        {
            return BowireLintConfigLoader.Load(path);
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            _ = ex;
            return null;
        }
    }

    private sealed record LintRequest(List<BowireServiceInfo>? Services);
}
