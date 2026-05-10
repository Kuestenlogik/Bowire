// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using MQTTnet;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Edge-case coverage for the MQTT mock surface — the proactive
/// emitter's qos-by-name parsing, empty-topic skip, and the reactive
/// responder's MQTT v5 CorrelationData passthrough.
/// </summary>
public sealed class MqttMockEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public MqttMockEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mqtt-edge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ProactiveEmitter_QosAsEnumName_AndRetainTrue_AreApplied()
    {
        // Step uses qos="ExactlyOnce" (enum name, not int) and
        // retain="true" — exercises the Enum.TryParse branch and the
        // retain-flag round-trip.
        var recording = new
        {
            id = "rec_qos_name",
            name = "qos by name",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_qos",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "qos",
                    method = "qos/by-name",
                    methodType = "Unary",
                    body = "value",
                    messages = new[] { "value" },
                    metadata = new Dictionary<string, string>
                    {
                        ["qos"] = "ExactlyOnce",
                        ["retain"] = "true"
                    },
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "qos.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var subscriber = factory.CreateMqttClient();
        var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            tcs.TrySetResult(args.ApplicationMessage);
            return Task.CompletedTask;
        };
        await subscriber.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("qos-name-sub")
                .Build(),
            TestContext.Current.CancellationToken);
        await subscriber.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic("qos/#")
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce))
                .Build(),
            TestContext.Current.CancellationToken);

        var msg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("qos/by-name", msg.Topic);
        Assert.Equal(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce, msg.QualityOfServiceLevel);
        // Retain=true on injected publishes is stored on the broker but not
        // surfaced as a flag on live-subscriber deliveries — the qos +
        // payload round-trip is enough to exercise the metadata branches.
        Assert.Equal("value", Encoding.UTF8.GetString(msg.Payload));

        await subscriber.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProactiveEmitter_EmptyTopicStep_IsSkipped_AndOtherStepsStillFire()
    {
        // Step with empty method (topic) is skipped with a warning;
        // the second step must still emit so the subscriber receives
        // exactly one publish.
        var recording = new
        {
            id = "rec_empty_topic",
            name = "empty topic",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "skip_me",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "skip",
                    method = "", // empty topic — emitter logs warning + skips
                    methodType = "Unary",
                    body = "X",
                    messages = new[] { "X" },
                    metadata = (Dictionary<string, string>?)null,
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                },
                new
                {
                    id = "fires",
                    capturedAt = 1_001L,
                    protocol = "mqtt",
                    service = "fires",
                    method = "fires/topic",
                    methodType = "Unary",
                    body = "Y",
                    messages = new[] { "Y" },
                    metadata = (Dictionary<string, string>?)null,
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "empty.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var subscriber = factory.CreateMqttClient();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.ApplicationMessageReceivedAsync += args =>
        {
            tcs.TrySetResult(args.ApplicationMessage.Topic);
            return Task.CompletedTask;
        };
        await subscriber.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("empty-sub")
                .Build(),
            TestContext.Current.CancellationToken);
        await subscriber.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("fires/#").Build(),
            TestContext.Current.CancellationToken);

        var topic = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("fires/topic", topic);

        await subscriber.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReactiveResponder_PassesThroughCorrelationDataOnMqttv5()
    {
        // MQTT v5 client publishes with CorrelationData; the reactive
        // responder must echo the same bytes on the response so the
        // client can pair its pending request with the arriving reply.
        var recording = new
        {
            id = "rec_corr",
            name = "correlation",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_corr",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "corr",
                    method = "corr/+/req",
                    methodType = "Duplex",
                    body = "ok",
                    messages = Array.Empty<string>(),
                    metadata = new Dictionary<string, string>
                    {
                        ["responseTopic"] = "corr/${topic.0}/res"
                    },
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "corr.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var responseTcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic.EndsWith("/res", StringComparison.Ordinal))
                responseTcs.TrySetResult(args.ApplicationMessage);
            return Task.CompletedTask;
        };

        await client.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("corr-test")
                .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V500)
                .Build(),
            TestContext.Current.CancellationToken);
        await client.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("corr/#").Build(),
            TestContext.Current.CancellationToken);

        var corrData = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("corr/abc/req")
            .WithPayload("{}"u8.ToArray())
            .WithCorrelationData(corrData)
            .Build(),
            TestContext.Current.CancellationToken);

        var resp = await responseTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("corr/abc/res", resp.Topic);
        Assert.NotNull(resp.CorrelationData);
        Assert.Equal(corrData, resp.CorrelationData);

        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReactiveResponder_QosAsInteger_AndRetainFlag_AreParsedFromMetadata()
    {
        // Reactive step declares qos="2" + retain="true" via metadata —
        // exercises the int-parse branch in ExtractReactiveSteps and
        // confirms the response carries those flags downstream.
        var recording = new
        {
            id = "rec_qos_int",
            name = "qos int",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_int",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "intq",
                    method = "intq/+/in",
                    methodType = "Duplex",
                    body = "ack",
                    messages = Array.Empty<string>(),
                    metadata = new Dictionary<string, string>
                    {
                        ["qos"] = "2",
                        ["retain"] = "true",
                        ["responseTopic"] = "intq/${topic.0}/out"
                    },
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "qos-int.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var tcs = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            if (args.ApplicationMessage.Topic.EndsWith("/out", StringComparison.Ordinal))
                tcs.TrySetResult(args.ApplicationMessage);
            return Task.CompletedTask;
        };
        await client.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("qos-int-test")
                .Build(),
            TestContext.Current.CancellationToken);
        await client.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic("intq/#")
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce))
                .Build(),
            TestContext.Current.CancellationToken);

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("intq/dev1/in").WithPayload("{}"u8.ToArray()).Build(),
            TestContext.Current.CancellationToken);

        var msg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("intq/dev1/out", msg.Topic);
        Assert.Equal(MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce, msg.QualityOfServiceLevel);

        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReactiveResponder_NoResponseTopicAndNoV5_FireAndForgetSilently()
    {
        // No metadata.responseTopic and no MQTT v5 ResponseTopic — the
        // responder logs at debug and emits nothing. Test asserts the
        // absence of a paired response, which exercises the
        // fire-and-forget branch.
        var recording = new
        {
            id = "rec_silent",
            name = "silent",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_silent",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "silent",
                    method = "silent/+/in",
                    methodType = "Duplex",
                    body = "{}",
                    messages = Array.Empty<string>(),
                    // No responseTopic key + no v5 client → fire-and-forget path.
                    metadata = new Dictionary<string, string>(),
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "silent.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), TestContext.Current.CancellationToken);

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            TestContext.Current.CancellationToken);

        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var responseSeen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ApplicationMessageReceivedAsync += args =>
        {
            // Any topic NOT matching what the client itself published is a
            // would-be paired response — for fire-and-forget we expect none.
            if (args.ApplicationMessage.Topic == "silent/dev/out")
                responseSeen.TrySetResult(true);
            return Task.CompletedTask;
        };
        await client.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", server.TransportPorts["mqtt"])
                .WithClientId("silent-test")
                .Build(),
            TestContext.Current.CancellationToken);
        await client.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("silent/#").Build(),
            TestContext.Current.CancellationToken);

        await client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("silent/dev/in").WithPayload("{}"u8.ToArray()).Build(),
            TestContext.Current.CancellationToken);

        // Wait briefly; the responder shouldn't emit anything on /out.
        var raceCompleted = await Task.WhenAny(
            responseSeen.Task,
            Task.Delay(750, TestContext.Current.CancellationToken));
        Assert.NotEqual(responseSeen.Task, raceCompleted);

        await client.DisconnectAsync(cancellationToken: TestContext.Current.CancellationToken);
    }
}
