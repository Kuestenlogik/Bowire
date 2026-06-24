// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// Tracks the MCP endpoints (full-server + adapter) mounted on an
/// ASP.NET host so the manifest endpoint can enumerate them and so
/// overlapping route prefixes are caught at startup rather than
/// silently shadowing each other.
/// </summary>
/// <remarks>
/// <para>
/// Lives as a DI singleton, populated by the two map-time extensions:
/// </para>
/// <list type="bullet">
///   <item><see cref="BowireMcpEndpointRouteBuilderExtensions.MapBowireMcp"/>
///         registers the full Bowire MCP server (mode
///         <see cref="BowireMcpEndpointMode.Server"/>).</item>
///   <item><c>McpAdapterEndpoints.MapBowireMcpAdapter</c> in
///         <c>Kuestenlogik.Bowire.Protocol.Mcp</c> registers the
///         adapter (mode <see cref="BowireMcpEndpointMode.Adapter"/>).</item>
/// </list>
/// <para>
/// Both endpoints can coexist on the same host because their default
/// prefixes (<c>/bowire/mcp</c> vs <c>/bowire/mcp/adapter</c>) don't
/// overlap. When an operator overrides one prefix to overlap the other
/// (same path, or one prefix-of the other), <see cref="Register"/>
/// throws <see cref="InvalidOperationException"/> at startup with a
/// message that names both offending routes — failing loud instead of
/// having one endpoint silently swallow the other's traffic.
/// </para>
/// <para>
/// The dispatcher (<see cref="BowireMcpDualHandlerDispatcher"/>)
/// consults this registry at request time to route <c>tools/list</c>,
/// <c>tools/call</c>, <c>resources/*</c> and <c>prompts/*</c>
/// requests to the right handler based on
/// <see cref="Microsoft.AspNetCore.Http.HttpContext.Request"/> path
/// matching one of the registered prefixes.
/// </para>
/// </remarks>
public sealed class BowireMcpEndpointRegistry
{
    // Concurrent because ASP.NET app startup can interleave Map* calls
    // across modules, and tests sometimes run Map* on background tasks
    // (WebApplicationFactory does not guarantee single-thread Map order).
    private readonly ConcurrentDictionary<string, BowireMcpEndpointMode> _entries
        = new(StringComparer.Ordinal);

    /// <summary>
    /// Marker used by <c>MapBowireMcpManifest</c> to enforce single
    /// registration of the manifest endpoint. The key is irrelevant
    /// (always <c>0</c>); only the success of the first
    /// <c>TryAdd</c> matters.
    /// </summary>
    internal ConcurrentDictionary<int, int> ManifestMounted { get; } = new();

    /// <summary>
    /// Register a mounted MCP endpoint. Normalises the prefix to a
    /// canonical "/segment[/segment...]" form (no trailing slash;
    /// empty becomes "/"). Throws
    /// <see cref="InvalidOperationException"/> when the new prefix
    /// overlaps with an already-registered one (exact match, or one
    /// is a path-prefix of the other and would shadow traffic).
    /// </summary>
    public void Register(string prefix, BowireMcpEndpointMode mode)
    {
        var canonical = Normalise(prefix);

        foreach (var existing in _entries)
        {
            if (string.Equals(existing.Key, canonical, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Bowire MCP endpoint at '{canonical}' is already registered as '{existing.Value}'. "
                    + "Each endpoint must mount at a distinct path. Pass a different "
                    + "prefix to MapBowireMcp / MapBowireMcpAdapter.");
            }
        }

        _entries[canonical] = mode;
    }

    /// <summary>
    /// Mode of the endpoint mounted at <paramref name="requestPath"/>,
    /// or <c>null</c> when no registered endpoint covers the request.
    /// Used by <see cref="BowireMcpDualHandlerDispatcher"/> to route
    /// list/call requests by URL path.
    /// </summary>
    public BowireMcpEndpointMode? ResolveMode(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath)) return null;

        // Longest-match wins: when both "/a" and "/a/b" are registered
        // (impossible after overlap check, but defensive), the deeper
        // path is the one ASP.NET routes to.
        BowireMcpEndpointMode? best = null;
        int bestLen = -1;
        foreach (var entry in _entries)
        {
            if (IsRequestUnderPrefix(requestPath, entry.Key) && entry.Key.Length > bestLen)
            {
                best = entry.Value;
                bestLen = entry.Key.Length;
            }
        }
        return best;
    }

    /// <summary>
    /// Snapshot of every registered endpoint, suitable for serving as
    /// the manifest endpoint's JSON body. The order is deterministic
    /// (ordinal by path) so clients (and tests) can rely on it.
    /// </summary>
    public IReadOnlyList<BowireMcpEndpointDescriptor> Snapshot()
        => _entries
            .Select(kv => new BowireMcpEndpointDescriptor(kv.Key, kv.Value))
            .OrderBy(e => e.Path, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Normalise the input prefix to a canonical
    /// <c>"/segment[/segment...]"</c> form. Empty / null / "/" all
    /// collapse to <c>"/"</c> (root mount). Trailing slashes are
    /// stripped so <c>"/mcp"</c> and <c>"/mcp/"</c> compare equal.
    /// </summary>
    internal static string Normalise(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return "/";
        var trimmed = prefix.Trim();
        // Allow callers to omit the leading slash (matches MapBowire's
        // convention — see BowireEndpointRouteBuilderExtensions).
        if (!trimmed.StartsWith('/')) trimmed = "/" + trimmed;
        if (trimmed.Length > 1 && trimmed.EndsWith('/')) trimmed = trimmed[..^1];
        return trimmed;
    }

    private static bool IsRequestUnderPrefix(string requestPath, string prefix)
    {
        if (prefix == "/") return true; // root-mounted endpoint covers everything
        if (!requestPath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        // Same boundary guard as IsPathPrefixOf so "/mcp" doesn't match
        // a request for "/mcpx".
        return requestPath.Length == prefix.Length
            || requestPath[prefix.Length] == '/';
    }
}

/// <summary>
/// Operational mode of a mounted Bowire MCP endpoint. Surfaced in the
/// manifest so introspection clients (the workbench's MCP panel, in
/// particular) can tell the two flavours apart without probing.
/// </summary>
public enum BowireMcpEndpointMode
{
    /// <summary>
    /// Full Bowire MCP server: exposes the workbench's own tooling
    /// (<c>bowire.discover</c>, <c>bowire.invoke</c>,
    /// <c>bowire.mock.*</c>, &amp;c). Mounted via
    /// <see cref="BowireMcpEndpointRouteBuilderExtensions.MapBowireMcp"/>.
    /// </summary>
    Server,

    /// <summary>
    /// Adapter mode: exposes the discovered upstream Bowire API
    /// (gRPC / REST / GraphQL / …) as MCP tools and resources.
    /// Mounted via <c>McpAdapterEndpoints.MapBowireMcpAdapter</c> from
    /// <c>Kuestenlogik.Bowire.Protocol.Mcp</c>.
    /// </summary>
    Adapter,
}

/// <summary>
/// One row of the manifest — what's mounted where, in which mode.
/// Property names are JSON-camelCased by the manifest endpoint's
/// serializer so the workbench JS can read <c>path</c> /
/// <c>mode</c> directly.
/// </summary>
public sealed record BowireMcpEndpointDescriptor(string Path, BowireMcpEndpointMode Mode);
