// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.AgentHub;

/// <summary>
/// The hub side of #128: a registry every Bowire agent pushes itself into.
/// </summary>
/// <remarks>
/// <list type="table">
///   <item><term><c>POST /hub/agents</c></term><description>register or heartbeat — idempotent on (serviceName, instanceId); answers the agent's id</description></item>
///   <item><term><c>GET /hub/agents</c></term><description>every known agent with <c>lastSeen</c> and <c>live</c></description></item>
///   <item><term><c>GET /hub/agents/catalogue</c></term><description>the live agents' catalogue entries, in the shape <c>Kuestenlogik.Bowire.Catalogue.Agent</c> reads</description></item>
///   <item><term><c>DELETE /hub/agents/{id}</c></term><description>deregister</description></item>
/// </list>
/// The hub does not proxy to its agents: it lists them and links to their
/// own workbench.
/// </remarks>
public static class BowireHubEndpoints
{
    /// <summary>
    /// Maps the Bowire hub under <paramref name="pattern"/> (default
    /// <c>/hub</c>, which is where agents and the agent catalogue look).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to attach to.</param>
    /// <param name="pattern">The hub's route prefix.</param>
    /// <returns>The same <paramref name="endpoints"/>, for chaining.</returns>
    public static IEndpointRouteBuilder MapBowireHub(this IEndpointRouteBuilder endpoints, string pattern = "/hub")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(pattern);
        var registry = endpoints.ServiceProvider.GetService<BowireHubRegistry>() ?? new BowireHubRegistry();
        var policy = BowireHubPolicy.From(endpoints.ServiceProvider.GetService<IConfiguration>());
        var hub = endpoints.MapGroup(pattern.TrimEnd('/'));
        hub.ExcludeFromDescription();

        hub.MapPost("/agents", (HttpContext ctx, BowireAgentRegistration? registration) =>
        {
            if (!policy.MayWrite(ctx)) return Results.Unauthorized();
            if (registration is null
                || string.IsNullOrWhiteSpace(registration.ServiceName)
                || string.IsNullOrWhiteSpace(registration.InstanceId))
            {
                return Results.BadRequest(new { error = "serviceName and instanceId are required" });
            }
            if (policy.CallbackRefusal(registration.CallbackUrl) is { } refusal)
            {
                return Results.BadRequest(new { error = refusal });
            }
            var id = registry.Register(registration);
            return Results.Ok(new { agentId = id });
        });

        hub.MapGet("/agents", (HttpContext ctx) =>
            policy.MayRead(ctx) ? Results.Ok(new { agents = registry.List() }) : Results.Unauthorized());

        hub.MapGet("/agents/catalogue", (HttpContext ctx) =>
        {
            if (!policy.MayRead(ctx)) return Results.Unauthorized();
            var agents = registry.List().Where(a => a.Live).Select(a => new
            {
                agentId = a.AgentId,
                serviceName = a.ServiceName,
                tags = a.Tags,
                entries = a.Entries,
            });
            return Results.Ok(new { version = 1, agents });
        });

        hub.MapDelete("/agents/{id}", (HttpContext ctx, string id) =>
        {
            if (!policy.MayWrite(ctx)) return Results.Unauthorized();
            return registry.Remove(id) ? Results.NoContent() : Results.NotFound();
        });

        return endpoints;
    }

    /// <summary>
    /// The workbench's own view of the hub, inside its auth-gated group:
    /// <c>{basePath}/api/hub/agents</c>. Answers <c>enabled: false</c> when
    /// this Bowire is not a hub, so the UI knows to leave the list out.
    /// </summary>
    internal static IEndpointRouteBuilder MapBowireHubWorkbenchEndpoints(this IEndpointRouteBuilder group, string basePath, bool enabled)
    {
        group.MapGet($"{basePath}/api/hub/agents", (HttpContext ctx) =>
        {
            var registry = enabled ? ctx.RequestServices.GetService<BowireHubRegistry>() : null;
            return Results.Ok(new { enabled = registry is not null, agents = registry?.List() ?? [] });
        }).ExcludeFromDescription();
        return group;
    }

    /// <summary><c>Bowire:Hub:Enabled</c>, or <c>BOWIRE_HUB_ENABLED=true</c>.</summary>
    internal static bool IsEnabled(IConfiguration? config) =>
        config?.GetValue<bool?>("Bowire:Hub:Enabled")
        ?? string.Equals(Environment.GetEnvironmentVariable("BOWIRE_HUB_ENABLED"), "true", StringComparison.OrdinalIgnoreCase);
}
