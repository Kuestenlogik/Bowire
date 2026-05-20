// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// WebSocket binding for AsyncAPI documents. Binding key in the
/// AsyncAPI spec is <c>ws</c>; the Bowire plugin id is
/// <c>websocket</c> — both are wired up here so the registry lookup
/// hits the right plugin.
///
/// Unlike MQTT and Kafka, the WebSocket plugin has no unary
/// invocation surface — <c>InvokeAsync</c> on the plugin returns an
/// instructive error pointing at the channel API. AsyncAPI <c>send</c>
/// operations against a WebSocket channel still need to act unary
/// from the workbench's perspective (one click → one frame sent), so
/// this resolver translates that intent into a single-shot
/// open + send + close against <see cref="IBowireProtocol.OpenChannelAsync"/>.
///
/// Long-lived receive / duplex semantics (AsyncAPI <c>receive</c>
/// operations, or a UI that wants to keep the channel open) need the
/// AsyncAPI loader to surface methods through the channel pipeline
/// instead of <see cref="InvokeAsync"/>; that integration is a
/// later phase, alongside the same work for MQTT subscribe and
/// Kafka consume.
///
/// Binding fields the AsyncAPI WebSocket spec defines on the channel
/// level (<c>method</c>, <c>query</c>, <c>headers</c>,
/// <c>bindingVersion</c>) and the operation level
/// (<c>subprotocol</c> where authors place it) are forwarded as
/// metadata on the channel open call. The WebSocket plugin reads
/// <c>subprotocol</c> via the existing <c>X-Bowire-Subprotocol</c>
/// markers; the rest ride along the metadata bag for future use.
/// </summary>
internal sealed class WebSocketBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    public WebSocketBindingResolver(BowireProtocolRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>
    /// AsyncAPI spec uses <c>ws</c> as the binding key under
    /// <c>bindings.&lt;id&gt;</c> — that's what the document author
    /// writes. The wire-plugin lookup below maps the <c>ws</c> key
    /// to the Bowire plugin id <c>"websocket"</c> at invocation time.
    /// </summary>
    public string BindingId => "ws";

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        throw new NotImplementedException(
            "WebSocketBindingResolver.BuildMethod is reserved for a later " +
            "phase where per-binding method metadata (subprotocol, query, " +
            "headers) needs to surface on the method itself. The current " +
            "phase builds methods directly from the V3/V2 operation block.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var ws = _registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, "websocket", StringComparison.OrdinalIgnoreCase));

        if (ws is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] =
                        "AsyncAPI document declares a WebSocket (ws) binding, " +
                        "but no WebSocket plugin is loaded. CLI: ships pre-bundled " +
                        "with the Kuestenlogik.Bowire.Tool. Embedded: add the " +
                        "Kuestenlogik.Bowire.Protocol.WebSocket NuGet package to your host."
                });
        }

        var mergedMetadata = MergeWebSocketBindingFields(channel.BindingFields, metadata);
        var payload = jsonMessages.FirstOrDefault() ?? string.Empty;

        // Single-shot open + send + close translates AsyncAPI's `send`
        // operation into something the WebSocket plugin can do today.
        // service / method both carry the channel address — the
        // WebSocket plugin's ResolveUri joins server URL + path, so
        // the channel address is the path portion.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        IBowireChannel? wsChannel = null;
        try
        {
            wsChannel = await ws.OpenChannelAsync(
                serverUrl: channel.ServerUrl,
                service: channel.ChannelAddress,
                method: channel.ChannelAddress,
                showInternalServices: false,
                metadata: mergedMetadata,
                ct: ct).ConfigureAwait(false);

            if (wsChannel is null)
            {
                sw.Stop();
                return new InvokeResult(
                    Response: null,
                    DurationMs: sw.ElapsedMilliseconds,
                    Status: "Error",
                    Metadata: new Dictionary<string, string>
                    {
                        ["error"] =
                            $"WebSocket plugin returned no channel for {channel.ServerUrl} + {channel.ChannelAddress}. " +
                            "Check that the URL is reachable and the subprotocol (if declared) is accepted."
                    });
            }

            var sent = await wsChannel.SendAsync(payload, ct).ConfigureAwait(false);
            await wsChannel.CloseAsync(ct).ConfigureAwait(false);
            sw.Stop();

            return new InvokeResult(
                Response: sent ? "{}" : null,
                DurationMs: sw.ElapsedMilliseconds,
                Status: sent ? "OK" : "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["channel.address"] = channel.ChannelAddress,
                    ["channel.bytesSent"] = payload.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["channel.negotiatedSubProtocol"] = wsChannel.NegotiatedSubProtocol ?? string.Empty
                });
        }
        finally
        {
            if (wsChannel is not null)
            {
                await wsChannel.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Translate AsyncAPI's <c>bindings.ws.*</c> fields into the
    /// metadata keys the WebSocket plugin reads. The plugin pulls
    /// the subprotocol via the existing X-Bowire-Subprotocol marker
    /// (already handled inside the plugin's ExtractSubProtocols
    /// helper); the other fields ride along the bag verbatim so a
    /// future plugin version that learns query / header injection
    /// can pick them up without touching this resolver.
    /// </summary>
    private static Dictionary<string, string> MergeWebSocketBindingFields(
        IReadOnlyDictionary<string, string>? bindingFields,
        IReadOnlyDictionary<string, string>? callerMetadata)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bindingFields is not null)
        {
            foreach (var (k, v) in bindingFields)
            {
                // The AsyncAPI ws binding's `subprotocol` field maps
                // directly onto the marker the WebSocket plugin reads
                // (X-Bowire-Subprotocol). Other fields pass through so
                // diagnostics + future plugin versions see them.
                merged[k] = v;
            }
            if (bindingFields.TryGetValue("subprotocol", out var subProtocol) &&
                !string.IsNullOrWhiteSpace(subProtocol))
            {
                merged["X-Bowire-Subprotocol"] = subProtocol;
            }
        }
        if (callerMetadata is not null)
        {
            foreach (var (k, v) in callerMetadata) merged[k] = v;
        }
        return merged;
    }
}
