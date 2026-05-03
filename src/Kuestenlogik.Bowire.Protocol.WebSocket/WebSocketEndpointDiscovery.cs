// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// Discovers WebSocket endpoints in two modes:
/// <list type="bullet">
/// <item>Statically registered via <see cref="BowireWebSocketProtocol.RegisterEndpoint"/></item>
/// <item>Embedded discovery from <see cref="EndpointDataSource"/> entries that carry a <see cref="WebSocketEndpointAttribute"/> in their metadata</item>
/// </list>
/// Returns a single Bowire service named "WebSocket" that aggregates all
/// found endpoints as "methods", each with a single <c>data</c> input field.
/// </summary>
internal static class WebSocketEndpointDiscovery
{
    public static List<BowireServiceInfo> Discover(
        IReadOnlyList<WebSocketEndpointInfo> registered,
        IServiceProvider? serviceProvider)
    {
        var endpoints = new List<WebSocketEndpointInfo>(registered);

        if (serviceProvider is not null)
        {
            try
            {
                var dataSources = serviceProvider.GetServices<EndpointDataSource>();
                foreach (var ds in dataSources)
                {
                    foreach (var ep in ds.Endpoints)
                    {
                        var attr = ep.Metadata.GetMetadata<WebSocketEndpointAttribute>();
                        if (attr is null) continue;

                        if (ep is RouteEndpoint route)
                        {
                            var path = "/" + route.RoutePattern.RawText?.TrimStart('/');
                            if (endpoints.Any(e => e.Path == path)) continue;

                            // Group name comes from three sources, in priority order:
                            //   1. [WebSocketGroup] as endpoint metadata (inline WithMetadata)
                            //   2. [WebSocketGroup] on the declaring type of the delegate
                            //   3. null → falls back to the generic "WebSocket" service later
                            var group = ep.Metadata.GetMetadata<WebSocketGroupAttribute>()?.Name;

                            endpoints.Add(new WebSocketEndpointInfo(
                                path,
                                attr.DisplayName ?? route.DisplayName,
                                attr.Description,
                                group));
                        }
                    }
                }
            }
            catch
            {
                // Embedded discovery is best-effort — fall back to statically registered ones
            }
        }

        if (endpoints.Count == 0)
            return [];

        // ---- Build methods once, then partition by group name ----
        var methodsByGroup = new Dictionary<string, List<BowireMethodInfo>>(StringComparer.Ordinal);
        foreach (var ep in endpoints)
        {
            var input = new BowireMessageInfo("WebSocketFrame", "WebSocketFrame",
            [
                new BowireFieldInfo(
                    Name: "data",
                    Number: 1,
                    Type: "string",
                    Label: "optional",
                    IsMap: false,
                    IsRepeated: false,
                    MessageType: null,
                    EnumValues: null)
                {
                    Source = "body",
                    Description = "Raw text frame to send. Send binary frames via the channel UI."
                }
            ]);

            var output = new BowireMessageInfo("WebSocketFrame", "WebSocketFrame", []);

            // Name = URL path — stable, unique identifier routed through
            // /api/channel/open and captured into recordings. Summary holds
            // the human-readable label from [WebSocketEndpoint("Chat")] so
            // the sidebar can show a friendly name without losing the path.
            // Earlier revisions swapped these two and the ResolvePathFor()
            // fallback in BowireWebSocketProtocol papered over it.
            var method = new BowireMethodInfo(
                Name: ep.Path,
                FullName: "WebSocket " + ep.Path,
                ClientStreaming: true,
                ServerStreaming: true,
                InputType: input,
                OutputType: output,
                MethodType: "Duplex")
            {
                HttpPath = ep.Path,
                // WebSocket upgrades always start as HTTP GET. Setting it
                // explicitly means the mock-server matcher (which keys REST
                // + WebSocket steps uniformly on verb+path) finds a recorded
                // WS step when a client opens /ws/echo.
                HttpMethod = "GET",
                Summary = ep.DisplayName ?? ep.Path,
                Description = ep.Description
            };

            var groupKey = ResolveGroupName(ep, endpoints);
            if (!methodsByGroup.TryGetValue(groupKey, out var list))
            {
                list = [];
                methodsByGroup[groupKey] = list;
            }
            list.Add(method);
        }

        var services = new List<BowireServiceInfo>(methodsByGroup.Count);
        foreach (var (groupName, methods) in methodsByGroup)
        {
            services.Add(new BowireServiceInfo(groupName, "websocket", methods)
            {
                Source = "websocket",
                Description = "WebSocket endpoints — interactive raw frame channels."
            });
        }
        return services;
    }

    /// <summary>
    /// Picks the sidebar service name for an endpoint in this priority:
    /// 1. explicit <see cref="WebSocketGroupAttribute"/> (via Endpoint.Metadata or registration);
    /// 2. the DisplayName, when exactly one endpoint is registered (preserves the single-endpoint UX);
    /// 3. the generic "WebSocket" umbrella.
    /// </summary>
    private static string ResolveGroupName(WebSocketEndpointInfo ep, List<WebSocketEndpointInfo> all)
    {
        if (!string.IsNullOrEmpty(ep.Group)) return ep.Group!;
        if (all.Count == 1 && !string.IsNullOrEmpty(ep.DisplayName)) return ep.DisplayName!;
        return "WebSocket";
    }
}
