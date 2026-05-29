// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Kuestenlogik.Bowire.Plugins.Sidecar;

/// <summary>
/// Per-id notification fan-out shared by both sidecar transports. One
/// unbounded channel per live stream / channel id; the transport's read
/// loop hands each inbound notification to <see cref="Route"/>, which
/// delivers it to the subscription matching its <c>params.streamId</c> /
/// <c>params.channelId</c>. Frames for an id nobody subscribed to are
/// dropped (late frame after unsubscribe, or a buggy sidecar) rather
/// than buffered forever.
/// </summary>
internal sealed class SidecarSubscriptionHub
{
    private readonly ConcurrentDictionary<string, Channel<JsonObject>> _subscriptions =
        new(StringComparer.Ordinal);

    public ChannelReader<JsonObject> Subscribe(string id)
    {
        var ch = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
        _subscriptions[id] = ch;
        return ch.Reader;
    }

    public void Unsubscribe(string id)
    {
        if (_subscriptions.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    /// <summary>Route a notification envelope to its subscription by stream/channel id.</summary>
    public void Route(JsonObject notification)
    {
        var np = notification["params"] as JsonObject;
        var subId = np?["streamId"]?.GetValue<string>()
            ?? np?["channelId"]?.GetValue<string>();
        if (subId is not null && _subscriptions.TryGetValue(subId, out var sub))
            sub.Writer.TryWrite(notification);
    }

    /// <summary>Complete every live subscription so blocked readers wake and exit.</summary>
    public void CompleteAll()
    {
        foreach (var kv in _subscriptions) kv.Value.Writer.TryComplete();
        _subscriptions.Clear();
    }
}
