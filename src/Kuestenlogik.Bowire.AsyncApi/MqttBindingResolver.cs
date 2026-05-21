// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.AsyncApi;

/// <summary>
/// MQTT binding for AsyncAPI documents. Looks up the MQTT wire plugin
/// (<c>Kuestenlogik.Bowire.Protocol.Mqtt</c>, id <c>"mqtt"</c>) at invocation
/// time via the shared <see cref="BowireProtocolRegistry"/> and forwards the
/// publish call there. No hard reference on the plugin csproj — the registry
/// only finds it when the host (CLI bundle or embedded app's NuGet set) has
/// the plugin loaded.
///
/// Phase A3 scope: handles <c>send</c> operations as a unary publish through
/// <see cref="IBowireProtocol.InvokeAsync"/>. <c>receive</c> operations
/// (server-stream subscribe) land in Phase A4 alongside the channel-streaming
/// shape — they need <see cref="IBowireProtocol.InvokeStreamAsync"/> instead.
///
/// Same resolver type handles both the AsyncAPI <c>mqtt</c> and
/// <c>mqtt5</c> binding keys (registered twice in
/// <see cref="BowireAsyncApiProtocol.Initialize"/> with different
/// <c>bindingId</c> ctor arguments). MQTT 5.0-specific fields
/// (sessionExpiryInterval, receiveMaximum, subscriptionIdentifiers, …)
/// arrive on the metadata bag via the generic extractor; MQTTnet 5 picks
/// them up if its own option-mapping recognises the keys, otherwise they
/// ride along for diagnostics + future plugin-side handling.
/// </summary>
internal sealed class MqttBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    public MqttBindingResolver(BowireProtocolRegistry registry, string bindingId = "mqtt")
    {
        _registry = registry;
        BindingId = bindingId;
    }

    public string BindingId { get; }

    public BowireMethodInfo BuildMethod(AsyncApiChannelContext channel)
    {
        // Method shape is built by BowireAsyncApiProtocol.MapV3Channels —
        // the resolver only needs the invocation side. Returning a
        // placeholder keeps the interface honest until Phase A4 (when
        // binding-specific method metadata, e.g. qos, lands).
        throw new NotImplementedException(
            "MqttBindingResolver.BuildMethod is reserved for Phase A4 when " +
            "per-binding method metadata (qos, retain, content-type) needs " +
            "to surface on the method itself. Phase A3 builds methods " +
            "directly from V3OperationDefinition.");
    }

    public async Task<InvokeResult> InvokeAsync(
        AsyncApiChannelContext channel, List<string> jsonMessages,
        Dictionary<string, string>? metadata, CancellationToken ct)
    {
        var mqtt = _registry.Protocols.FirstOrDefault(p =>
            string.Equals(p.Id, "mqtt", StringComparison.OrdinalIgnoreCase));

        if (mqtt is null)
        {
            return new InvokeResult(
                Response: null,
                DurationMs: 0,
                Status: "Error",
                Metadata: new Dictionary<string, string>
                {
                    ["error"] =
                        "AsyncAPI document declares an MQTT binding, but no MQTT " +
                        "plugin is loaded. CLI: ships pre-bundled with the " +
                        "Kuestenlogik.Bowire.Tool. Embedded: add the " +
                        "Kuestenlogik.Bowire.Protocol.Mqtt NuGet package to your host."
                });
        }

        // Merge the AsyncAPI `bindings.mqtt.qos` / `.retain` fields the
        // extractor pulled at discovery into the metadata the MQTT
        // plugin reads. Caller-supplied metadata wins (lets a UI
        // override the doc's qos for a one-off send); doc bindings
        // fill in what the caller didn't specify.
        var mergedMetadata = MergeMqttBindingFields(channel.BindingFields, metadata);

        // The MQTT plugin's invoke contract:
        //   serverUrl = broker URL (mqtt://host:port)
        //   service   = topic prefix (used only for sidebar grouping)
        //   method    = full topic path  ←  channel.address from AsyncAPI
        //   metadata  = qos / retain — from doc bindings or caller
        return await mqtt.InvokeAsync(
            serverUrl: channel.ServerUrl,
            service: channel.ChannelAddress,
            method: channel.ChannelAddress,
            jsonMessages: jsonMessages,
            showInternalServices: false,
            metadata: mergedMetadata,
            ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Translate AsyncAPI's <c>bindings.mqtt.*</c> field map into the
    /// metadata keys the Bowire MQTT plugin reads (<c>qos</c>,
    /// <c>retain</c>). AsyncAPI uses integer 0/1/2 for qos; the MQTT
    /// plugin parses it via <c>Enum.TryParse&lt;MqttQualityOfServiceLevel&gt;</c>
    /// which accepts both the numeric value and the textual name
    /// (<c>AtMostOnce</c> / <c>AtLeastOnce</c> / <c>ExactlyOnce</c>).
    /// Translation: pick the textual name so the plugin's enum parser
    /// can't misread a stringified integer as something else later.
    /// Caller-supplied metadata wins; doc fields are the fallback.
    /// </summary>
    private static Dictionary<string, string> MergeMqttBindingFields(
        IReadOnlyDictionary<string, string>? bindingFields,
        IReadOnlyDictionary<string, string>? callerMetadata)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (bindingFields is not null)
        {
            if (bindingFields.TryGetValue("qos", out var qos))
            {
                merged["qos"] = qos switch
                {
                    "0" => "AtMostOnce",
                    "1" => "AtLeastOnce",
                    "2" => "ExactlyOnce",
                    _ => qos
                };
            }
            if (bindingFields.TryGetValue("retain", out var retain))
            {
                merged["retain"] = retain;
            }
        }
        if (callerMetadata is not null)
        {
            foreach (var (k, v) in callerMetadata) merged[k] = v;
        }
        return merged;
    }
}
