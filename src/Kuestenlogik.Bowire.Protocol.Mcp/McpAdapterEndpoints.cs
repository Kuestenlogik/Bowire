// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Mcp;

/// <summary>
/// Map-time wiring for the Bowire MCP adapter. The DI-time half lives
/// in <see cref="BowireMcpAdapterServiceCollectionExtensions.AddBowireMcpAdapter(Microsoft.Extensions.DependencyInjection.IServiceCollection, string?)"/>;
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
/// builder.Services.AddBowireMcpAdapter(opts =&gt;
/// {
///     opts.UpstreamServerUrl = "http://localhost:5005";
/// });
/// var app = builder.Build();
/// app.MapBowire(opts =&gt; opts.Title = "My API");
/// app.MapBowireMcpAdapter();   // default: /bowire/mcp/adapter
/// </code>
/// <para>
/// Dual-mount coexistence (#287): when paired with
/// <c>Kuestenlogik.Bowire.Mcp.MapBowireMcp</c>, both endpoints share
/// the same SDK streamable-HTTP transport and route by URL through
/// <see cref="BowireMcpDualHandlerDispatcher"/>. Overlapping prefixes
/// throw at startup so a typo doesn't silently shadow one endpoint
/// with the other.
/// </para>
/// </remarks>
public static class McpAdapterEndpoints
{
    /// <summary>
    /// Mount the MCP adapter at <paramref name="prefix"/>. Defaults to
    /// <c>/bowire/mcp/adapter</c> — distinct from the full-server's
    /// <c>/bowire/mcp</c> default so both can coexist on the same
    /// host without conflict. Pass an empty string to mount at the
    /// site root (the standalone CLI does this, since it mounts the
    /// workbench at <c>/</c> and the adapter alone has no sibling to
    /// collide with).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="prefix">
    /// URL prefix the adapter mounts under. Defaults to
    /// <c>/bowire/mcp/adapter</c>; pass <c>""</c> to mount at site
    /// root (legacy standalone behaviour, <c>POST /mcp</c>).
    /// </param>
    public static IEndpointRouteBuilder MapBowireMcpAdapter(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/bowire/mcp/adapter")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var canonical = BowireMcpEndpointRegistry.Normalise(prefix);

        // Legacy mount discipline: when the caller passes "", the SDK
        // mounts at "/mcp". The registry should track that so the
        // manifest reflects what the operator actually sees, and so
        // overlap detection works for hosts that mix the legacy root
        // mount with the new default. The new default is
        // "/bowire/mcp/adapter" itself — already an absolute path.
        // We register the prefix the *caller* passed (canonical form)
        // because that's where the SDK actually maps the transport.
        var registry = endpoints.ServiceProvider.GetRequiredService<BowireMcpEndpointRegistry>();
        registry.Register(canonical, BowireMcpEndpointMode.Adapter);

        // The SDK mounts the streamable-HTTP transport at the given
        // path: POST <pattern> handles incoming JSON-RPC, GET <pattern>
        // serves the server-stream half (notifications, sampling).
        // Match MapMcp's empty-string semantics: when canonical is "/"
        // we pass "" so the SDK doesn't double-slash.
        var mountPattern = canonical == "/" ? string.Empty : canonical;
        endpoints.MapMcp(mountPattern);

        // Read-only info endpoint the workbench's Settings → General
        // tab consults to render the MCP-adapter status row. Returns
        // 200 + { enabled: true, path } whenever this method has been
        // called; the absence of the route (a plain 404) is the
        // "disabled" signal the frontend reads back.
        //
        // Backwards-compat path discipline: the previous shape mounted
        // the info endpoint at "<workbench-prefix>/api/mcp-adapter",
        // i.e. one segment up from the adapter route. We preserve that
        // by stripping the final segment of the adapter prefix:
        //   "/mcp"                  -> "/api/mcp-adapter"
        //   "/bowire/mcp/adapter"   -> "/bowire/mcp/api/mcp-adapter"
        //   "/bowire/mcp"           -> "/bowire/api/mcp-adapter"
        // Hosts that want the manifest-driven shape (#287) should poll
        // /bowire/mcp/manifest instead — both endpoints are exposed so
        // the frontend can migrate at its own pace.
        var adapterPath = canonical == "/" ? "/mcp" : canonical;
        var infoBase = ParentOf(canonical);
        endpoints.MapGet($"{infoBase}/api/mcp-adapter", () =>
            Results.Ok(new { enabled = true, path = adapterPath }))
            .ExcludeFromDescription();

        // Idempotently mount the manifest endpoint so a host that
        // wires only the adapter (no AddBowireMcp / MapBowireMcp) still
        // exposes the introspection surface the workbench's MCP panel
        // expects to find.
        endpoints.MapBowireMcpManifest();

        return endpoints;
    }

    /// <summary>
    /// Drop the final segment of a path: <c>"/bowire/mcp/adapter"</c>
    /// → <c>"/bowire/mcp"</c>. Used to position the info endpoint one
    /// segment up from the adapter route — preserving the wire shape
    /// the workbench JS expects (info-endpoint sibling of the bowire
    /// HTML mount, not sibling of the MCP transport itself).
    /// </summary>
    private static string ParentOf(string canonical)
    {
        if (canonical == "/" || canonical.Length == 0) return string.Empty;
        var lastSlash = canonical.LastIndexOf('/');
        if (lastSlash <= 0) return string.Empty;
        return canonical[..lastSlash];
    }
}
