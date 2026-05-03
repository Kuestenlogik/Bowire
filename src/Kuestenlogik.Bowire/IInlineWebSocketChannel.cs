// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional capability surface for protocol plugins that can open a raw
/// WebSocket channel — used by plugins that need to ride on top of a
/// WebSocket transport (e.g. the GraphQL plugin's graphql-transport-ws
/// subscription support) without taking a compile-time dependency on
/// <c>Kuestenlogik.Bowire.Protocol.WebSocket</c>.
///
/// Implementers should honour the <c>subProtocols</c> parameter by passing
/// it to <c>ClientWebSocket.Options.AddSubProtocol(...)</c> before the
/// handshake — that's how WebSocket sub-protocols like
/// <c>graphql-transport-ws</c> are negotiated. Headers go on the upgrade
/// request via <c>SetRequestHeader</c> so the existing auth-helper
/// pipeline keeps working unchanged.
/// </summary>
public interface IInlineWebSocketChannel
{
    /// <summary>
    /// Connect to <paramref name="url"/> with the given sub-protocols and
    /// request headers, and return an <see cref="IBowireChannel"/> the
    /// caller can drive to send and receive frames. <paramref name="url"/>
    /// must use the <c>ws://</c> or <c>wss://</c> scheme — http/https URLs
    /// should be rewritten by the caller before invoking this.
    /// </summary>
    Task<IBowireChannel> OpenAsync(
        string url,
        IReadOnlyList<string>? subProtocols,
        Dictionary<string, string>? headers,
        CancellationToken ct = default);
}
