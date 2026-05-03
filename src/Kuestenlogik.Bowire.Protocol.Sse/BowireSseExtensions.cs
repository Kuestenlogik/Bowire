// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Routing;

namespace Kuestenlogik.Bowire.Protocol.Sse;

/// <summary>
/// Extension methods for registering SSE endpoints with Bowire.
/// </summary>
public static class BowireSseExtensions
{
    /// <summary>
    /// Registers an SSE endpoint for Bowire discovery. Call before <c>MapBowire()</c>.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">The URL path of the SSE endpoint (e.g., "/events/ticker").</param>
    /// <param name="name">Display name for the endpoint.</param>
    /// <param name="description">Optional description of what events this endpoint produces.</param>
    /// <param name="eventTypes">Optional comma-separated list of event types.</param>
    /// <returns>The endpoint route builder for chaining.</returns>
    public static IEndpointRouteBuilder AddBowireSseEndpoint(
        this IEndpointRouteBuilder endpoints,
        string path,
        string name,
        string? description = null,
        string? eventTypes = null)
    {
        BowireSseProtocol.RegisterEndpoint(new SseEndpointInfo(path, name, description, eventTypes));
        return endpoints;
    }
}
