// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// Map-time wiring for the Bowire MCP adapter. The DI-time half lives
/// in <see cref="BowireMcpAdapterServiceCollectionExtensions.AddBowireMcpAdapter"/>;
/// this just mounts the SDK's streamable-HTTP transport on the host's
/// route table plus a small info-endpoint the workbench's Settings →
/// General tab consults.
/// </summary>
/// <remarks>
/// <para>
/// <b>Security warning:</b> mounting this endpoint allows any MCP
/// client to invoke any discovered API method. Do not enable in
/// production unless the surface is intentional.
/// </para>
/// <para>
/// Usage:
/// </para>
/// <code>
/// builder.Services.AddBowireMcpAdapter("http://localhost:5005");
/// var app = builder.Build();
/// app.MapBowire(opts =&gt; opts.Title = "My API");
/// app.MapBowireMcpAdapter("/bowire");   // mounts POST /bowire/mcp
/// </code>
/// </remarks>
public static class McpAdapterEndpoints
{
    /// <summary>
    /// Mount the MCP adapter at <c>{prefix}/mcp</c>. Wraps the SDK's
    /// <c>app.MapMcp(...)</c> so callers don't have to remember the
    /// trailing path segment, and adds the info-endpoint the
    /// workbench reads back.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">
    /// URL prefix the adapter mounts under. Defaults to <c>/bowire</c>
    /// to match the workbench prefix; pass <c>""</c> to mount at the
    /// site root (the standalone CLI does this).
    /// </param>
    public static IEndpointRouteBuilder MapBowireMcpAdapter(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/bowire")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Normalise like BowireApiEndpoints.Map does so an empty
        // prefix collapses cleanly (standalone CLI mounts at site
        // root and passes "" here).
        var trimmed = prefix.TrimStart('/').TrimEnd('/');
        var basePath = trimmed.Length == 0 ? string.Empty : "/" + trimmed;

        // The SDK mounts the streamable-HTTP transport at the given
        // path: POST <pattern> handles incoming JSON-RPC, GET <pattern>
        // serves the server-stream half (notifications, sampling).
        endpoints.MapMcp($"{basePath}/mcp");

        // Read-only info endpoint the workbench's Settings → General
        // tab consults to render the MCP-adapter status row. Returns
        // 200 + { enabled: true, path } whenever this method has been
        // called; the absence of the route (a plain 404) is the
        // "disabled" signal the frontend reads back.
        var adapterPath = $"{basePath}/mcp";
        endpoints.MapGet($"{basePath}/api/mcp-adapter", () =>
            Results.Ok(new { enabled = true, path = adapterPath }))
            .ExcludeFromDescription();

        return endpoints;
    }
}
