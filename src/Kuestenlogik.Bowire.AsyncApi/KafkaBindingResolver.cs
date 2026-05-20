// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// Kafka binding for AsyncAPI documents. Looks up the Kafka wire plugin
/// (<c>Kuestenlogik.Bowire.Protocol.Kafka</c>, id <c>"kafka"</c>) at
/// invocation time via the shared <see cref="BowireProtocolRegistry"/>
/// and forwards the publish call there. Symmetric to the MQTT path:
/// no hard csproj reference on the Kafka plugin — the registry only
/// finds it when the host has the plugin loaded (CLI bundle or
/// embedded host's NuGet set).
///
/// Phase B scope: handles <c>send</c> operations as a unary publish
/// through <see cref="IBowireProtocol.InvokeAsync"/> (which the Kafka
/// plugin maps onto a <c>Confluent.Kafka</c> producer). <c>receive</c>
/// operations (server-stream consume) need channel-streaming and
/// land alongside the WebSocket channel work in a later phase.
///
/// Binding fields the AsyncAPI Kafka spec defines on the operation
/// level (<c>topic</c>, <c>key</c>, <c>partition</c>, <c>partitions</c>,
/// <c>replicas</c>, <c>schemaIdLocation</c>,
/// <c>schemaIdPayloadEncoding</c>, <c>schemaLookupStrategy</c>) are
/// pulled out by <see cref="AsyncApiBindingsExtractor"/> and merged
/// onto the metadata bag the Kafka plugin reads. The plugin today
/// honours <c>key</c> and <c>partition</c> directly; the schema-
/// registry fields ride along as metadata so a future Kafka-plugin
/// release can pick them up without touching the resolver.
/// </summary>
internal sealed class KafkaBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    public KafkaBindingResolver(BowireProtocolRegistry registry)
    {
        _registry = registry;
    }

    public string BindingId => "kafka";

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        // Same shape as MQTT: method materialisation happens in
        // BowireAsyncApiProtocol.MapV3Channels / MapV2Channels. The
        // resolver is invocation-side only until per-binding method
        // metadata (key, partition, schema-id) needs to surface on
        // the method itself.
        throw new NotImplementedException(
            "KafkaBindingResolver.BuildMethod is reserved for a future " +
            "phase where per-binding method metadata (key, partition, " +
            "schema-id) needs to surface on the method. Current phase " +
            "builds methods directly from the V3/V2 operation block.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var kafka = _registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, "kafka", StringComparison.OrdinalIgnoreCase));

        if (kafka is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] =
                        "AsyncAPI document declares a Kafka binding, but no Kafka " +
                        "plugin is loaded. CLI: ships pre-bundled with the " +
                        "Kuestenlogik.Bowire.Tool. Embedded: add the " +
                        "Kuestenlogik.Bowire.Protocol.Kafka NuGet package to your host."
                });
        }

        // Doc bindings populate the metadata bag the Kafka plugin
        // reads; caller-supplied metadata wins so a UI override (one-
        // off key / partition for a single produce) doesn't get
        // overwritten by the doc defaults.
        var mergedMetadata = MergeKafkaBindingFields(channel.BindingFields, metadata);

        // The Kafka plugin's invoke contract:
        //   serverUrl = broker URL (kafka://host:9092 — `?schema-registry=` query OK)
        //   service   = topic     ← channel.address from AsyncAPI
        //   method    = "produce" (the plugin's literal verb for unary publish)
        //   metadata  = key / partition / schema-registry knobs
        return await kafka.InvokeAsync(
            serverUrl: channel.ServerUrl,
            service: channel.ChannelAddress,
            method: "produce",
            jsonMessages: jsonMessages,
            showInternalServices: false,
            metadata: mergedMetadata,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translate AsyncAPI's <c>bindings.kafka.*</c> field map into the
    /// metadata keys the Bowire Kafka plugin reads. The plugin
    /// currently consumes <c>key</c> (string, used as the Kafka
    /// message key) and <c>partition</c> (integer-as-string, routes
    /// to a specific partition). The schema-registry-related fields
    /// (<c>schemaIdLocation</c>, <c>schemaIdPayloadEncoding</c>,
    /// <c>schemaLookupStrategy</c>, <c>schemaRegistryUrl</c>) are
    /// forwarded verbatim so a future Kafka-plugin version that
    /// learns Confluent Schema-Registry-driven encode can read them
    /// without touching this resolver.
    /// </summary>
    private static Dictionary<string, string> MergeKafkaBindingFields(
        IReadOnlyDictionary<string, string>? bindingFields,
        IReadOnlyDictionary<string, string>? callerMetadata)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bindingFields is not null)
        {
            foreach (var (k, v) in bindingFields)
            {
                // Pass through every Kafka-binding scalar verbatim.
                // The Kafka plugin only acts on the keys it knows;
                // unrecognised entries stay along the metadata
                // bag for diagnostics + future use.
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
