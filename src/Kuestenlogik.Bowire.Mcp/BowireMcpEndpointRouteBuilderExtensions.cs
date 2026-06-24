// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// Map-time wiring for the full Bowire MCP server (the side that
/// exposes the workbench's own tooling: <c>bowire.discover</c>,
/// <c>bowire.invoke</c>, <c>bowire.mock.*</c>, &amp;c). Pairs with
/// the DI registration in
/// <see cref="BowireMcpServiceCollectionExtensions.AddBowireMcp"/>.
/// </summary>
/// <remarks>
/// <para>
/// The companion <c>McpAdapterEndpoints.MapBowireMcpAdapter</c>
/// (from <c>Kuestenlogik.Bowire.Protocol.Mcp</c>) mounts the
/// adapter-mode endpoint. Both extensions push their mount path into
/// the same <see cref="BowireMcpEndpointRegistry"/> singleton so they
/// can coexist on a single host: each mount is served at a distinct
/// route prefix, overlapping prefixes throw
/// <see cref="InvalidOperationException"/> at startup, and the
/// dispatcher (<see cref="BowireMcpDualHandlerDispatcher"/>) routes
/// per-request to the right handler based on
/// <see cref="HttpContext.Request"/> path.
/// </para>
/// <para>
/// Usage in an embedded host:
/// </para>
/// <code>
/// builder.Services.AddBowireMcp();
/// builder.Services.AddBowireMcpAdapter("http://localhost:5005");
/// var app = builder.Build();
/// app.MapBowire();
/// app.MapBowireMcp();              // full server at /bowire/mcp
/// app.MapBowireMcpAdapter();       // adapter at /bowire/mcp/adapter
/// // manifest at /bowire/mcp/manifest (auto-mounted by either Map* call)
/// </code>
/// </remarks>
public static class BowireMcpEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Canonical mount path for the workbench-discoverable manifest
    /// endpoint. Kept stable across hosts (standalone CLI, embedded)
    /// so the workbench's MCP-introspection panel can probe a single
    /// well-known URL.
    /// </summary>
    public const string ManifestPath = "/bowire/mcp/manifest";

    /// <summary>
    /// Mount the full Bowire MCP server at <paramref name="prefix"/>.
    /// Defaults to <c>/bowire/mcp</c>; pass a different prefix when
    /// you need to co-exist with another mount at that path, but
    /// be aware that <c>MapBowireMcpAdapter</c> (from
    /// <c>Kuestenlogik.Bowire.Protocol.Mcp</c>) defaults to
    /// <c>/bowire/mcp/adapter</c> — overlapping the two throws at
    /// startup.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">
    /// URL prefix the streamable-HTTP transport mounts at. Pass an
    /// empty string to mount at site root (the standalone CLI does
    /// this when adapter+server are exposed in the same process).
    /// </param>
    public static IEndpointRouteBuilder MapBowireMcp(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/bowire/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var canonical = BowireMcpEndpointRegistry.Normalise(prefix);

        var registry = endpoints.ServiceProvider.GetRequiredService<BowireMcpEndpointRegistry>();
        registry.Register(canonical, BowireMcpEndpointMode.Server);

        // The SDK pattern is empty-string-friendly: MapMcp("") mounts at
        // the routing-group root. Mirror MapBowireMcpAdapter's behaviour
        // and pass through what we received (with canonical "/" turned
        // back into "" so MapMcp(...) doesn't double-prefix).
        var mountPattern = canonical == "/" ? string.Empty : canonical;
        endpoints.MapMcp(mountPattern);

        // Idempotent manifest mount: callers don't have to call us in a
        // specific order; whichever Map* fires first wins the manifest
        // registration, and the second one no-ops.
        endpoints.MapBowireMcpManifest();
        return endpoints;
    }

    /// <summary>
    /// Mount the MCP-manifest endpoint at the canonical path
    /// (<see cref="ManifestPath"/>). Idempotent: the first call wins,
    /// subsequent calls (from either <see cref="MapBowireMcp"/> or
    /// <c>MapBowireMcpAdapter</c>) no-op.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoint returns a JSON array of every mounted MCP route
    /// with its mode tag, e.g.
    /// </para>
    /// <code>
    /// [
    ///   { "path": "/bowire/mcp",          "mode": "server"  },
    ///   { "path": "/bowire/mcp/adapter",  "mode": "adapter" }
    /// ]
    /// </code>
    /// <para>
    /// The workbench's MCP-introspection panel polls this path to
    /// render the per-endpoint badges; an embedded host that wires up
    /// only the adapter still gets a manifest with one entry. The
    /// shape is stable so external tooling can rely on it.
    /// </para>
    /// </remarks>
    public static IEndpointRouteBuilder MapBowireMcpManifest(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Idempotency via a marker in the global DI scope. The endpoint
        // graph itself doesn't expose a "is this route registered" hook,
        // so we record the fact ourselves; subsequent calls bail out
        // without trying to add a duplicate.
        var registry = endpoints.ServiceProvider.GetRequiredService<BowireMcpEndpointRegistry>();
        if (!registry.ManifestMounted.TryAdd(0, 0)) return endpoints;

        endpoints.MapGet(ManifestPath, (BowireMcpEndpointRegistry r) =>
        {
            var entries = r.Snapshot()
                .Select(e => new
                {
                    path = e.Path,
                    // Lowercased mode tag keeps the JSON wire stable
                    // regardless of how .NET serialises the enum (the
                    // default policy is named-capitalised).
                    mode = e.Mode == BowireMcpEndpointMode.Server ? "server" : "adapter",
                })
                .ToArray();

            return Results.Json(new { endpoints = entries });
        })
        .ExcludeFromDescription();

        return endpoints;
    }
}
