// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using MQTTnet;
using MQTTnet.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mqtt;

/// <summary>
/// Bowire protocol plugin for MQTT 3.1.1 / 5.0 brokers via MQTTnet.
///
/// Discovery model:
///   - Connects to the broker and subscribes to <c>#</c> (all topics) for
///     a short window, collecting active topic prefixes.
///   - Each top-level topic prefix becomes a <see cref="BowireServiceInfo"/>.
///   - Individual sub-topics become methods: <c>Publish</c> (Unary) and
///     <c>Subscribe</c> (ServerStreaming).
///
/// Payload handling:
///   - Payloads are tried as JSON first, then UTF-8 text, then hex-dumped.
///     This makes the plugin usable for both structured IoT data (JSON)
///     and raw binary payloads.
///
/// Auto-discovered by <see cref="BowireProtocolRegistry"/>.
/// </summary>
public sealed class BowireMqttProtocol : IBowireProtocol
{
    public string Name => "MQTT";
    public string Id => "mqtt";

    // Official mqtt.org logo (simpleicons).
    public string IconSvg => """<svg viewBox="0 0 24 24" fill="#660066" width="16" height="16" aria-hidden="true"><path d="M10.657 23.994h-9.45A1.212 1.212 0 0 1 0 22.788v-9.18h.071c5.784 0 10.504 4.65 10.586 10.386Zm7.606 0h-4.045C14.135 16.246 7.795 9.977 0 9.942V6.038h.071c9.983 0 18.121 8.044 18.192 17.956Zm4.53 0h-.97C21.754 12.071 11.995 2.407 0 2.372v-1.16C0 .55.544.006 1.207.006h7.64C15.733 2.49 21.257 7.789 24 14.508v8.291c0 .663-.544 1.195-1.207 1.195ZM16.713.006h6.092A1.19 1.19 0 0 1 24 1.2v5.914c-.91-1.242-2.046-2.65-3.158-3.762C19.588 2.11 18.122.987 16.714.005Z"/></svg>""";

    public IReadOnlyList<BowirePluginSetting> Settings =>
    [
        new("autoInterpretJson", "Auto-interpret JSON",
            "Parse JSON payloads for structured display instead of raw text",
            "bool", true),
        new("scanDuration", "Topic scan duration",
            "How long to subscribe to # during discovery (seconds)",
            "number", 3)
    ];

    public void Initialize(IServiceProvider? serviceProvider) { }

    public async Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            return [];

        var brokerUri = MqttConnectionHelper.ParseBrokerUrl(serverUrl);
        if (brokerUri is null)
            return [];

