// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using MQTTnet;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// End-to-end test for the MQTT reactive subscribe-match-respond
/// path: a client publishes on a topic matching a recorded pattern,
/// and the mock emits a paired response with topic-wildcard bindings
/// substituted into the response body.
/// </summary>
public sealed class MqttReactiveResponderTests : IDisposable
{
    private readonly string _tempDir;

    public MqttReactiveResponderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mqtt-reactive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ReactiveStep_MatchesIncomingPublish_EmitsPairedResponse()
    {
        // Step: when anyone publishes on cmd/+/reboot, emit an ack on
        // cmd/${topic.0}/ack. Substitution captures the first
        // wildcard segment.
        var recording = new
        {
            id = "rec_mqtt_react",
            name = "mqtt reactive",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_cmd",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "cmd",
                    method = "cmd/+/reboot",
                    methodType = "Duplex",
                    body = """{"ack":true,"device":"${topic.0}"}""",
                    messages = Array.Empty<string>(),
                    metadata = new Dictionary<string, string>
                    {
                        ["qos"] = "1",
                        ["responseTopic"] = "cmd/${topic.0}/ack"
                    },
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "react.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var responseTcs = new TaskCompletionSource<(string Topic, string Payload)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            // Only capture the ack, not the request echo (the broker
            // routes the client's own publish back through the
            // subscribe).
            var topic = args.ApplicationMessage.Topic;
            if (!topic.EndsWith("/ack", StringComparison.Ordinal)) return Task.CompletedTask;
            var payload = Encoding.UTF8.GetString(args.ApplicationMessage.Payload);
            responseTcs.TrySetResult((topic, payload));
            return Task.CompletedTask;
        };

        await client.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("bowire-mqtt-react-test")
                .Build(),
            TestContext.Current.CancellationToken);
        await client.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("cmd/#").Build(),
            TestContext.Current.CancellationToken);

        // Fire the request publish on a concrete topic matching the
        // recorded wildcard pattern.
        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("cmd/robot42/reboot")
            .WithPayload("{}"u8.ToArray())
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(),
            TestContext.Current.CancellationToken);

        var (respTopic, respPayload) = await responseTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Response topic has ${topic.0} replaced with "robot42".
        Assert.Equal("cmd/robot42/ack", respTopic);

        // Response body has ${topic.0} replaced too.
        using var json = JsonDocument.Parse(respPayload);
        Assert.True(json.RootElement.GetProperty("ack").GetBoolean());
        Assert.Equal("robot42", json.RootElement.GetProperty("device").GetString());

        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NonMatchingTopic_NoResponseEmitted()
    {
        // Same step as above, but the client publishes on an
        // unrelated topic that doesn't satisfy the pattern.
        var recording = new
        {
            id = "rec_mqtt_nomatch",
            name = "mqtt nomatch",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_cmd",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "cmd",
                    method = "cmd/+/reboot",
                    methodType = "Duplex",
                    body = """{"ack":true}""",
                    messages = Array.Empty<string>(),
                    metadata = new Dictionary<string, string>
                    {
                        ["responseTopic"] = "cmd/${topic.0}/ack"
                    },
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "nomatch.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var ackReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic.EndsWith("/ack", StringComparison.Ordinal))
                ackReceived.TrySetResult(true);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("bowire-mqtt-nomatch-test")
                .Build(),
            TestContext.Current.CancellationToken);
        await client.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("cmd/#").Build(),
            TestContext.Current.CancellationToken);

        // Publish on a topic that doesn't match cmd/+/reboot.
        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("cmd/robot42/status")
            .WithPayload("{}"u8.ToArray())
            .Build(),
            TestContext.Current.CancellationToken);

        // Wait briefly; no ack should arrive.
        var completed = await Task.WhenAny(ackReceived.Task, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.NotEqual(ackReceived.Task, completed);

        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Mqttv5_ResponseTopicProperty_OverridesRecordedTemplate()
    {
        // Step declares responseTopic = cmd/${topic.0}/default-ack,
        // but the client's MQTT v5 publish carries a ResponseTopic
        // property that should win.
        var recording = new
        {
            id = "rec_mqtt_v5",
            name = "mqtt v5",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_cmd",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "cmd",
                    method = "cmd/+/reboot",
                    methodType = "Duplex",
                    body = """{"ack":true}""",
                    messages = Array.Empty<string>(),
                    metadata = new Dictionary<string, string>
                    {
                        ["responseTopic"] = "cmd/${topic.0}/default-ack"
                    },
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "v5.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            var topic = args.ApplicationMessage.Topic;
            if (topic.StartsWith("cmd/client-response/", StringComparison.Ordinal))
                received.TrySetResult(topic);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("bowire-mqtt-v5-test")
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
                .Build(),
            TestContext.Current.CancellationToken);
        await client.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("cmd/#").Build(),
            TestContext.Current.CancellationToken);

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("cmd/robot/reboot")
            .WithPayload("{}"u8.ToArray())
            .WithResponseTopic("cmd/client-response/here") // MQTT v5
            .Build(),
            TestContext.Current.CancellationToken);

        var respTopic = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Client's v5 ResponseTopic wins over the step's template.
        Assert.Equal("cmd/client-response/here", respTopic);

        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }
}
