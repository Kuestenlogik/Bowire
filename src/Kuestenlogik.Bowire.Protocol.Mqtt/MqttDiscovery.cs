// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using MQTTnet;
using MQTTnet.Protocol;

namespace Kuestenlogik.Bowire.Protocol.Mqtt;

/// <summary>
/// MQTT topic discovery. Connects to the broker, subscribes to <c>#</c>
/// for a short window, and collects active topics. Topics are grouped
/// into services by their first path segment (e.g. <c>sensors/temperature</c>
/// and <c>sensors/humidity</c> both belong to the <c>sensors</c> service).
///
/// Each unique topic becomes two methods:
///   - <c>Publish</c> (Unary) — send a message to the topic
///   - <c>Subscribe</c> (ServerStreaming) — listen for messages
///
/// The method's <c>Name</c> and <c>FullName</c> are the full topic path
/// so the invocation layer can use them directly without mapping.
/// </summary>
internal static class MqttDiscovery
{
    /// <summary>
    /// Scan the broker for active topics. Subscribes to <c>#</c> for up to
    /// <paramref name="scanDuration"/> and collects every topic that
    /// publishes at least one message during that window.
    /// </summary>
    public static async Task<HashSet<string>> ScanTopicsAsync(
        string host, int port, CancellationToken ct, TimeSpan? scanDuration = null)
    {
        var duration = scanDuration ?? TimeSpan.FromSeconds(3);
        var topics = new HashSet<string>(StringComparer.Ordinal);

        using var client = MqttConnectionHelper.CreateClient();

        client.ApplicationMessageReceivedAsync += e =>
        {
            var topic = e.ApplicationMessage.Topic;
            if (!string.IsNullOrEmpty(topic) && !topic.StartsWith("$SYS"))
                topics.Add(topic);
            return Task.CompletedTask;
        };

        await MqttConnectionHelper.ConnectAsync(client, host, port, ct);

        // Subscribe to all topics
        await client.SubscribeAsync(
            new MqttTopicFilterBuilder()
                .WithTopic("#")
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                .Build(),
            ct);

        // Also try $SYS for broker metadata (some brokers support it)
        try
        {
            await client.SubscribeAsync(
                new MqttTopicFilterBuilder()
                    .WithTopic("$SYS/#")
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                    .Build(),
                ct);
        }
        catch
        {
            // $SYS not supported — that's fine
        }

        // Wait for messages to arrive
        try
        {
            await Task.Delay(duration, ct);
        }
        catch (OperationCanceledException)
        {
            // Scan cut short — return what we have
        }

        await MqttConnectionHelper.DisconnectQuietly(client);
        return topics;
    }

    /// <summary>
    /// Group discovered topics into Bowire services. Each first-level
    /// topic segment becomes a service; sub-topics become methods. Single-
    /// segment topics (e.g. <c>heartbeat</c>) go into a "(root)" service.
    /// </summary>
    public static List<BowireServiceInfo> BuildServices(HashSet<string> topics, string originUrl)
    {
        if (topics.Count == 0) return [];

        // Group by first path segment
        var groups = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var topic in topics.OrderBy(t => t, StringComparer.Ordinal))
        {
            var slashIdx = topic.IndexOf('/');
            var prefix = slashIdx > 0 ? topic[..slashIdx] : "(root)";
            if (!groups.TryGetValue(prefix, out var list))
            {
                list = [];
                groups[prefix] = list;
            }
            list.Add(topic);
        }

        var services = new List<BowireServiceInfo>();
        foreach (var (prefix, topicList) in groups)
        {
            var methods = new List<BowireMethodInfo>();
            foreach (var topic in topicList)
            {
                // Subscribe method — server-streaming
                methods.Add(new BowireMethodInfo(
                    Name: topic,
                    FullName: $"mqtt/{topic}/subscribe",
                    ClientStreaming: false,
                    ServerStreaming: true,
                    InputType: BuildSubscribeInput(topic),
                    OutputType: BuildMessageOutput(),
                    MethodType: "ServerStreaming")
                {
                    Summary = $"Subscribe to {topic}",
                    Description = $"Listens for messages on MQTT topic '{topic}'. Each received message is streamed back as a JSON envelope with topic, payload, QoS, retain flag and byte count."
                });

                // Publish method — unary
                methods.Add(new BowireMethodInfo(
                    Name: topic,
                    FullName: $"mqtt/{topic}/publish",
                    ClientStreaming: false,
                    ServerStreaming: false,
                    InputType: BuildPublishInput(topic),
                    OutputType: BuildPublishOutput(),
                    MethodType: "Unary")
                {
                    Summary = $"Publish to {topic}",
                    Description = $"Publishes a message to MQTT topic '{topic}'. Set QoS and retain via metadata headers."
                });
            }

            var svc = new BowireServiceInfo(prefix, "mqtt", methods)
            {
                Source = "mqtt",
                OriginUrl = originUrl,
                Description = $"MQTT topics under '{prefix}/' ({topicList.Count} topic{(topicList.Count != 1 ? "s" : "")})"
            };
            services.Add(svc);
        }

        return services;
    }

    private static BowireMessageInfo BuildPublishInput(string topic) => new(
        "MqttPublishRequest",
        $"mqtt.{topic}.PublishRequest",
        [
            new BowireFieldInfo("payload", 1, "string", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Message payload (JSON string or plain text)",
                Required = true
            },
            new BowireFieldInfo("qos", 2, "int32", "LABEL_OPTIONAL", false, false, null,
            [
                new BowireEnumValue("AtMostOnce", 0),
                new BowireEnumValue("AtLeastOnce", 1),
                new BowireEnumValue("ExactlyOnce", 2)
            ])
            {
                Description = "Quality of Service level (0, 1, or 2)"
            },
            new BowireFieldInfo("retain", 3, "bool", "LABEL_OPTIONAL", false, false, null, null)
            {
                Description = "Whether the broker should retain this message"
            }
        ]);

    private static BowireMessageInfo BuildSubscribeInput(string topic) => new(
        "MqttSubscribeRequest",
        $"mqtt.{topic}.SubscribeRequest",
        [
            new BowireFieldInfo("qos", 1, "int32", "LABEL_OPTIONAL", false, false, null,
            [
                new BowireEnumValue("AtMostOnce", 0),
                new BowireEnumValue("AtLeastOnce", 1),
                new BowireEnumValue("ExactlyOnce", 2)
            ])
            {
                Description = "Subscription QoS level (0, 1, or 2)"
            }
        ]);

    private static BowireMessageInfo BuildPublishOutput() => new(
        "MqttPublishResponse",
        "mqtt.PublishResponse",
        [
            new BowireFieldInfo("topic", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("payload", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("qos", 3, "int32", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("retain", 4, "bool", "LABEL_OPTIONAL", false, false, null, null)
        ]);

    private static BowireMessageInfo BuildMessageOutput() => new(
        "MqttMessage",
        "mqtt.Message",
        [
            new BowireFieldInfo("topic", 1, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("payload", 2, "string", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("qos", 3, "int32", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("retain", 4, "bool", "LABEL_OPTIONAL", false, false, null, null),
            new BowireFieldInfo("bytes", 5, "int32", "LABEL_OPTIONAL", false, false, null, null)
        ]);
}
