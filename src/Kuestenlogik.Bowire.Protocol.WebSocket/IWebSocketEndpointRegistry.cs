// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.WebSocket;

/// <summary>
/// Per-container catalogue of <see cref="WebSocketEndpointInfo"/>
/// instances Bowire's WebSocket plugin should surface in
/// <c>DiscoverAsync</c>, on top of anything the plugin finds via
/// <see cref="WebSocketEndpointAttribute"/> on routed endpoints.
///
/// <para>
/// Registered as a singleton through
/// <see cref="BowireWebSocketServiceCollectionExtensions.AddBowireWebSocketEndpoints"/>.
/// Replaces the pre-v1.7 process-wide static collection on
/// <c>BowireWebSocketProtocol</c>: a per-container instance keeps
/// each ASP.NET host's endpoint list independent (tests, side-by-side
/// in-process hosts) and removes the cross-test races the static
/// collection produced.
/// </para>
/// </summary>
public interface IWebSocketEndpointRegistry
{
    /// <summary>
    /// Append an endpoint to the registry. Thread-safe. Throws
    /// <see cref="ArgumentNullException"/> if <paramref name="endpoint"/>
    /// is null.
    /// </summary>
    void Add(WebSocketEndpointInfo endpoint);

    /// <summary>
    /// Stable snapshot of the registered endpoints at the moment of
    /// the call. The returned list is detached from the registry's
    /// internal storage — concurrent <see cref="Add"/> calls after the
    /// snapshot don't mutate it.
    /// </summary>
    IReadOnlyList<WebSocketEndpointInfo> Snapshot();
}
