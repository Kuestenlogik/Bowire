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
    /// Dispatch a discovered AsyncAPI <c>send</c> operation to its wire
    /// plugin. Implementation looks up the wire plugin via
    /// <c>BowireProtocolRegistry</c>, maps the AsyncAPI channel + binding
    /// fields onto the wire plugin's invocation contract (e.g. MQTT
    /// publish topic + qos), and forwards the JSON payloads.
    /// </summary>
    Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct);

    /// <summary>
    /// Dispatch a discovered AsyncAPI <c>receive</c> operation — a
    /// subscription — to its wire plugin and stream what arrives. #357:
    /// the default does not throw. A binding whose wire plugin has no
    /// subscribe shape yet yields one <c>{"error": …}</c> frame that says
    /// so, so the operator sees "not supported for this binding" in the
    /// stream pane rather than a stack trace.
    /// </summary>
    IAsyncEnumerable<string> InvokeStreamAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
        => AsyncApiStreamSupport.Unsupported(BindingId);
}

/// <summary>Shared pieces of the resolvers' stream paths.</summary>
internal static class AsyncApiStreamSupport
{
    /// <summary>One error frame in the shape the stream pane renders.</summary>
    public static string ErrorFrame(string message) =>
        System.Text.Json.JsonSerializer.Serialize(new { error = message });

    /// <summary>The single-frame stream for a binding that cannot subscribe yet.</summary>
    public static async IAsyncEnumerable<string> Unsupported(string bindingId)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return ErrorFrame(
            $"Receiving over the AsyncAPI {bindingId} binding is not supported yet: the wire plugin " +
            "has no subscribe shape for it. Send operations on this binding work; see the " +
            "supported-bindings matrix in the AsyncAPI protocol docs.");
    }

    /// <summary>The single-frame stream for a binding whose wire plugin is not loaded.</summary>
    public static async IAsyncEnumerable<string> PluginMissing(string message)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield return ErrorFrame(message);
    }
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