        try
        {
            var topics = await MqttDiscovery.ScanTopicsAsync(brokerUri.Value.host, brokerUri.Value.port, ct);
            return MqttDiscovery.BuildServices(topics, serverUrl);
        }
        catch
        {
            // Broker unreachable / connection refused — not an error for
            // discovery, just means there's nothing here.
            return [];
        }
    }

    public async Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        var brokerUri = MqttConnectionHelper.ParseBrokerUrl(serverUrl);
        if (brokerUri is null)
            return new InvokeResult(null, 0, "Error", new()) { };

        // The "method" is the full topic path. The "service" is the
        // topic prefix used for grouping — not needed for invocation.
        var topic = method;
        var payload = jsonMessages.FirstOrDefault() ?? "{}";

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var client = MqttConnectionHelper.CreateClient();
        await MqttConnectionHelper.ConnectAsync(client, brokerUri.Value.host, brokerUri.Value.port, ct);

        try
        {
            var qos = MqttQualityOfServiceLevel.AtLeastOnce;
            if (metadata?.TryGetValue("qos", out var qosStr) == true)
                qos = Enum.TryParse<MqttQualityOfServiceLevel>(qosStr, true, out var q) ? q : qos;

            var retain = metadata?.TryGetValue("retain", out var retainStr) == true
                && string.Equals(retainStr, "true", StringComparison.OrdinalIgnoreCase);

            var msg = new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(payload))
                .WithQualityOfServiceLevel(qos)
                .WithRetainFlag(retain)
                .Build();

            await client.PublishAsync(msg, ct);
            sw.Stop();

            return new InvokeResult(
                JsonSerializer.Serialize(new { topic, payload, qos = (int)qos, retain }),
                sw.ElapsedMilliseconds,
                "OK",
                new Dictionary<string, string>
                {
                    ["topic"] = topic,
                    ["qos"] = ((int)qos).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["retain"] = retain ? "true" : "false"
                });
        }
        finally
        {
            await MqttConnectionHelper.DisconnectQuietly(client);
        }
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var brokerUri = MqttConnectionHelper.ParseBrokerUrl(serverUrl);
        if (brokerUri is null)
            yield break;

        var topic = method;
        using var client = MqttConnectionHelper.CreateClient();
        await MqttConnectionHelper.ConnectAsync(client, brokerUri.Value.host, brokerUri.Value.port, ct);

        var channel = System.Threading.Channels.Channel.CreateUnbounded<string>();

        client.ApplicationMessageReceivedAsync += e =>
        {
            var payloadSeq = e.ApplicationMessage.Payload;
            var payloadBytes = SequenceToArray(payloadSeq);
            var payloadText = MqttPayloadHelper.PayloadToDisplayString(payloadBytes);
            var envelope = JsonSerializer.Serialize(new
            {
                topic = e.ApplicationMessage.Topic,
                qos = (int)e.ApplicationMessage.QualityOfServiceLevel,
                retain = e.ApplicationMessage.Retain,
                payload = payloadText,
                bytes = payloadBytes.Length
            });
            channel.Writer.TryWrite(envelope);
            return Task.CompletedTask;
        };

        var qos = MqttQualityOfServiceLevel.AtLeastOnce;
        if (metadata?.TryGetValue("qos", out var qosStr) == true)
            qos = Enum.TryParse<MqttQualityOfServiceLevel>(qosStr, true, out var q) ? q : qos;

        await client.SubscribeAsync(
            new MqttTopicFilterBuilder().WithTopic(topic).WithQualityOfServiceLevel(qos).Build(),
            ct);

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(ct))
            {
                yield return msg;
            }
        }
        finally
        {
            await MqttConnectionHelper.DisconnectQuietly(client);
        }
    }

    public async Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var brokerUri = MqttConnectionHelper.ParseBrokerUrl(serverUrl);
        if (brokerUri is null) return null;

        // By default we publish to AND subscribe on the method's topic so the
        // channel behaves like a raw duplex stream. For the request/response
        // pattern, metadata can point each side at a different topic.
        var publishTopic = metadata?.TryGetValue("publish_topic", out var pt) == true && !string.IsNullOrWhiteSpace(pt)
            ? pt
            : method;
        var subscribeTopic = metadata?.TryGetValue("subscribe_topic", out var st) == true && !string.IsNullOrWhiteSpace(st)
            ? st
            : method;

        var qos = MqttQualityOfServiceLevel.AtLeastOnce;
        if (metadata?.TryGetValue("qos", out var qosStr) == true)
            qos = Enum.TryParse<MqttQualityOfServiceLevel>(qosStr, true, out var q) ? q : qos;

        var retain = metadata?.TryGetValue("retain", out var retainStr) == true
            && string.Equals(retainStr, "true", StringComparison.OrdinalIgnoreCase);

        try
        {
            return await MqttBowireChannel.CreateAsync(
                brokerUri.Value.host,
                brokerUri.Value.port,
                publishTopic,
                subscribeTopic,
                qos,
                retain,
                ct);
        }
        catch
        {
            // Connect / subscribe failed — return null so the UI can fall
            // back to the non-channel publish/subscribe path.
            return null;
        }
    }

    /// <summary>
    /// Convert a <see cref="ReadOnlySequence{T}"/> to a byte array.
    /// MQTTnet v5 uses sequences instead of ArraySegment, and the
    /// generic ToArray extension conflicts with ImmutableArray's.
    /// </summary>
    private static byte[] SequenceToArray(ReadOnlySequence<byte> seq)
    {
        if (seq.IsEmpty) return [];
        var arr = new byte[seq.Length];
        var pos = 0;
        foreach (var segment in seq)
        {
            segment.Span.CopyTo(arr.AsSpan(pos));
            pos += segment.Length;
        }
        return arr;
    }
}
