// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional capability surface for protocol plugins that can subscribe to a
/// remote Server-Sent Events stream and yield each event as it arrives.
///
/// The SSE plugin implements this so that other plugins (MCP for server-side
/// notifications, GraphQL for the graphql-sse subscription transport) can
/// reuse the SSE event-stream parser without taking a hard dependency on
/// <c>Kuestenlogik.Bowire.Protocol.Sse</c> at compile time. If the SSE plugin isn't
/// loaded, <see cref="BowireProtocolRegistry.FindSseSubscriber"/> returns
/// null and the consuming plugin should fall back to a clear "install the
/// SSE plugin" error.
/// </summary>
public interface IInlineSseSubscriber
{
    /// <summary>
    /// Open an SSE connection to <paramref name="url"/> with the given
    /// request headers and yield each parsed event as a JSON envelope of the
    /// form <c>{ id, event, data, retry }</c>. The stream stays open until
    /// the server closes it or <paramref name="ct"/> is cancelled.
    /// </summary>
    IAsyncEnumerable<string> SubscribeAsync(
        string url,
        Dictionary<string, string>? headers,
        CancellationToken ct = default);
}
