// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Sse;

/// <summary>
/// Describes an SSE endpoint for Bowire discovery.
/// </summary>
public sealed class SseEndpointInfo
{
    /// <summary>The URL path of the SSE endpoint (e.g., "/events/ticker").</summary>
    public string Path { get; }

    /// <summary>Display name for the endpoint.</summary>
    public string Name { get; }

    /// <summary>Description of what events this endpoint produces.</summary>
    public string? Description { get; }

    /// <summary>Comma-separated list of expected event types (e.g., "price,volume").</summary>
    public string? EventTypes { get; }

    public SseEndpointInfo(string path, string name, string? description = null, string? eventTypes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Path = path;
        Name = name;
        Description = description;
        EventTypes = eventTypes;
    }
}
