// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Sse;

/// <summary>
/// Discovers SSE endpoints from manual registrations and endpoint routing metadata.
/// </summary>
internal static class SseEndpointDiscovery
{
    private const string EventStreamContentType = "text/event-stream";

    /// <summary>
    /// Discovers SSE endpoints from configured registrations, <see cref="SseEndpointAttribute"/>
    /// markers, and <c>Produces("text/event-stream")</c> metadata in the endpoint data sources.
    /// </summary>
    public static List<BowireServiceInfo> Discover(
        IReadOnlyList<SseEndpointInfo> configuredEndpoints,
        IServiceProvider? serviceProvider)
    {
        var endpoints = new List<SseEndpointInfo>(configuredEndpoints);

        // Scan endpoint data sources for auto-discoverable SSE endpoints
        if (serviceProvider is not null)
        {
            var dataSources = serviceProvider.GetService<IEnumerable<EndpointDataSource>>();
            if (dataSources is not null)
            {
                foreach (var source in dataSources)
                {
                    foreach (var endpoint in source.Endpoints)
                    {
                        ScanEndpoint(endpoint, endpoints);
                    }
                }
            }
        }

        if (endpoints.Count == 0) return [];

        // Deduplicate by path (manual registrations take precedence)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<SseEndpointInfo>();
        foreach (var ep in endpoints)
        {
            if (seen.Add(ep.Path))
                deduped.Add(ep);
        }

        // Convert each SSE endpoint to a BowireMethodInfo grouped under a single service
        var methods = deduped.Select(ep => new BowireMethodInfo(
            Name: ep.Name,
            FullName: $"SSE{ep.Path}",
            ClientStreaming: false,
            ServerStreaming: true, // SSE is always server streaming
            InputType: BuildInputType(ep),
            OutputType: BuildOutputType(),
            MethodType: "ServerStreaming"
        )).ToList();

        return
        [
            new BowireServiceInfo(
                Name: "SSE Endpoints",
                Package: "/events",
                Methods: methods)
            { Source = "sse" }
        ];
    }

    private static void ScanEndpoint(Endpoint endpoint, List<SseEndpointInfo> results)
    {
        // Check for [SseEndpoint] attribute
        var sseAttr = endpoint.Metadata.GetMetadata<SseEndpointAttribute>();
        if (sseAttr is not null)
        {
            var path = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "";
            var name = sseAttr.Description ?? path;
            results.Add(new SseEndpointInfo(path, name, sseAttr.Description, sseAttr.EventType));
            return;
        }

        // Check for Produces("text/event-stream") metadata
        var producesMetadata = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
        foreach (var produces in producesMetadata)
        {
            if (produces.ContentTypes.Contains(EventStreamContentType, StringComparer.OrdinalIgnoreCase))
            {
                var path = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "";
                if (string.IsNullOrEmpty(path)) continue;

                var displayName = endpoint.DisplayName ?? path;
                results.Add(new SseEndpointInfo(path, displayName, description: null, eventTypes: null));
                return;
            }
        }
    }

    private static BowireMessageInfo BuildInputType(SseEndpointInfo ep)
    {
        // SSE endpoints accept the URL (with optional query parameters) as input
        return new BowireMessageInfo("SubscribeRequest", "sse.SubscribeRequest",
        [
            new BowireFieldInfo("url", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
        ]);
    }

    private static BowireMessageInfo BuildOutputType()
    {
        // SSE events have a standard structure: id, event, data, retry
        return new BowireMessageInfo("SseEvent", "sse.SseEvent",
        [
            new BowireFieldInfo("id", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("event", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("data", 3, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("retry", 4, "int32", "LABEL_OPTIONAL", false, false, null, null),
        ]);
    }
}
