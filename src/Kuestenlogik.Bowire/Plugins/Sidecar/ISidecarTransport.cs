// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// The JSON-RPC channel between the host and a sidecar plugin,
/// abstracted over the wire so the same <see cref="SidecarBowireProtocol"/>
/// drives either transport:
/// <list type="bullet">
///   <item><see cref="SidecarJsonRpcTransport"/> — a local child process,
///   NDJSON over its stdin / stdout (zero-config, host-owned lifecycle).</item>
///   <item><see cref="SidecarHttpTransport"/> — a (possibly remote) HTTP
///   service: requests POST as JSON-RPC, notifications arrive over SSE
///   (the MCP-style streamable-HTTP shape).</item>
/// </list>
/// Both route notifications through per-id subscriptions so concurrent
/// streams + duplex channels never steal each other's frames.
/// </summary>
internal interface ISidecarTransport : IAsyncDisposable
{
    /// <summary>True once the underlying transport is gone (process exited / SSE dropped).</summary>
    bool HasExited { get; }

    /// <summary>
    /// Send a JSON-RPC request, await the matching response, return its
    /// <c>result</c>. Throws <see cref="SidecarJsonRpcException"/> on a
    /// JSON-RPC <c>error</c>.
    /// </summary>
    Task<JsonElement> RequestAsync(string method, object? @params, CancellationToken ct);

    /// <summary>
    /// Register interest in notifications tagged with <paramref name="id"/>
    /// (a host-minted streamId / channelId). Subscribe before sending the
    /// request that starts the stream / channel so no early frame is lost.
    /// </summary>
    ChannelReader<JsonObject> Subscribe(string id);

    /// <summary>Drop the subscription for <paramref name="id"/>.</summary>
    void Unsubscribe(string id);
}
