// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Mqtt;
using MQTTnet;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Integration tests for <see cref="BowireMqttProtocol"/> and the
/// internal <c>MqttBowireChannel</c> against a real (in-process) MQTT
/// broker. The mock's <see cref="MqttMockTransportHost"/> already spins
/// up an embedded broker for replay; we hijack that broker as a generic
/// MQTT endpoint so we can exercise the publish/subscribe channel and
/// the Invoke / InvokeStream paths end-to-end.
/// </summary>
public sealed class MqttBowireChannelIntegrationTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private MockServer? _server;

    public MqttBowireChannelIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mqtt-channel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Spin up the embedded broker via MockServer with a tiny
    /// reactive recording so a Duplex MQTT step is present (forces the
    /// transport host to start). Returns the bound broker port.
    /// </summary>
    private async Task<int> StartBrokerAsync(CancellationToken ct)
    {
        var recording = new
        {
            id = "rec_channel",
            name = "channel",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "noop",
                    capturedAt = 1_000L,
                    protocol = "mqtt",
                    service = "noop",
                    method = "__noop__",
                    methodType = "Duplex",
                    body = "{}",
                    messages = Array.Empty<string>(),
                    metadata = (Dictionary<string, string>?)null,
                    status = "OK",
                    durationMs = 0L,
                    response = (string?)null
                }
            }
        };

        var path = Path.Combine(_tempDir, "rec.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(recording), ct);

        _server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                TransportPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["mqtt"] = 0 },
                TransportHosts = new IBowireMockTransportHost[] { new MqttMockTransportHost() },
                Watch = false,
                ReplaySpeed = 0
            },
            ct);

        var port = _server.TransportPorts["mqtt"];
        Assert.True(port > 0, "Broker port must be assigned.");
        return port;
    }

    [Fact]
    public async Task OpenChannelAsync_ConnectsToLiveBroker_AndDeliversIncomingMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();
        await using var channel = await protocol.OpenChannelAsync(
            serverUrl: $"mqtt://127.0.0.1:{port}",
            service: "tests",
            method: "tests/echo",
            showInternalServices: false,
            metadata: null,
            ct);

        Assert.NotNull(channel);
        Assert.True(channel!.IsClientStreaming);
        Assert.True(channel.IsServerStreaming);
        Assert.False(channel.IsClosed);
        Assert.NotEmpty(channel.Id);

        // Inject a publish onto the channel's subscribe topic via a
        // second MQTTnet client; the channel should pick it up via
        // ApplicationMessageReceivedAsync and surface it in
        // ReadResponsesAsync as a JSON envelope.
        var factory = new MqttClientFactory();
        using var publisher = factory.CreateMqttClient();
        await publisher.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("mqtt-bowire-pub")
                .Build(),
            ct);

        await publisher.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("tests/echo")
            .WithPayload(Encoding.UTF8.GetBytes("""{"value":"hello"}"""))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(),
            ct);

        // Bind the read-stream to a 5s ceiling so a missing message
        // surfaces as a real test failure rather than a hang.
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromSeconds(5));
        await using var reader = channel.ReadResponsesAsync(readCts.Token).GetAsyncEnumerator(readCts.Token);
        var received = await reader.MoveNextAsync();
        Assert.True(received, "Channel must surface the incoming publish.");

        using var doc = JsonDocument.Parse(reader.Current);
        Assert.Equal("text", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("tests/echo", doc.RootElement.GetProperty("topic").GetString());
        // Payload was JSON — pretty-printed by MqttPayloadHelper.
        var payload = doc.RootElement.GetProperty("payload").GetString()!;
        Assert.Contains("\"value\"", payload, StringComparison.Ordinal);

        await publisher.DisconnectAsync(cancellationToken: ct);
    }

    [Fact]
    public async Task SendAsync_PlainPayload_PublishesToBrokerAndIncrementsCounter()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();

        // Set up a witness client subscribed on the publish topic so we
        // can confirm the channel-side publish round-trips through the
        // broker.
        var factory = new MqttClientFactory();
        using var witness = factory.CreateMqttClient();
        var received = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.ApplicationMessageReceivedAsync += args =>
        {
            received.TrySetResult(args.ApplicationMessage);
            return Task.CompletedTask;
        };
        await witness.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("mqtt-witness")
                .Build(),
            ct);
        await witness.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("send/raw").Build(),
            ct);

        await using var channel = await protocol.OpenChannelAsync(
            serverUrl: $"mqtt://127.0.0.1:{port}",
            service: "send",
            method: "send/raw",
            showInternalServices: false,
            metadata: new Dictionary<string, string> { ["qos"] = "AtMostOnce" },
            ct);

        Assert.NotNull(channel);
        Assert.Equal(0, channel!.SentCount);

        var ok = await channel.SendAsync("hello-world", ct);
        Assert.True(ok);
        Assert.Equal(1, channel.SentCount);
        Assert.True(channel.ElapsedMs >= 0);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("send/raw", msg.Topic);
        Assert.Equal("hello-world", Encoding.UTF8.GetString(msg.Payload));
    }

    [Fact]
    public async Task SendAsync_TextFrame_UnpacksInnerText()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();
        var factory = new MqttClientFactory();
        using var witness = factory.CreateMqttClient();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.ApplicationMessageReceivedAsync += args =>
        {
            received.TrySetResult(System.Buffers.BuffersExtensions.ToArray(args.ApplicationMessage.Payload));
            return Task.CompletedTask;
        };
        await witness.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("mqtt-text-witness")
                .Build(),
            ct);
        await witness.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("send/text").Build(),
            ct);

        await using var channel = await protocol.OpenChannelAsync(
            serverUrl: $"mqtt://127.0.0.1:{port}",
            service: "send",
            method: "send/text",
            showInternalServices: false,
            metadata: null,
            ct);

        Assert.NotNull(channel);
        var ok = await channel!.SendAsync("""{"type":"text","text":"unpacked"}""", ct);
        Assert.True(ok);

        var bytes = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("unpacked", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task SendAsync_BinaryFrame_DecodesBase64Payload()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();
        var factory = new MqttClientFactory();
        using var witness = factory.CreateMqttClient();
        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.ApplicationMessageReceivedAsync += args =>
        {
            received.TrySetResult(System.Buffers.BuffersExtensions.ToArray(args.ApplicationMessage.Payload));
            return Task.CompletedTask;
        };
        await witness.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("mqtt-bin-witness")
                .Build(),
            ct);
        await witness.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("send/bin").Build(),
            ct);

        await using var channel = await protocol.OpenChannelAsync(
            serverUrl: $"mqtt://127.0.0.1:{port}",
            service: "send",
            method: "send/bin",
            showInternalServices: false,
            metadata: null,
            ct);

        Assert.NotNull(channel);
        var raw = new byte[] { 0x00, 0x01, 0x02, 0xFF };
        var b64 = Convert.ToBase64String(raw);
        var ok = await channel!.SendAsync($$"""{"type":"binary","base64":"{{b64}}"}""", ct);
        Assert.True(ok);

        var bytes = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(raw, bytes);
    }

    [Fact]
    public async Task CloseAsync_StopsResponseStream_AndIsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();
        await using var channel = await protocol.OpenChannelAsync(
            serverUrl: $"mqtt://127.0.0.1:{port}",
            service: "close",
            method: "close/me",
            showInternalServices: false,
            metadata: null,
            ct);

        Assert.NotNull(channel);
        Assert.False(channel!.IsClosed);

        await channel.CloseAsync(ct);
        Assert.True(channel.IsClosed);

        // Idempotent: a second close is a no-op.
        await channel.CloseAsync(ct);
        Assert.True(channel.IsClosed);

        // After close, the response stream completes immediately.
        var any = false;
        await foreach (var _ in channel.ReadResponsesAsync(ct))
        {
            any = true;
            break;
        }
        Assert.False(any, "Closed channel must yield no responses.");

        // Send after close is a no-op returning false.
        var sent = await channel.SendAsync("post-close", ct);
        Assert.False(sent);
    }

    [Fact]
    public async Task InvokeAsync_PublishesPayloadToBroker_AndReturnsOkResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();
        var factory = new MqttClientFactory();
        using var witness = factory.CreateMqttClient();
        var received = new TaskCompletionSource<MqttApplicationMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.ApplicationMessageReceivedAsync += args =>
        {
            received.TrySetResult(args.ApplicationMessage);
            return Task.CompletedTask;
        };
        await witness.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("invoke-witness")
                .Build(),
            ct);
        await witness.SubscribeAsync(
            factory.CreateSubscribeOptionsBuilder().WithTopicFilter("invoke/topic").Build(),
            ct);

        var result = await protocol.InvokeAsync(
            serverUrl: $"mqtt://127.0.0.1:{port}",
            service: "invoke",
            method: "invoke/topic",
            jsonMessages: ["""{"v":42}"""],
            showInternalServices: false,
            metadata: new Dictionary<string, string>
            {
                ["qos"] = "AtLeastOnce",
                ["retain"] = "true"
            },
            ct);

        Assert.Equal("OK", result.Status);
        Assert.NotNull(result.Response);
        Assert.Equal("invoke/topic", result.Metadata["topic"]);
        Assert.Equal("1", result.Metadata["qos"]);
        Assert.Equal("true", result.Metadata["retain"]);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("invoke/topic", msg.Topic);
        Assert.Equal("""{"v":42}""", Encoding.UTF8.GetString(msg.Payload));
    }

    [Fact]
    public async Task InvokeAsync_DefaultPayload_WhenNoMessagesProvided()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();

        // Empty jsonMessages list → invoke falls through to "{}" default.
        var result = await protocol.InvokeAsync(
            serverUrl: $"mqtt://127.0.0.1:{port}",
            service: "default",
            method: "default/topic",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct);

        Assert.Equal("OK", result.Status);
        Assert.Contains("{}", result.Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStreamAsync_YieldsBrokerPublishesAsJsonEnvelopes()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        var protocol = new BowireMqttProtocol();
        using var perTestCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perTestCts.CancelAfter(TimeSpan.FromSeconds(10));

        // Drive a publish-injection task in the background so the stream
        // has something to yield. Wait briefly for the subscriber to
        // settle inside InvokeStreamAsync before publishing.
        _ = Task.Run(async () =>
        {
            await Task.Delay(500, perTestCts.Token);
            var factory = new MqttClientFactory();
            using var pub = factory.CreateMqttClient();
            await pub.ConnectAsync(
                factory.CreateClientOptionsBuilder()
                    .WithTcpServer("127.0.0.1", port)
                    .WithClientId("stream-pub")
                    .Build(),
                perTestCts.Token);
            await pub.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic("stream/topic")
                .WithPayload(Encoding.UTF8.GetBytes("payload-1"))
                .Build(), perTestCts.Token);
            await pub.DisconnectAsync(cancellationToken: perTestCts.Token);
        }, perTestCts.Token);

        await foreach (var envelope in protocol.InvokeStreamAsync(
                           serverUrl: $"mqtt://127.0.0.1:{port}",
                           service: "stream",
                           method: "stream/topic",
                           jsonMessages: [],
                           showInternalServices: false,
                           metadata: new Dictionary<string, string> { ["qos"] = "AtMostOnce" },
                           ct: perTestCts.Token))
        {
            using var doc = JsonDocument.Parse(envelope);
            Assert.Equal("stream/topic", doc.RootElement.GetProperty("topic").GetString());
            var payload = doc.RootElement.GetProperty("payload").GetString();
            Assert.Equal("payload-1", payload);
            // First envelope is enough — break stops the consumer's
            // foreach which trips the IAsyncEnumerable's cancellation.
            await perTestCts.CancelAsync();
            break;
        }
    }

    [Fact]
    public async Task InvokeStreamAsync_NullBrokerUrl_YieldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var protocol = new BowireMqttProtocol();
        var count = 0;
        await foreach (var _ in protocol.InvokeStreamAsync(
                           serverUrl: "",
                           service: "x", method: "x",
                           jsonMessages: [],
                           showInternalServices: false,
                           metadata: null,
                           ct: ct))
        {
            count++;
        }
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task DiscoverAsync_AgainstLiveBroker_ReturnsServicesGroupedByPrefix()
    {
        var ct = TestContext.Current.CancellationToken;
        var port = await StartBrokerAsync(ct);

        // Pre-populate a couple of retained topics so the scan window
        // sees them (broker forwards retained messages immediately on
        // subscribe).
        var factory = new MqttClientFactory();
        using var seeder = factory.CreateMqttClient();
        await seeder.ConnectAsync(
            factory.CreateClientOptionsBuilder()
                .WithTcpServer("127.0.0.1", port)
                .WithClientId("seeder")
                .Build(),
            ct);
        await seeder.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scan/temperature").WithPayload(Encoding.UTF8.GetBytes("21.5"))
            .WithRetainFlag(true).Build(), ct);
        await seeder.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic("scan/humidity").WithPayload(Encoding.UTF8.GetBytes("70"))
            .WithRetainFlag(true).Build(), ct);
        await seeder.DisconnectAsync(cancellationToken: ct);

        var protocol = new BowireMqttProtocol();
        // The default scan window inside DiscoverAsync is 3s — give it
        // plenty of headroom on slow CI hosts.
        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        scanCts.CancelAfter(TimeSpan.FromSeconds(15));
        var services = await protocol.DiscoverAsync(
            $"mqtt://127.0.0.1:{port}", showInternalServices: false, scanCts.Token);

        // Retained messages on retained topics arrive during the scan
        // → service "scan" with two methods (publish + subscribe per topic).
        var scan = services.FirstOrDefault(s => s.Name == "scan");
        Assert.NotNull(scan);
        Assert.Equal(4, scan!.Methods.Count);
        Assert.Contains(scan.Methods, m => m.Name == "scan/temperature" && m.MethodType == "Unary");
        Assert.Contains(scan.Methods, m => m.Name == "scan/humidity" && m.MethodType == "ServerStreaming");
    }
}
