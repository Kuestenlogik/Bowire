// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using MQTTnet;

namespace Kuestenlogik.Bowire.Protocol.Mqtt;

/// <summary>
/// Shared helpers for MQTT client creation and broker URL parsing.
/// </summary>
internal static class MqttConnectionHelper
{
    /// <summary>
    /// Parse a broker URL like <c>mqtt://host:1883</c>, <c>tcp://host:1883</c>,
    /// or plain <c>host:1883</c> into a (host, port) tuple. Returns null if
    /// the URL doesn't look like an MQTT broker address.
    /// </summary>
    public static (string host, int port)? ParseBrokerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var raw = url.Trim();

        // Strip known MQTT schemes
        foreach (var scheme in new[] { "mqtt://", "mqtts://", "tcp://", "ssl://" })
        {
            if (raw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[scheme.Length..];
                break;
            }
        }

        // Also strip http(s):// for convenience — the user might paste
        // the same URL they use for other protocols.
        foreach (var scheme in new[] { "https://", "http://" })
        {
            if (raw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                raw = raw[scheme.Length..];
                break;
            }
        }

        // Strip trailing path segments (e.g. /mqtt from ws URLs)
        var slashIdx = raw.IndexOf('/');
        if (slashIdx >= 0) raw = raw[..slashIdx];

        // Split host:port
        var colonIdx = raw.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(raw[(colonIdx + 1)..], out var port))
            return (raw[..colonIdx], port);

        // No port → default MQTT port
        if (raw.Length > 0)
            return (raw, 1883);

        return null;
    }

    public static IMqttClient CreateClient()
    {
        return new MqttClientFactory().CreateMqttClient();
    }

    public static async Task ConnectAsync(IMqttClient client, string host, int port, CancellationToken ct)
    {
        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(host, port)
            .WithClientId($"bowire-{Guid.NewGuid():N}"[..24])
            .WithCleanSession(true)
            .WithTimeout(TimeSpan.FromSeconds(10))
            .Build();

        // MQTTnet's WithTimeout only covers the MQTT-protocol handshake, not the
        // underlying TCP connect. When a host silently drops SYN packets to :1883
        // (e.g. https://example.com pasted into the broker field) the kernel's TCP
        // retransmit retry runs for ~4m30s before giving up, which blocks the whole
        // discovery call. Cap the connect at 15s via a linked CTS.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(options, cts.Token);
    }

    public static async Task DisconnectQuietly(IMqttClient client)
    {
        try
        {
            if (client.IsConnected)
                await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build());
        }
        catch
        {
            // Best-effort disconnect — don't let cleanup failures bubble up.
        }
    }
}
