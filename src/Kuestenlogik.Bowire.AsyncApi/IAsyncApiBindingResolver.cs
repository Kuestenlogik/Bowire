// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// Per-wire binding resolver. One implementation per AsyncAPI binding spec
/// (mqtt, kafka, ws, amqp, …) — translates the binding-specific fields
/// (topic, qos, retain, partition-key, subprotocol, …) and the AsyncAPI
/// channel/operation pair into the corresponding wire-plugin call.
///
/// Resolvers are lookups from <c>IBowireProtocol.Id</c>, so the AsyncAPI
/// loader can:
///   1. Parse the channel's <c>bindings:</c> block,
///   2. Pick the resolver whose <see cref="BindingId"/> matches the binding key,
///   3. Ask the resolver to materialise an invocation against the
///      corresponding wire plugin (looked up through
///      <c>BowireProtocolRegistry</c>).
///
/// Phase A only ships the MQTT resolver. Phase B adds Kafka + WebSocket.
/// Phase C the remaining bindings whose wire plugins also need to land.
/// </summary>
public interface IAsyncApiBindingResolver
{
    /// <summary>
    /// AsyncAPI binding key — "mqtt", "kafka", "ws", "amqp", "nats", … —
    /// as it appears under <c>operations.&lt;name&gt;.bindings.&lt;key&gt;</c>
    /// in the document. Matches <see cref="IBowireProtocol.Id"/> by
    /// convention so the resolver and the wire plugin pair up by id.
    /// </summary>
    string BindingId { get; }

    /// <summary>
    /// Build a <see cref="BowireMethodInfo"/> from an AsyncAPI operation +
    /// channel + the parsed binding block. The returned method is wired
    /// onto the service that represents the channel; invocations on that
    /// method run through <see cref="InvokeAsync"/> below.
    /// </summary>
    BowireMethodInfo BuildMethod(AsyncApiChannelContext channel);

    /// <summary>
    /// Dispatch a discovered AsyncAPI method to its wire plugin.
    /// Implementation looks up the wire plugin via
    /// <c>BowireProtocolRegistry</c>, maps the AsyncAPI channel + binding
    /// fields onto the wire plugin's invocation contract (e.g. MQTT
    /// publish topic + qos), and forwards the JSON payloads.
    /// </summary>
    Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct);
}

/// <summary>
/// Everything a binding resolver needs to translate one AsyncAPI operation
/// into a wire-plugin call. Filled by the AsyncAPI loader; consumed by
/// <see cref="IAsyncApiBindingResolver"/> implementations.
/// </summary>
/// <param name="ServerUrl">Resolved <c>servers[]</c> entry the channel binds to.</param>
/// <param name="ChannelAddress">Channel address from <c>channels.&lt;name&gt;.address</c>.</param>
/// <param name="OperationAction"><c>"send"</c> or <c>"receive"</c> from the operation block.</param>
/// <param name="BindingFields">Raw key/value map of the matching <c>bindings.&lt;id&gt;</c> block.</param>
public sealed record AsyncApiChannelContext(
    string ServerUrl,
    string ChannelAddress,
    string OperationAction,
    IReadOnlyDictionary<string, string> BindingFields);
