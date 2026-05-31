// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// Default <see cref="IWebSocketEndpointRegistry"/> backed by a
/// lock-guarded <see cref="List{T}"/>. Public so consumers who want
/// to construct one directly (test fixtures, in-process embedding
/// outside the standard DI extension) can do so without going through
/// the service collection.
/// </summary>
public sealed class WebSocketEndpointRegistry : IWebSocketEndpointRegistry
{
    private readonly List<WebSocketEndpointInfo> _endpoints = [];
    private readonly Lock _lock = new();

    /// <inheritdoc />
    public void Add(WebSocketEndpointInfo endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_lock)
        {
            _endpoints.Add(endpoint);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<WebSocketEndpointInfo> Snapshot()
    {
        lock (_lock)
        {
            // ToArray detaches the snapshot from the live list so
            // concurrent Add calls after this point don't mutate the
            // returned view.
            return _endpoints.ToArray();
        }
    }
}
