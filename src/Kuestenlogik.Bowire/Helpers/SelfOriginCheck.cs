// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Helpers;

/// <summary>
/// Helper for distinguishing &quot;serverUrl is this very workbench host&quot; from
/// &quot;serverUrl points at an external server&quot;. Used by protocol plugins that
/// scan the local <see cref="Microsoft.AspNetCore.Routing.EndpointDataSource"/>
/// for auto-discoverable endpoints (SSE, WebSocket, SignalR) — without this
/// check, the workbench host&apos;s OWN endpoints (e.g. <c>/mcp</c> when run with
/// <c>--enable-mcp-adapter</c>) leak into every external <c>serverUrl</c> the
/// operator adds and read as accidental services tagged to the wrong source.
/// </summary>
public static class SelfOriginCheck
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="serverUrl"/>&apos;s host:port
    /// matches one of the addresses ASP.NET Core is listening on. Falls back
    /// to <c>false</c> when the URL cannot be parsed, when no
    /// <see cref="IServerAddressesFeature"/> is available, or when the
    /// listening addresses set is empty (test hosts / unusual hosting models).
    /// </summary>
    /// <remarks>
    /// Comparison is host (case-insensitive) + port (explicit, after the URL
    /// scheme&apos;s default is filled in). Path is ignored — a source URL of
    /// <c>http://localhost:5180/api/foo</c> and a server listening at
    /// <c>http://localhost:5180/</c> are considered the same origin.
    /// </remarks>
    public static bool IsSelfOrigin(string? serverUrl, IServiceProvider? serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(serverUrl)) return false;
        if (serviceProvider is null) return false;

        Uri? candidate;
        try
        {
            candidate = new Uri(serverUrl, UriKind.Absolute);
        }
        catch
        {
            return false;
        }

        var server = serviceProvider.GetService<IServer>();
        var feature = server?.Features.Get<IServerAddressesFeature>();
        var addresses = feature?.Addresses;
        if (addresses is null || addresses.Count == 0) return false;

        var candidateHost = candidate.Host;
        var candidatePort = candidate.Port;

        foreach (var addr in addresses)
        {
            // Server.Addresses entries can be wildcard ("http://+:5180"
            // / "http://*:5180") or "http://[::]:5180". Normalise via
            // try-parse + fall back to the raw substring match for
            // wildcard cases.
            if (Uri.TryCreate(addr, UriKind.Absolute, out var listening))
            {
                if (PortsEqual(candidatePort, listening.Port)
                    && HostsEqual(candidateHost, listening.Host))
                {
                    return true;
                }
                continue;
            }
            // Wildcard form — pull the port suffix and treat host as any.
            var lastColon = addr.LastIndexOf(':');
            if (lastColon > 0
                && int.TryParse(addr.Substring(lastColon + 1).TrimEnd('/'), out var wildcardPort)
                && candidatePort == wildcardPort)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HostsEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        // Treat localhost / 127.0.0.1 / [::1] as aliases of each other so the
        // operator can add "http://localhost:5180" while the server is listening
        // on "http://127.0.0.1:5180" or vice versa.
        var loopback = new[] { "localhost", "127.0.0.1", "::1", "[::1]" };
        var aIsLoop = loopback.Any(l => string.Equals(a, l, StringComparison.OrdinalIgnoreCase));
        var bIsLoop = loopback.Any(l => string.Equals(b, l, StringComparison.OrdinalIgnoreCase));
        // Wildcard host ("+" or "*") matches anything — Kestrel binds to every
        // interface; an inbound URL with any host hits the same process.
        var aIsAny = a == "+" || a == "*" || a == "0.0.0.0";
        var bIsAny = b == "+" || b == "*" || b == "0.0.0.0";
        if (aIsAny || bIsAny) return true;
        return aIsLoop && bIsLoop;
    }

    private static bool PortsEqual(int a, int b) => a == b;
}
