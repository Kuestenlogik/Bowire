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
/// Binding-specific fields from the document's <c>bindings.mqtt</c> block
/// (qos, retain) aren't parsed yet because the SDK reader chokes on them
/// without the Bindings packages registered — that wiring is a Phase A4
/// item. Defaults (qos=AtLeastOnce, retain=false) cover the demo flow.
/// </summary>
internal sealed class MqttBindingResolver : IAsyncApiBindingResolver
{
    private readonly BowireProtocolRegistry _registry;

    public MqttBindingResolver(BowireProtocolRegistry registry)
    {
        _registry = registry;
    }

    public string BindingId => "mqtt";

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

        // The MQTT plugin's invoke contract:
        //   serverUrl = broker URL (mqtt://host:port)
        //   service   = topic prefix (used only for sidebar grouping)
        //   method    = full topic path  ←  channel.address from AsyncAPI
        //   metadata  = optional qos/retain (Phase A4 once bindings parse)
        return await mqtt.InvokeAsync(
            serverUrl: channel.ServerUrl,
            service: channel.ChannelAddress,
            method: channel.ChannelAddress,
            jsonMessages: jsonMessages,
            showInternalServices: false,
            metadata: metadata,
            ct: ct).ConfigureAwait(false);
    }
}
