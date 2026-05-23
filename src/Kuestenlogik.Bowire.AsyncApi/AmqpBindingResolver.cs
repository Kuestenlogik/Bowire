// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// AMQP binding for AsyncAPI documents. Same shape as the MQTT/Kafka
/// resolvers: looks up the AMQP wire plugin
/// (<c>Kuestenlogik.Bowire.Protocol.Amqp</c>, id <c>"amqp"</c>) at
/// invocation time via the shared <see cref="BowireProtocolRegistry"/>
/// and forwards the publish call there. No hard csproj reference on
/// the wire plugin — the registry only finds it when the host has the
/// plugin loaded (CLI bundle or embedded host's NuGet set).
///
/// One resolver class covers both AsyncAPI AMQP binding keys:
/// <c>bindings.amqp</c> (AMQP 0.9.1) and <c>bindings.amqp1</c>
/// (AMQP 1.0). The wire plugin itself dispatches between the two
/// wires based on the URL scheme (<c>amqp://</c> vs <c>amqp1://</c>),
/// so the resolver mostly hands the message through and merges the
/// binding-field bag onto the metadata the plugin reads.
///
/// Phase C scope: handles <c>send</c> operations as a unary publish.
/// Receive (server-stream consume) is wired through
/// <see cref="IBowireProtocol.InvokeStreamAsync"/> in the plugin but
/// the AsyncAPI loader doesn't expose a streaming entrypoint here yet
/// — same constraint Kafka/MQTT still live with.
///
/// AsyncAPI 0.9.1 binding fields (per spec on the channel/operation
/// level): <c>is</c> (queue/routingKey), <c>exchange.name/type/durable/
/// autoDelete/vhost</c>, <c>queue.name/durable/exclusive/autoDelete/
/// vhost</c>, plus operation-level <c>cc</c>, <c>ack</c>,
/// <c>deliveryMode</c>, <c>expiration</c>, <c>mandatory</c>,
/// <c>priority</c>, <c>timestamp</c>. AsyncAPI 1.0 binding (less
/// fleshed out by the spec) carries <c>messageType</c> and a thin
/// header set. Both go through <see cref="AsyncApiBindingsExtractor"/>
/// the same way Kafka does — every scalar gets forwarded to the
/// metadata bag verbatim; the plugin acts on the keys it knows
/// (<c>routingKey</c>, <c>deliveryMode</c>, <c>expiration</c>,
/// <c>mandatory</c>, <c>messageId</c>, <c>correlationId</c>,
/// <c>contentType</c>, <c>address</c>).
/// </summary>
internal sealed class AmqpBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    /// <summary>Default binding id <c>"amqp"</c> covers AsyncAPI 0.9.1
    /// declarations. Pass <c>"amqp1"</c> for 1.0.</summary>
    public AmqpBindingResolver(BowireProtocolRegistry registry, string bindingId = "amqp")
    {
        _registry = registry;
        BindingId = bindingId;
    }

    public string BindingId { get; }

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        // Method materialisation happens in BowireAsyncApiProtocol's
        // V3/V2 channel mapping; the resolver is invocation-side only
        // until per-binding method metadata (routingKey, exchange) needs
        // to surface on the method itself. Same shape as Kafka/MQTT.
        throw new NotImplementedException(
            "AmqpBindingResolver.BuildMethod is reserved for a future " +
            "phase where per-binding method metadata (routingKey, " +
            "exchange, queue) needs to surface on the method. Current " +
            "phase builds methods directly from the V3/V2 operation block.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var amqp = _registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, "amqp", StringComparison.OrdinalIgnoreCase));

        if (amqp is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] =
                        $"AsyncAPI document declares an {BindingId} binding, but no AMQP " +
                        "plugin is loaded. CLI: ships pre-bundled with the " +
                        "Kuestenlogik.Bowire.Tool (v1.6+). Embedded: add the " +
                        "Kuestenlogik.Bowire.Protocol.Amqp NuGet package to your host."
                });
        }

        // Doc bindings populate the metadata bag the AMQP plugin reads;
        // caller-supplied metadata wins so a UI override (one-off
        // routing key / address for a single publish) isn't lost to the
        // doc defaults.
        var mergedMetadata = MergeAmqpBindingFields(channel.BindingFields, metadata);

        // The AMQP plugin's invoke contract:
        //   serverUrl = broker URL (amqp:// or amqp1:// — scheme picks wire)
        //   service   = exchange (0.9.1) or address (1.0); from channel.address
        //   method    = "send" (the plugin's literal verb for unary publish)
        //   metadata  = routingKey / deliveryMode / expiration / address / …
        return await amqp.InvokeAsync(
            serverUrl: channel.ServerUrl,
            service: channel.ChannelAddress,
            method: "send",
            jsonMessages: jsonMessages,
            showInternalServices: false,
            metadata: mergedMetadata,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translate AsyncAPI's <c>bindings.amqp.*</c> / <c>bindings.amqp1.*</c>
    /// field map into the metadata keys the Bowire AMQP plugin reads.
    /// Every binding scalar is forwarded verbatim — the plugin acts on
    /// the keys it knows; unknown entries ride along for diagnostics +
    /// future use (same policy as the Kafka resolver). One translation
    /// applied here: AsyncAPI 0.9.1 publishes can carry <c>is: routingKey</c>
    /// + <c>cc</c> separately from a top-level routing-key — we don't
    /// flatten those yet, the plugin can interpret <c>cc</c> directly
    /// when it grows the matching feature.
    /// </summary>
    private static Dictionary<string, string> MergeAmqpBindingFields(
        IReadOnlyDictionary<string, string>? bindingFields,
        IReadOnlyDictionary<string, string>? callerMetadata)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bindingFields is not null)
        {
            foreach (var (k, v) in bindingFields)
            {
                merged[k] = v;
            }
        }
        if (callerMetadata is not null)
        {
            foreach (var (k, v) in callerMetadata) merged[k] = v;
        }
        return merged;
    }
}
