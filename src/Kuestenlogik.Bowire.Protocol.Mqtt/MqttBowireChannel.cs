// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MQTTnet;
using MQTTnet.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mqtt;

/// <summary>
/// <see cref="IBowireChannel"/> on top of an MQTTnet client. Treats a single
/// topic (or a publish/subscribe pair) as a bidirectional stream:
///   - <see cref="SendAsync"/> publishes the given JSON payload to the
///     configured publish-topic (default: the method's topic).
///   - <see cref="ReadResponsesAsync"/> yields every incoming message on the
///     configured subscribe-topic wrapped as a JSON envelope mirroring
///     <see cref="MqttPayloadHelper"/>'s strategy (JSON → UTF-8 text → hex).
///
/// Metadata overrides:
///   - <c>publish_topic</c> / <c>subscribe_topic</c>: split the two sides onto
///     different topics (request/response pattern).
///   - <c>qos</c>: subscription AND publish QoS level (0/1/2).
///   - <c>retain</c>: retain flag on every publish.
/// </summary>
internal sealed class MqttBowireChannel : IBowireChannel
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    private readonly IMqttClient _client;
    private readonly string _publishTopic;
    private readonly string _subscribeTopic;
    private readonly MqttQualityOfServiceLevel _qos;
    private readonly bool _retain;
    private readonly Channel<string> _responses = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = false,
        SingleWriter = true
    });
    private readonly Stopwatch _stopwatch;
    private readonly CancellationTokenSource _cts;

    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool IsClientStreaming => true;
    public bool IsServerStreaming => true;
    public int SentCount { get; private set; }
    public bool IsClosed { get; private set; }
    public long ElapsedMs => _stopwatch.ElapsedMilliseconds;

    private MqttBowireChannel(
        IMqttClient client,
        string publishTopic,
        string subscribeTopic,
        MqttQualityOfServiceLevel qos,
        bool retain,
        CancellationTokenSource cts)
    {
        _client = client;
        _publishTopic = publishTopic;
        _subscribeTopic = subscribeTopic;
        _qos = qos;
        _retain = retain;
        _cts = cts;
        _stopwatch = Stopwatch.StartNew();

        _client.ApplicationMessageReceivedAsync += OnMessageReceived;
    }

    /// <summary>
    /// Connect to the broker, subscribe to <paramref name="subscribeTopic"/>,
    /// and return a fully wired channel. Throws if the broker is unreachable
    /// — call sites treat that as "channel open failed" and surface the
    /// error back to the UI.
    /// </summary>
    public static async Task<MqttBowireChannel> CreateAsync(
        string host, int port,
        string publishTopic, string subscribeTopic,
        MqttQualityOfServiceLevel qos, bool retain,
        CancellationToken ct)
    {
        var client = MqttConnectionHelper.CreateClient();
        try
        {
            await MqttConnectionHelper.ConnectAsync(client, host, port, ct);

            await client.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic(subscribeTopic)
                    .WithQualityOfServiceLevel(qos)
                    .Build(),
                ct);
        }
        catch
        {
            await MqttConnectionHelper.DisconnectQuietly(client);
            throw;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return new MqttBowireChannel(client, publishTopic, subscribeTopic, qos, retain, cts);
    }

    public async Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default)
    {
        if (IsClosed || !_client.IsConnected) return false;

        // Bowire's typed frames { type: "text", text: "..." } and
        // { type: "binary", base64: "..." } are unpacked; any other string,
        // JSON or not, is the payload itself (#893).
        var (isBinary, text, bytes) = ParseOutgoingFrame(jsonMessage);

        var payload = isBinary && bytes is not null
            ? bytes
            : Encoding.UTF8.GetBytes(text ?? jsonMessage);

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(_publishTopic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(_qos)
            .WithRetainFlag(_retain)
            .Build();

        try
        {
            await _client.PublishAsync(msg, ct);
            SentCount++;
            return true;
        }
        catch (Exception ex)
        {
            await _responses.Writer.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "error",
                message = ex.Message
            }, s_indented), ct);
            return false;
        }
    }

    public Task CloseAsync(CancellationToken ct = default)
    {
        if (IsClosed) return Task.CompletedTask;
        IsClosed = true;
        _responses.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ReadResponsesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var response in _responses.Reader.ReadAllAsync(ct))
            yield return response;
    }

    public async ValueTask DisposeAsync()
    {
        IsClosed = true;
        _responses.Writer.TryComplete();
        await _cts.CancelAsync();

        _client.ApplicationMessageReceivedAsync -= OnMessageReceived;
        await MqttConnectionHelper.DisconnectQuietly(_client);
        _client.Dispose();
        _cts.Dispose();
    }

    private Task OnMessageReceived(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;

        // The broker may fan a subscribe-topic wildcard to many concrete
        // topics. Keep all of them — the user's filter was explicit, so any
        // match is a valid inbound frame. The envelope reports the real topic.
        var payloadBytes = SequenceToArray(e.ApplicationMessage.Payload);
        var envelope = JsonSerializer.Serialize(new
        {
            type = "text",
            topic,
            payload = MqttPayloadHelper.PayloadToDisplayString(payloadBytes),
            qos = (int)e.ApplicationMessage.QualityOfServiceLevel,
            retain = e.ApplicationMessage.Retain,
            bytes = payloadBytes.Length
        }, s_indented);

        _responses.Writer.TryWrite(envelope);
        return Task.CompletedTask;
    }

    private static (bool IsBinary, string? Text, byte[]? Bytes) ParseOutgoingFrame(string jsonMessage)
    {
        if (string.IsNullOrWhiteSpace(jsonMessage))
            return (false, "", null);

        try
        {
            using var doc = JsonDocument.Parse(jsonMessage);
            // Only Bowire's own frames are envelopes, and only exactly:
            // {"type":"text","text":"…"} or {"type":"binary","base64":"…"}
            // and no other key (#893). MQTT devices send objects with a
            // `type` and a `data` / `text` all the time; a numeric type
            // (#852), an extra key or a malformed envelope is the message
            // itself and goes out verbatim. The former `data` shortcut is
            // gone: no Bowire sender produced it for MQTT.
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                CountProperties(root) == 2 &&
                root.TryGetProperty("type", out var typeProp) &&
                typeProp.ValueKind == JsonValueKind.String)
            {
                var t = typeProp.GetString();
                if (t == "binary" && root.TryGetProperty("base64", out var b64) && b64.ValueKind == JsonValueKind.String
                    && TryDecodeBase64(b64.GetString()!, out var bytes))
                {
                    return (true, null, bytes);
                }

                if (t == "text" && root.TryGetProperty("text", out var textProp) && textProp.ValueKind == JsonValueKind.String)
                    return (false, textProp.GetString()!, null);
            }
        }
        catch (JsonException)
        {
            // Not JSON — treat as raw payload
        }

        return (false, jsonMessage, null);
    }

    private static int CountProperties(JsonElement obj)
    {
        var count = 0;
        foreach (var _ in obj.EnumerateObject()) count++;
        return count;
    }

    private static bool TryDecodeBase64(string base64, out byte[] bytes)
    {
        var buffer = new byte[base64.Length * 3 / 4];
        if (Convert.TryFromBase64String(base64, buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }
        bytes = [];
        return false;
    }

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
