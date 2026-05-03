// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.Sse;

/// <summary>
/// Marks an ASP.NET endpoint as an SSE endpoint for Bowire discovery.
/// Apply to minimal API handlers or controller actions that produce <c>text/event-stream</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SseEndpointAttribute : Attribute
{
    /// <summary>Human-readable description of the SSE endpoint.</summary>
    public string? Description { get; set; }

    /// <summary>Comma-separated list of event types emitted by this endpoint.</summary>
    public string? EventType { get; set; }
}
