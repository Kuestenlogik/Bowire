// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// Maps the MCP adapter — exposes Bowire's discovered API services as MCP
/// tools so AI agents (Claude, Copilot, Cursor) can call them. This is a
/// development-time feature: every discovered method becomes a tool the AI
/// agent can invoke. <b>Opt-in only</b> — call this explicitly to avoid
/// accidentally exposing an internal API surface to AI clients.
/// </summary>
public static class McpAdapterEndpoints
{
    /// <summary>
    /// Chainable opt-in: enables the MCP adapter on the same endpoint route
    /// builder used for <c>MapBowire()</c>. This extension only exists when
    /// the <c>Kuestenlogik.Bowire.Protocol.Mcp</c> package is referenced — projects
    /// that don't depend on it cannot accidentally activate the adapter.
    /// </summary>
    /// <example>
    /// <code>
    /// app.MapBowire(opts => opts.Title = "My API")
    ///    .WithMcpAdapter("http://localhost:5005");
    /// </code>
    /// </example>
    public static IEndpointRouteBuilder WithMcpAdapter(
        this IEndpointRouteBuilder endpoints,
        string? serverUrl = null,
        string prefix = "bowire")
    {
        var registry = BowireProtocolRegistry.Discover();
        foreach (var protocol in registry.Protocols)
            protocol.Initialize(endpoints.ServiceProvider);

        // Normalise the same way BowireApiEndpoints.Map does so an empty
        // prefix collapses cleanly — the standalone CLI mounts at site
        // root and passes "" here.
        var trimmed = prefix.TrimStart('/').TrimEnd('/');
        var basePath = trimmed.Length == 0 ? string.Empty : "/" + trimmed;

        return endpoints.MapBowireMcpAdapter(registry, serverUrl ?? "http://localhost", basePath);
    }

    /// <summary>
    /// Maps the MCP adapter at <c>POST {prefix}/mcp</c> — the modern
    /// MCP <i>streamable HTTP</i> transport (MCP 2025-03-26 spec): a single
    /// endpoint, JSON-RPC 2.0 in, JSON out. Every method that any registered
    /// protocol can discover is wrapped as an MCP tool. Call <b>after</b>
    /// <c>MapBowire()</c> and only if you want to expose the discovered
    /// services to AI agents.
    /// </summary>
    /// <remarks>
    /// <b>Security warning:</b> this endpoint allows any MCP client to invoke
    /// any discovered API method. Do not enable in production unless the
    /// surface is intentional.
    /// </remarks>
    public static IEndpointRouteBuilder MapBowireMcpAdapter(
        this IEndpointRouteBuilder endpoints,
        BowireProtocolRegistry registry,
        string serverUrl,
        string basePath = "/bowire")
    {
        var server = new McpAdapterServer(registry, serverUrl);

        endpoints.MapPost($"{basePath}/mcp", HandleMessage).ExcludeFromDescription();

        return endpoints;

        async Task HandleMessage(HttpContext ctx)
        {
            JsonElement message;
            try
            {
                message = await JsonSerializer.DeserializeAsync<JsonElement>(
                    ctx.Request.Body, cancellationToken: ctx.RequestAborted);
            }
            catch
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid JSON" });
                return;
            }

            var response = await server.HandleMessageAsync(message, ctx.RequestAborted);

            if (response.ValueKind == JsonValueKind.Undefined)
            {
                ctx.Response.StatusCode = 204; // Notification -- no response
                return;
            }

            await ctx.Response.WriteAsJsonAsync(response);
        }
    }
}
